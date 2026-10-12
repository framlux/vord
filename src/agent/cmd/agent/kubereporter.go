// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package main

import (
	"context"
	"log/slog"
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
