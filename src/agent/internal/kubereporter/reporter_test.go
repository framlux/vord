// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"testing"
)

// Intent: The state-changed hook runs on a live context of its own with a deadline, so a command that has used
// up or lost its own context still refreshes the server's view of this host.
func TestStateChanged_HookGetsALiveBoundedContext(t *testing.T) {
	var hookErr error

	var hasDeadline bool

	reporter := New(Options{OnStateChanged: func(ctx context.Context) {
		hookErr = ctx.Err()
		_, hasDeadline = ctx.Deadline()
	}})

	parent, cancel := context.WithCancel(context.Background())
	cancel()

	reporter.stateChanged(parent)

	if hookErr != nil {
		t.Errorf("hook context is already done: %v", hookErr)
	}

	if hasDeadline == false {
		t.Error("hook context has no deadline")
	}
}

// Intent: With no hook and no releaser configured, a Reporter still works: defaults are no-ops.
func TestNew_DefaultsAreNoOps(t *testing.T) {
	reporter := New(Options{DataDir: t.TempDir()})

	reporter.stateChanged(context.Background())
	reporter.releaser.ReleaseAndStop(context.Background())

	result := reporter.Unenrol(context.Background())
	if result.Success == false || result.Message != messageNotEnrolled {
		t.Errorf("unenrol of a host that was never enrolled = %+v", result)
	}
}
