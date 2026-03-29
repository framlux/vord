// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"log/slog"
	"runtime/debug"
)

// safeRun executes fn and recovers from any panic, logging the panic and stack trace.
func safeRun(name string, fn func()) {
	defer func() {
		if r := recover(); r != nil {
			slog.Error("collection panicked",
				"collector", name,
				"panic", r,
				"stack", string(debug.Stack()),
			)
		}
	}()
	fn()
}
