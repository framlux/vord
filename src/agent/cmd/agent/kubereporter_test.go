// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package main

import (
	"context"
	"errors"
	"testing"
)

// Intent: A reporter state change collects the local Kubernetes report immediately.
func TestReporterStateHook_CollectsAtOnce(t *testing.T) {
	collected := 0
	hook := newReporterStateHook(func(context.Context) error { collected++; return nil }, func() bool { return false })

	hook(context.Background())

	if collected != 1 {
		t.Errorf("collected %d times, want 1", collected)
	}
}

// Intent: A paused host queues nothing, so the hook leaves the report to the scheduler.
func TestReporterStateHook_PausedHostCollectsNothing(t *testing.T) {
	collected := 0
	hook := newReporterStateHook(func(context.Context) error { collected++; return nil }, func() bool { return true })

	hook(context.Background())

	if collected != 0 {
		t.Errorf("collected %d times while paused, want 0", collected)
	}
}

// Intent: A collection failure is logged and never escapes the hook, which runs inside a command.
func TestReporterStateHook_CollectFailureDoesNotPanic(t *testing.T) {
	hook := newReporterStateHook(func(context.Context) error { return errors.New("boom") }, func() bool { return false })

	hook(context.Background())
}
