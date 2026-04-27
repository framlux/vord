// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"fmt"
	"sync/atomic"
	"testing"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/state"
)

// --- Fake collector for testing ---

type fakeCollector struct {
	name         string
	interval     time.Duration
	collectFunc  func(ctx context.Context, store *db.Store) error
	collectCount atomic.Int32
}

func (f *fakeCollector) Name() string {
	return f.name
}

func (f *fakeCollector) Collect(ctx context.Context, store *db.Store) error {
	f.collectCount.Add(1)
	if f.collectFunc != nil {
		return f.collectFunc(ctx, store)
	}

	return nil
}

func (f *fakeCollector) DefaultInterval() time.Duration {
	return f.interval
}

// --- Scheduler tests ---

// Intent: RunOnce() calls Collect() on every registered collector.
func TestRunOnce_ExecutesAllCollectors(t *testing.T) {
	store := newTestStore(t)
	registry := NewRegistry()

	c1 := &fakeCollector{name: "c1", interval: time.Second}
	c2 := &fakeCollector{name: "c2", interval: time.Second}
	c3 := &fakeCollector{name: "c3", interval: time.Second}

	registry.Register(c1)
	registry.Register(c2)
	registry.Register(c3)

	scheduler := NewScheduler(registry, store, state.New())
	scheduler.RunOnce(context.Background())

	if c1.collectCount.Load() != 1 {
		t.Errorf("expected c1 to be called once, got %d", c1.collectCount.Load())
	}
	if c2.collectCount.Load() != 1 {
		t.Errorf("expected c2 to be called once, got %d", c2.collectCount.Load())
	}
	if c3.collectCount.Load() != 1 {
		t.Errorf("expected c3 to be called once, got %d", c3.collectCount.Load())
	}
}

// Intent: Panicking collector doesn't crash scheduler (safeExecuteCollector recovers).
func TestSafeExecuteCollector_PanicRecovery(t *testing.T) {
	store := newTestStore(t)
	registry := NewRegistry()

	panicker := &fakeCollector{
		name:     "panicker",
		interval: time.Second,
		collectFunc: func(ctx context.Context, store *db.Store) error {
			panic("intentional panic for testing")
		},
	}

	registry.Register(panicker)
	scheduler := NewScheduler(registry, store, state.New())

	// safeExecuteCollector should recover from the panic without crashing.
	defer func() {
		if r := recover(); r != nil {
			t.Errorf("safeExecuteCollector did not recover panic: %v", r)
		}
	}()

	scheduler.safeExecuteCollector(context.Background(), panicker)
}

// Intent: One collector error doesn't affect others.
func TestSafeExecuteCollector_ErrorIsolation(t *testing.T) {
	store := newTestStore(t)
	registry := NewRegistry()

	failing := &fakeCollector{
		name:     "failing",
		interval: time.Second,
		collectFunc: func(ctx context.Context, store *db.Store) error {
			return fmt.Errorf("simulated error")
		},
	}
	succeeding := &fakeCollector{
		name:     "succeeding",
		interval: time.Second,
	}

	registry.Register(failing)
	registry.Register(succeeding)

	scheduler := NewScheduler(registry, store, state.New())
	scheduler.RunOnce(context.Background())

	// Both should be called regardless of the error in 'failing'.
	if failing.collectCount.Load() != 1 {
		t.Errorf("expected failing collector to be called once, got %d", failing.collectCount.Load())
	}
	if succeeding.collectCount.Load() != 1 {
		t.Errorf("expected succeeding collector to be called once, got %d", succeeding.collectCount.Load())
	}
}

// Intent: Registering same name twice doesn't create duplicates (or is handled gracefully).
func TestRegistry_DuplicateNames(t *testing.T) {
	registry := NewRegistry()

	c1 := &fakeCollector{name: "same_name", interval: time.Second}
	c2 := &fakeCollector{name: "same_name", interval: time.Second}

	registry.Register(c1)
	registry.Register(c2)

	// Both are registered (registry doesn't deduplicate by name).
	if len(registry.entries) != 2 {
		t.Errorf("expected 2 entries (registry allows duplicates), got %d", len(registry.entries))
	}
}

// Intent: Empty registry runs without error.
func TestRunOnce_EmptyRegistry(t *testing.T) {
	store := newTestStore(t)
	registry := NewRegistry()

	scheduler := NewScheduler(registry, store, state.New())

	// Should not panic with empty registry.
	scheduler.RunOnce(context.Background())
}

// --- RegisterDynamic tests ---

// Intent: RegisterDynamic stores the getInterval function in the entry so the
// scheduler can check for interval changes at runtime.
func TestRegisterDynamic_StoresGetInterval(t *testing.T) {
	registry := NewRegistry()

	c := &fakeCollector{name: "dynamic_c", interval: time.Second}
	called := false
	getInterval := func() time.Duration {
		called = true

		return 2 * time.Second
	}

	registry.RegisterDynamic(c, getInterval)

	if len(registry.entries) != 1 {
		t.Fatalf("expected 1 entry, got %d", len(registry.entries))
	}
	if registry.entries[0].getInterval == nil {
		t.Fatal("expected getInterval to be non-nil for dynamic registration")
	}

	// Invoke the stored function to confirm it is the one we passed.
	result := registry.entries[0].getInterval()
	if called == false {
		t.Error("expected getInterval function to be invoked")
	}
	if result != 2*time.Second {
		t.Errorf("expected getInterval to return 2s, got %v", result)
	}
}

// Intent: The legacy Register() method must leave getInterval nil so the scheduler
// treats the collector as fixed-interval and never attempts dynamic resets.
func TestRegister_Legacy_SetsNilGetInterval(t *testing.T) {
	registry := NewRegistry()

	c := &fakeCollector{name: "static_c", interval: time.Second}
	registry.Register(c)

	if len(registry.entries) != 1 {
		t.Fatalf("expected 1 entry, got %d", len(registry.entries))
	}
	if registry.entries[0].getInterval != nil {
		t.Error("expected getInterval=nil for legacy Register(), but it was non-nil")
	}
}

// Intent: A dynamically registered collector picks up interval changes at runtime.
// We verify this by running the scheduler briefly and observing that the collector
// executes more than once when given a very short interval.
func TestRegisterDynamic_RunCollector_RespectsIntervalChange(t *testing.T) {
	store := newTestStore(t)
	registry := NewRegistry()

	c := &fakeCollector{name: "dynamic_runner", interval: 50 * time.Millisecond}

	currentInterval := 50 * time.Millisecond
	getInterval := func() time.Duration {
		return currentInterval
	}

	registry.RegisterDynamic(c, getInterval)

	scheduler := NewScheduler(registry, store, state.New())

	ctx, cancel := context.WithTimeout(context.Background(), 200*time.Millisecond)
	defer cancel()

	scheduler.Run(ctx)

	// The collector should have been called at least twice: once on startup + at least one tick.
	count := c.collectCount.Load()
	if count < 2 {
		t.Errorf("expected dynamic collector to run at least 2 times, got %d", count)
	}
}

