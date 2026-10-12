// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"strings"
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

// Intent: Only the reasons that need an explanation carry one. Whatever detail a caller passes, every other
// reason is sent as its bare code, so cluster text cannot slip into a message by a new call site.
func TestNewFailure_OnlySanctionedReasonsCarryDetail(t *testing.T) {
	withDetail := map[string]bool{reasonAPIUnreachable: true, reasonNoAdminCredential: true, reasonNameConflict: true}

	codes := []string{
		reasonNoAdminCredential, reasonNotControlPlane, reasonAPIUnreachable, reasonRBACDenied, reasonNameConflict,
		reasonNamespaceTerminating, reasonTokenNotPopulated, reasonCredentialsRejected, reasonTLSCAChanged, reasonStateWriteFailed,
	}

	for _, code := range codes {
		message := newFailure(code, "some explanation", nil).result().Message

		if withDetail[code] {
			if message != code+": some explanation" {
				t.Errorf("%s: message = %q, want the explanation kept", code, message)
			}
		} else if message != code {
			t.Errorf("%s: message = %q, want the bare code", code, message)
		}
	}
}

// Intent: Explanations are one line of printable text, bounded in length.
func TestNewFailure_DetailIsSanitisedAndBounded(t *testing.T) {
	message := newFailure(reasonAPIUnreachable, "line one\nline two\x1b[31m"+strings.Repeat("x", 1000), nil).result().Message

	if strings.ContainsAny(message, "\n\x1b") {
		t.Errorf("message holds control characters: %q", message)
	}

	if len(message) > len(reasonAPIUnreachable)+2+maxDetailBytes+3 {
		t.Errorf("message is %d bytes", len(message))
	}
}
