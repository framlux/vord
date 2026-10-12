// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package main

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/framlux/vord/internal/db"
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

// Intent: losing the Lease drops every cluster snapshot this host queued, and only those, in the real queue.
func TestSnapshotFence_DeletesQueuedClusterSnapshots(t *testing.T) {
	database, err := db.Open(":memory:")
	if err != nil {
		t.Fatalf("open database: %v", err)
	}

	t.Cleanup(func() { database.Close() })

	store := db.NewStore(database, 0)

	for id, itemType := range map[string]db.TelemetryType{
		"snapshot": db.TelemetryKubernetesCluster,
		"node":     db.TelemetryKubernetesNode,
		"cpu":      db.TelemetryCpuUsage,
	} {
		if err := store.EnqueueTelemetry(id, itemType, `{}`); err != nil {
			t.Fatalf("enqueue %s: %v", id, err)
		}
	}

	newSnapshotFence(store.DeleteTelemetryByType)()

	pending, err := store.CountPendingTelemetry()
	if err != nil {
		t.Fatalf("CountPendingTelemetry: %v", err)
	}

	if pending != 2 {
		t.Errorf("pending items after the fence = %d, want 2 (the node report and the cpu sample)", pending)
	}

	left, err := store.DequeueTelemetryByTypes([]db.TelemetryType{db.TelemetryKubernetesCluster}, 10)
	if err != nil || len(left) != 0 {
		t.Errorf("cluster snapshots left = %d (%v), want none", len(left), err)
	}
}

// Intent: the fence deletes the cluster snapshot type and nothing else, whatever the queue holds.
func TestSnapshotFence_NamesTheClusterSnapshotType(t *testing.T) {
	var deleted []db.TelemetryType

	newSnapshotFence(func(itemType db.TelemetryType) (int64, error) {
		deleted = append(deleted, itemType)

		return 0, nil
	})()

	if len(deleted) != 1 || deleted[0] != db.TelemetryKubernetesCluster {
		t.Errorf("deleted types = %v, want only %d", deleted, db.TelemetryKubernetesCluster)
	}
}

// Intent: a failed delete is logged and never escapes, because the fence runs inside the election.
func TestSnapshotFence_DeleteFailureDoesNotPanic(t *testing.T) {
	newSnapshotFence(func(db.TelemetryType) (int64, error) { return 0, errors.New("disk full") })()
}

// Intent: the gate's three agent-side inputs are the ones the agent owns: the pause state, the sender's last
// acknowledgement and the moment the reporter started counting from.
func TestNewReporterGate_UsesTheAgentsOwnInputs(t *testing.T) {
	started := time.Date(2026, 10, 11, 12, 0, 0, 0, time.UTC)
	acked := started.Add(time.Minute)
	paused := false

	gate := newReporterGate(func() bool { return paused }, func() time.Time { return acked }, started)

	if gate.Paused() {
		t.Error("Paused() = true for a host that is not paused")
	}

	paused = true

	if gate.Paused() == false {
		t.Error("Paused() = false for a paused host")
	}

	if got := gate.LastAck(); got.Equal(acked) == false {
		t.Errorf("LastAck() = %v, want %v", got, acked)
	}

	if gate.StartedAt.Equal(started) == false {
		t.Errorf("StartedAt = %v, want %v", gate.StartedAt, started)
	}
}
