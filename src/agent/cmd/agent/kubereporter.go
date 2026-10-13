// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package main

import (
	"context"
	"log/slog"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/kubereporter"
)

// newReporterStateHook builds what runs after every successful reporter enrol, unenrol or uninstall: the local
// Kubernetes report is collected at once, so the server sees the change within one send instead of after the
// collector's next scheduled run. A paused host queues nothing, so the hook does nothing while paused.
func newReporterStateHook(collect func(ctx context.Context) error, paused func() bool) func(ctx context.Context) {
	return func(ctx context.Context) {
		if paused() {
			slog.Debug("host is paused, not refreshing the Kubernetes node report after a reporter change")

			return
		}

		if err := collect(ctx); err != nil {
			slog.Warn("could not refresh the Kubernetes node report after a reporter change", "error", err)
		}
	}
}

// newSnapshotFence builds what runs when this host stops being the holder of its cluster's reporter Lease, and
// when the reporter starts on an enrolled host: every cluster snapshot in the queue, waiting or taken by the
// sender, is deleted, so a snapshot of a tenure that has ended is not sent later. A snapshot already on the wire
// or in an envelope the sender has built cannot be recalled; the server's per-epoch fence rejects those.
func newSnapshotFence(deleteByType func(db.TelemetryType) (int64, error)) func() {
	return func() {
		deleted, err := deleteByType(db.TelemetryKubernetesCluster)
		if err != nil {
			slog.Warn("could not drop the queued cluster snapshots after losing the reporter lease", "error", err)

			return
		}

		if deleted > 0 {
			slog.Info("dropped the queued cluster snapshots after losing the reporter lease", "count", deleted)
		}
	}
}

// newReporterGate builds the agent-side inputs of the reporter's health gate: whether the host is paused, when
// the server last acknowledged a batch, when the oldest undelivered cluster snapshot was queued, how often the
// slow telemetry tier is sent (which sets how long a snapshot may wait), and when the agent started counting
// from. The count of error snapshots in a row is the runtime's own and is not set here.
func newReporterGate(paused func() bool, lastAck func() time.Time, oldestPendingSnapshot func() (time.Time, bool), slowSendInterval func() time.Duration, startedAt time.Time) kubereporter.Gate {
	return kubereporter.Gate{
		Paused:                paused,
		LastAck:               lastAck,
		StartedAt:             startedAt,
		OldestPendingSnapshot: oldestPendingSnapshot,
		SlowSendInterval:      slowSendInterval,
	}
}

// newOldestPendingSnapshot builds the gate's view of the queue: when the oldest cluster snapshot that is pending
// or being sent began waiting. A snapshot that replaces an undelivered one inherits its time, so snapshots that
// keep being replaced without getting through still age. A queue that cannot be read reports none, so a database
// fault cannot make the holder give up the Lease; it is logged.
func newOldestPendingSnapshot(oldestQueued func(db.TelemetryType) (time.Time, bool, error)) func() (time.Time, bool) {
	return func() (time.Time, bool) {
		created, found, err := oldestQueued(db.TelemetryKubernetesCluster)
		if err != nil {
			slog.Warn("could not read the age of the queued cluster snapshot", "error", err)

			return time.Time{}, false
		}

		return created, found
	}
}
