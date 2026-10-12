// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"net/http"
	"testing"
)

// Intent: Unenrol hands the Lease back first, then deletes this candidate's token Secret and its state file. It
// touches no shared object and no other candidate's Secret.
func TestUnenrol_RemovesOnlyOwnSecret_ReleasesLeaseIfHolder(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	own := h.secretPath()
	other := h.seedSecret("bbbbbbbbbbbbbbbb", true)
	foreign := h.seedSecret("cccccccccccccccc", false)

	versions := map[string]string{}
	for _, path := range sharedObjectPaths() {
		versions[path] = h.api.resourceVersion(path)
	}

	before := len(h.api.requestLog())
	issuedBefore := h.api.issuedTokens()

	result := h.reporter.Unenrol(context.Background())

	requireSuccess(t, result)

	if h.releaser.calls != 1 {
		t.Fatalf("lease releaser ran %d times, want 1", h.releaser.calls)
	}

	if h.releaser.atRequestLen != before {
		t.Errorf("the lease was released after %d requests, want before the first of this command (%d)", h.releaser.atRequestLen, before)
	}

	if h.api.has(own) {
		t.Error("the candidate's own Secret was not deleted")
	}

	if h.api.has(other) == false {
		t.Error("another candidate's Secret was deleted")
	}

	if h.api.has(foreign) == false {
		t.Error("an unlabelled Secret was deleted")
	}

	for _, write := range h.api.writesSince(before) {
		if (write.Method != http.MethodDelete) || (write.Path != own) {
			t.Errorf("unexpected write %s %s", write.Method, write.Path)
		}
	}

	for _, path := range sharedObjectPaths() {
		if got := h.api.resourceVersion(path); got != versions[path] {
			t.Errorf("shared object %s changed", path)
		}
	}

	if h.hasState() {
		t.Error("the state file was not deleted")
	}

	if len(issuedBefore) != 1 || len(h.api.issuedTokens()) != 0 {
		t.Errorf("tokens before=%d after=%d, want the one token revoked", len(issuedBefore), len(h.api.issuedTokens()))
	}

	if h.changed != 2 {
		t.Errorf("state-changed hook ran %d times, want 2 (enrol and unenrol)", h.changed)
	}
}

// Intent: A Secret that is already gone, or a namespace that is, counts as removed.
func TestUnenrol_SecretAlreadyGone_Succeeds(t *testing.T) {
	t.Run("secret deleted", func(t *testing.T) {
		h := newHarness(t)
		h.enrol()

		path := h.secretPath()
		h.api.mu.Lock()
		h.api.deleteLocked(path)
		h.api.mu.Unlock()

		requireSuccess(t, h.reporter.Unenrol(context.Background()))

		if h.hasState() {
			t.Error("the state file was not deleted")
		}
	})

	t.Run("namespace deleted", func(t *testing.T) {
		h := newHarness(t)
		h.enrol()

		h.api.mu.Lock()
		h.api.deleteLocked(namespacePath)
		h.api.mu.Unlock()

		requireSuccess(t, h.reporter.Unenrol(context.Background()))

		if h.hasState() {
			t.Error("the state file was not deleted")
		}
	})
}

// Intent: Without the admin credential the host cannot delete its Secret. It removes its file, stops, and says
// the Secret is left for uninstall to revoke.
func TestUnenrol_NoAdminCredential_RemovesFileAndLeavesSecret(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	own := h.secretPath()
	h.facts.AdminCredentialPresent = false
	h.facts.AdminKubeconfigPath = ""
	before := len(h.api.requestLog())

	result := h.reporter.Unenrol(context.Background())

	requireSuccess(t, result)

	if result.Message != "secret_left_for_uninstall" {
		t.Errorf("message = %q, want secret_left_for_uninstall", result.Message)
	}

	if h.api.has(own) == false {
		t.Error("the Secret was deleted without the admin credential")
	}

	if got := len(h.api.requestLog()); got != before {
		t.Errorf("made %d API requests without an admin credential", got-before)
	}

	if h.hasState() {
		t.Error("the state file was not deleted")
	}

	if h.releaser.calls != 1 {
		t.Error("the lease was not released")
	}
}

// Intent: A host that was never enrolled has nothing to remove and succeeds.
func TestUnenrol_NotEnrolled_Succeeds(t *testing.T) {
	h := newHarness(t)

	result := h.reporter.Unenrol(context.Background())

	requireSuccess(t, result)

	if got := len(h.api.requestLog()); got != 0 {
		t.Errorf("made %d API requests", got)
	}
}

// Intent: An unlabelled Secret of this candidate's name is not ours to delete: the command fails and leaves it,
// and the state file, in place.
func TestUnenrol_UnlabelledSecret_IsKept(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	own := h.secretPath()
	h.api.seed(own, map[string]any{"metadata": map[string]any{"name": tokenSecretPrefix + h.state().InstanceID}})

	requireFailure(t, h.reporter.Unenrol(context.Background()), reasonNameConflict)

	if h.api.has(own) == false {
		t.Error("the unlabelled Secret was deleted")
	}

	if h.hasState() == false {
		t.Error("the state file was removed although the Secret could not be")
	}
}

// Intent: When the API cannot be reached the file stays, so unenrol can be retried.
func TestUnenrol_ApiUnreachable_KeepsFile(t *testing.T) {
	h := newHarness(t)
	h.enrol()
	h.api.server.Close()

	requireFailure(t, h.reporter.Unenrol(context.Background()), reasonAPIUnreachable)

	if h.hasState() == false {
		t.Error("the state file was removed although the Secret could not be")
	}
}

// Intent: An unreadable kubeconfig is the same as no admin credential: the file goes and the Secret is left for
// uninstall.
func TestUnenrol_UnusableKubeconfig_TreatedAsNoAdminCredential(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	h.facts.AdminKubeconfigPath = h.kubeconfig + ".missing"

	result := h.reporter.Unenrol(context.Background())

	requireSuccess(t, result)

	if result.Message != "secret_left_for_uninstall" {
		t.Errorf("message = %q", result.Message)
	}

	if h.hasState() {
		t.Error("the state file was not deleted")
	}
}
