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

// Intent: the gate's agent-side inputs are the ones the agent owns: the pause state, the sender's last
// acknowledgement, the age of the oldest undelivered cluster snapshot and the moment the reporter started
// counting from.
func TestNewReporterGate_UsesTheAgentsOwnInputs(t *testing.T) {
	started := time.Date(2026, 10, 11, 12, 0, 0, 0, time.UTC)
	acked := started.Add(time.Minute)
	queued := started.Add(2 * time.Minute)
	paused := false

	slowSend := 7 * time.Minute

	gate := newReporterGate(func() bool { return paused }, func() time.Time { return acked }, func() (time.Time, bool) { return queued, true },
		func() time.Duration { return slowSend }, started)

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

	if oldest, pending := gate.OldestPendingSnapshot(); (pending == false) || oldest.Equal(queued) == false {
		t.Errorf("OldestPendingSnapshot() = %v, %v; want %v, true", oldest, pending, queued)
	}

	if got := gate.SlowSendInterval(); got != slowSend {
		t.Errorf("SlowSendInterval() = %v, want %v", got, slowSend)
	}
}

// Intent: the gate's question about a snapshot that is stuck is answered from the real queue: the creation time
// of the oldest cluster snapshot that is pending or being sent, and nothing when the queue holds none, whatever
// else it holds.
func TestOldestPendingSnapshot_ReadsTheRealQueue(t *testing.T) {
	database, err := db.Open(":memory:")
	if err != nil {
		t.Fatalf("open database: %v", err)
	}

	t.Cleanup(func() { database.Close() })

	store := db.NewStore(database, 0)
	oldest := newOldestPendingSnapshot(store.OldestQueuedTelemetry)

	if _, pending := oldest(); pending {
		t.Fatal("an empty queue reports a pending snapshot")
	}

	if err := store.EnqueueTelemetry("node", db.TelemetryKubernetesNode, `{}`); err != nil {
		t.Fatalf("enqueue: %v", err)
	}

	if _, pending := oldest(); pending {
		t.Fatal("a queue holding only a node report reports a pending snapshot")
	}

	if err := store.ReplaceTelemetryOfType("snapshot", db.TelemetryKubernetesCluster, `{}`); err != nil {
		t.Fatalf("replace: %v", err)
	}

	if _, err := store.DB().Exec("UPDATE telemetry_queue SET created_at = '2026-10-11T11:00:00Z' WHERE id = 'snapshot'"); err != nil {
		t.Fatalf("set created_at: %v", err)
	}

	created, pending := oldest()
	if want := time.Date(2026, 10, 11, 11, 0, 0, 0, time.UTC); (pending == false) || created.Equal(want) == false {
		t.Fatalf("oldest = %v, %v; want %v, true", created, pending, want)
	}

	if _, err := store.DequeueTelemetryByTypes([]db.TelemetryType{db.TelemetryKubernetesCluster}, 1); err != nil {
		t.Fatalf("dequeue: %v", err)
	}

	if _, pending := oldest(); pending == false {
		t.Error("a snapshot the sender holds no longer counts as undelivered")
	}

	if err := store.MarkTelemetryCompleted([]string{"snapshot"}); err != nil {
		t.Fatalf("complete: %v", err)
	}

	if _, pending := oldest(); pending {
		t.Error("a delivered snapshot still counts as undelivered")
	}
}

// Intent: a queue that cannot be read must not stop the Lease being held: the gate is told nothing is pending,
// and the failure is logged.
func TestOldestPendingSnapshot_AReadFailureReportsNothingPending(t *testing.T) {
	oldest := newOldestPendingSnapshot(func(itemType db.TelemetryType) (time.Time, bool, error) {
		if itemType != db.TelemetryKubernetesCluster {
			t.Errorf("asked for type %d, want the cluster snapshot type", itemType)
		}

		return time.Date(2026, 10, 11, 11, 0, 0, 0, time.UTC), true, errors.New("database is locked")
	})

	if created, pending := oldest(); pending || created.IsZero() == false {
		t.Errorf("oldest = %v, %v; want nothing pending", created, pending)
	}
}
