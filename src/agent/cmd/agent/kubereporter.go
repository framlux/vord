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

// newSnapshotFence builds what runs when this host stops being the holder of its cluster's reporter Lease: every
// cluster snapshot it queued, waiting or already taken by the sender, is deleted, so a snapshot from a tenure that
// has ended is never sent after another agent has taken over.
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
// the server last acknowledged a batch, and when the agent started counting from.
func newReporterGate(paused func() bool, lastAck func() time.Time, startedAt time.Time) kubereporter.Gate {
	return kubereporter.Gate{
		Paused:    paused,
		LastAck:   lastAck,
		StartedAt: startedAt,
	}
}
