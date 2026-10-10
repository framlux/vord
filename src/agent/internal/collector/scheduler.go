// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"log/slog"
	"runtime/debug"
	"sync"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/pause"
	"github.com/framlux/vord/internal/state"
)

// Scheduler runs registered collectors on their configured intervals,
// alongside the grouped FastTick and SlowTick goroutines.
type Scheduler struct {
	registry *Registry
	store    *db.Store
	pause    *pause.Coordinator
	fastTick *FastTick
	slowTick *SlowTick
}

// NewScheduler creates a new Scheduler. While the pause coordinator reports the host paused, no collector,
// including the grouped fast and slow ticks, collects anything.
func NewScheduler(registry *Registry, store *db.Store, rs *state.RuntimeState, pc *pause.Coordinator) *Scheduler {
	return &Scheduler{
		registry: registry,
		store:    store,
		pause:    pc,
		fastTick: NewFastTick(store, rs, pc),
		slowTick: NewSlowTick(store, rs, pc),
	}
}

// Run starts the grouped tick goroutines and all independent collectors
// on their intervals. It blocks until ctx is cancelled.
func (s *Scheduler) Run(ctx context.Context) {
	var wg sync.WaitGroup

	// Start fast tick goroutine.
	wg.Add(1)
	go func() {
		defer wg.Done()
		defer func() {
			if r := recover(); r != nil {
				slog.Error("fast tick goroutine panicked",
					"panic", r,
					"stack", string(debug.Stack()),
				)
			}
		}()
		s.fastTick.Run(ctx)
	}()

	// Start slow tick goroutine.
	wg.Add(1)
	go func() {
		defer wg.Done()
		defer func() {
			if r := recover(); r != nil {
				slog.Error("slow tick goroutine panicked",
					"panic", r,
					"stack", string(debug.Stack()),
				)
			}
		}()
		s.slowTick.Run(ctx)
	}()

	// Start independent collector goroutines.
	for _, e := range s.registry.entries {
		wg.Add(1)
		go func(c Collector, interval time.Duration, getInterval func() time.Duration) {
			defer wg.Done()
			defer func() {
				if r := recover(); r != nil {
					slog.Error("collector goroutine panicked",
						"collector", c.Name(),
						"panic", r,
						"stack", string(debug.Stack()),
					)
				}
			}()
			s.runCollector(ctx, c, interval, getInterval)
		}(e.collector, e.interval, e.getInterval)
	}

	wg.Wait()
}

func (s *Scheduler) runCollector(ctx context.Context, c Collector, interval time.Duration, getInterval func() time.Duration) {
	name := c.Name()
	slog.Info("starting collector", "name", name, "interval", interval.String())

	// Run immediately on startup.
	s.safeExecuteCollector(ctx, c)

	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			slog.Info("stopping collector", "name", name)

			return
		case <-ticker.C:
			s.safeExecuteCollector(ctx, c)

			if getInterval != nil {
				if newInterval := getInterval(); newInterval != interval {
					slog.Info("collector interval changed", "name", name, "old", interval.String(), "new", newInterval.String())
					interval = newInterval
					ticker.Reset(interval)
				}
			}
		}
	}
}

// safeExecuteCollector wraps executeCollector with panic recovery so that a
// panicking collector does not kill the goroutine permanently. The collector
// will simply skip this tick and run again on the next one.
func (s *Scheduler) safeExecuteCollector(ctx context.Context, c Collector) {
	defer func() {
		if r := recover(); r != nil {
			slog.Error("collector panicked, will retry on next tick",
				"collector", c.Name(),
				"panic", r,
				"stack", string(debug.Stack()),
			)
		}
	}()
	s.executeCollector(ctx, c)
}

func (s *Scheduler) executeCollector(ctx context.Context, c Collector) {
	name := c.Name()

	// A paused host collects nothing: the server would discard it, and collecting would advance cursors past
	// events that should be skipped on resume anyway.
	if s.pause.Paused() {
		slog.Debug("collector skipped, host is paused", "name", name)

		return
	}

	start := time.Now()

	if err := c.Collect(ctx, s.store); err != nil {
		slog.Error("collector failed", "name", name, "error", err, "duration", time.Since(start))

		return
	}

	slog.Debug("collector completed", "name", name, "duration", time.Since(start))
}

// RunOnce executes all independent collectors a single time (useful for testing).
func (s *Scheduler) RunOnce(ctx context.Context) {
	for _, e := range s.registry.entries {
		s.executeCollector(ctx, e.collector)
	}
}
