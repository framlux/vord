// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package main

import (
	"context"
	"fmt"
	"testing"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/pause"
)

func newTestStore(t *testing.T) *db.Store {
	t.Helper()
	database, err := db.Open(":memory:")
	if err != nil {
		t.Fatalf("open test db: %v", err)
	}
	t.Cleanup(func() { database.Close() })

	return db.NewStore(database, 0)
}

// fakeResets stands in for the SSH collector and the sender, recording what the hooks asked of them.
type fakeResets struct {
	streamsClosed chan struct{}
	sshResets     int
	sshStore      *db.Store
	sshErr        error
}

func newFakeResets() *fakeResets {
	return &fakeResets{streamsClosed: make(chan struct{}, 8)}
}

func (f *fakeResets) closeStreams() { f.streamsClosed <- struct{}{} }

func (f *fakeResets) resetSSH(_ context.Context, store *db.Store) error {
	f.sshResets++
	f.sshStore = store

	return f.sshErr
}

func queueRows(t *testing.T, store *db.Store) int {
	t.Helper()
	var n int
	if err := store.DB().QueryRow("SELECT COUNT(*) FROM telemetry_queue").Scan(&n); err != nil {
		t.Fatalf("count queue: %v", err)
	}

	return n
}

func enqueueAll(t *testing.T, store *db.Store, ids ...string) {
	t.Helper()
	for _, id := range ids {
		if err := store.EnqueueTelemetry(id, db.TelemetryCpuUsage, `{}`); err != nil {
			t.Fatalf("EnqueueTelemetry %s: %v", id, err)
		}
	}
}

func saveCpuTicks(t *testing.T, store *db.Store) {
	t.Helper()
	ticks := `{"user":100,"idle":900}`
	if err := store.SaveCollectorState("cpu_usage", &ticks); err != nil {
		t.Fatalf("SaveCollectorState: %v", err)
	}
}

func cpuStateIsNil(t *testing.T, store *db.Store) bool {
	t.Helper()
	cs, err := store.GetCollectorState("cpu_usage")
	if err != nil {
		t.Fatalf("GetCollectorState: %v", err)
	}
	if cs == nil {
		t.Fatal("expected a cpu_usage collector_state row (with nil state), found none")
	}

	return cs.StateJSON == nil
}

// Intent: entering a pause empties the local queue, in every status, and closes the telemetry streams.
func TestPauseHooks_OnPauseClearsQueueAndClosesStreams(t *testing.T) {
	store := newTestStore(t)
	enqueueAll(t, store, "a", "b", "c")
	if _, err := store.DequeueTelemetry(1); err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	fake := newFakeResets()
	hooks := newPauseHooks(context.Background(), store, fake.closeStreams, fake.resetSSH)

	hooks.OnPause(pause.HostPaused)

	if n := queueRows(t, store); n != 0 {
		t.Errorf("expected an empty queue after the pause, got %d row(s)", n)
	}
	if n, err := store.CountPendingTelemetry(); err != nil || n != 0 {
		t.Errorf("expected 0 pending after the pause, got %d (err %v)", n, err)
	}
	<-fake.streamsClosed
}

// Intent: a queue that cannot be cleared does not stop the streams being closed.
func TestPauseHooks_OnPauseClosesStreamsEvenWhenClearFails(t *testing.T) {
	store := newTestStore(t)
	fake := newFakeResets()
	hooks := newPauseHooks(context.Background(), store, fake.closeStreams, fake.resetSSH)
	if err := store.Close(); err != nil {
		t.Fatalf("Close: %v", err)
	}

	hooks.OnPause(pause.SubscriptionInactive)

	<-fake.streamsClosed
}

// Intent: resuming moves the SSH collection point to now and clears the CPU baseline, so neither the pause's
// events nor its idle gap are replayed.
func TestPauseHooks_OnResumeResetsSSHAndCpuBaseline(t *testing.T) {
	store := newTestStore(t)
	saveCpuTicks(t, store)
	fake := newFakeResets()
	hooks := newPauseHooks(context.Background(), store, fake.closeStreams, fake.resetSSH)

	hooks.OnResume()

	if fake.sshResets != 1 {
		t.Errorf("expected one SSH reset, got %d", fake.sshResets)
	}
	if fake.sshStore != store {
		t.Error("expected the SSH reset to receive the agent's store")
	}
	if cpuStateIsNil(t, store) == false {
		t.Error("expected the cpu_usage collector state to be nil so the next sample becomes the baseline")
	}
}

// Intent: an SSH reset that fails does not leave the CPU baseline stale; each reset is attempted on its own.
func TestPauseHooks_OnResumeResetsCpuBaselineEvenWhenSSHResetFails(t *testing.T) {
	store := newTestStore(t)
	saveCpuTicks(t, store)
	fake := newFakeResets()
	fake.sshErr = fmt.Errorf("auth log unreadable")
	hooks := newPauseHooks(context.Background(), store, fake.closeStreams, fake.resetSSH)

	hooks.OnResume()

	if cpuStateIsNil(t, store) == false {
		t.Error("expected the cpu_usage collector state to be reset despite the SSH failure")
	}
}

// Intent: wired into a real coordinator, a pause empties the queue and a resume resets the collectors, once each.
func TestPauseHooks_DriveACoordinatorThroughPauseAndResume(t *testing.T) {
	store := newTestStore(t)
	enqueueAll(t, store, "x", "y")
	saveCpuTicks(t, store)
	fake := newFakeResets()
	coord := pause.New(time.Now, func() time.Duration { return time.Hour },
		newPauseHooks(context.Background(), store, fake.closeStreams, fake.resetSSH))

	coord.Observe(pause.HostPaused)
	coord.Observe(pause.HostPaused)

	if n := queueRows(t, store); n != 0 {
		t.Errorf("expected the pause to clear the queue, got %d row(s)", n)
	}
	<-fake.streamsClosed
	if fake.sshResets != 0 {
		t.Errorf("expected no SSH reset while paused, got %d", fake.sshResets)
	}

	coord.Observe(pause.None)

	if fake.sshResets != 1 {
		t.Errorf("expected one SSH reset on resume, got %d", fake.sshResets)
	}
	if cpuStateIsNil(t, store) == false {
		t.Error("expected the CPU baseline to be cleared on resume")
	}
	if coord.Paused() {
		t.Error("expected the host to be running again")
	}
}
