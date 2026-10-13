// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"net/http"
	"os"
	"strings"
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

// Intent: A credential that is present but unusable is not an absent one. The command cannot tell whether the
// Secret is gone, so it fails with a readable reason, keeps the state file, and has not stopped the runtime or
// touched the cluster.
func TestUnenrol_UnusableKubeconfig_FailsAndKeepsState(t *testing.T) {
	cases := map[string]func(h *harness){
		"unreadable": func(h *harness) { h.facts.AdminKubeconfigPath = h.kubeconfig + ".missing" },
		"exec auth": func(h *harness) {
			config := "apiVersion: v1\nkind: Config\ncurrent-context: c\nclusters:\n- name: t\n  cluster: {server: " + h.api.url() +
				", certificate-authority-data: Zm9v}\nusers:\n- name: a\n  user:\n    exec: {command: aws}\n" +
				"contexts:\n- name: c\n  context: {cluster: t, user: a}\n"
			if err := os.WriteFile(h.kubeconfig, []byte(config), 0o600); err != nil {
				t.Fatal(err)
			}
		},
		"malformed": func(h *harness) {
			if err := os.WriteFile(h.kubeconfig, []byte("not: [valid"), 0o600); err != nil {
				t.Fatal(err)
			}
		},
	}

	for name, arrange := range cases {
		t.Run(name, func(t *testing.T) {
			h := newHarness(t)
			h.enrol()

			own := h.secretPath()
			arrange(h)

			before := len(h.api.requestLog())
			state := h.state()

			result := h.reporter.Unenrol(context.Background())

			requireFailure(t, result, reasonNoAdminCredential)

			if strings.Contains(result.Message, ":") == false {
				t.Errorf("message %q gives no reason", result.Message)
			}

			if h.hasState() == false {
				t.Fatal("the state file was removed although the Secret could not be")
			}

			if h.state() != state {
				t.Error("the state file changed")
			}

			if h.api.has(own) == false {
				t.Error("the Secret was deleted")
			}

			if got := len(h.api.requestLog()); got != before {
				t.Errorf("made %d API requests", got-before)
			}

			if h.releaser.calls != 0 {
				t.Error("the runtime was stopped by a command that changed nothing")
			}

			if h.changed != 1 {
				t.Errorf("state-changed hook ran %d times, want only the enrol's", h.changed)
			}
		})
	}
}

// seedPendingSecret leaves what an enrol that created its Secret and never wrote the state file leaves: the Secret
// in the cluster and the pending file that names it. It returns the Secret's path and the instance id.
func (h *harness) seedPendingSecret() (string, string) {
	h.t.Helper()

	const instanceID = "a1b2c3d4e5f60718"

	path := h.seedSecret(instanceID, true)

	if err := savePending(h.dataDir, instanceID, hostMachineIDValue); err != nil {
		h.t.Fatal(err)
	}

	return path, instanceID
}

// Intent: A host that has only a pending file, because its enrol never reached the state file, still revokes its
// token when it is unenrolled with the admin credential: the Secret goes, by the id the file names, and the file
// with it. Nothing else in the cluster is touched.
func TestUnenrol_PendingFileOnly_DeletesTheSecretAndTheFile(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	// Another candidate's Secret and the enrolled one stay; only the pending file's Secret goes.
	other := h.secretPath()
	pendingSecret, _ := h.seedPendingSecret()

	if err := DeleteState(h.dataDir); err != nil {
		t.Fatal(err)
	}

	before := len(h.api.requestLog())

	result := h.reporter.Unenrol(context.Background())

	requireSuccess(t, result)

	if h.api.has(pendingSecret) {
		t.Error("the Secret the pending file names was not deleted")
	}

	if h.api.has(other) == false {
		t.Error("another candidate's Secret was deleted")
	}

	if h.hasPending() {
		t.Error("the pending file was left")
	}

	for _, write := range h.api.writesSince(before) {
		if (write.Method != http.MethodDelete) || (write.Path != pendingSecret) {
			t.Errorf("unexpected write %s %s", write.Method, write.Path)
		}
	}
}

// Intent: The same holds for a state file that cannot be loaded but still names its Secret.
func TestUnenrol_UnusableStateFileWithAnInstanceID_DeletesTheSecret(t *testing.T) {
	h := newHarness(t)

	secret := h.seedSecret(pinnedInstanceID, true)
	h.writeRecordFile(StateFileName, `{"instance_id":"`+pinnedInstanceID+`","server":"https://x"}`)

	requireSuccess(t, h.reporter.Unenrol(context.Background()))

	if h.api.has(secret) {
		t.Error("the Secret the damaged state file names was not deleted")
	}

	if h.hasState() {
		t.Error("the damaged state file was left")
	}
}

// Intent: The pending file takes precedence over a damaged state file, as it does in an enrol.
func TestUnenrol_PendingFileBeatsAnUnusableStateFile(t *testing.T) {
	h := newHarness(t)

	pendingSecret, _ := h.seedPendingSecret()
	stateSecret := h.seedSecret(pinnedInstanceID, true)
	h.writeRecordFile(StateFileName, `{"instance_id":"`+pinnedInstanceID+`"}`)

	requireSuccess(t, h.reporter.Unenrol(context.Background()))

	if h.api.has(pendingSecret) {
		t.Error("the pending file's Secret was not deleted")
	}

	if h.api.has(stateSecret) == false {
		t.Error("the damaged state file's Secret was deleted although the pending file came first")
	}
}

// Intent: Without the admin credential the host cannot delete anything, so it keeps its pending file, the only
// record of the Secret, makes no request, and says the Secret is left for uninstall.
func TestUnenrol_PendingFileWithoutAnAdminCredential_KeepsTheFileAndLeavesTheSecret(t *testing.T) {
	h := newHarness(t)

	secret, instanceID := h.seedPendingSecret()
	h.facts.AdminCredentialPresent = false
	h.facts.AdminKubeconfigPath = ""

	before := len(h.api.requestLog())

	result := h.reporter.Unenrol(context.Background())

	requireSuccess(t, result)

	if result.Message != "secret_left_for_uninstall" {
		t.Errorf("message = %q, want secret_left_for_uninstall", result.Message)
	}

	if h.api.has(secret) == false {
		t.Error("the Secret was deleted without the admin credential")
	}

	if got := len(h.api.requestLog()); got != before {
		t.Errorf("made %d API requests without an admin credential", got-before)
	}

	if (h.hasPending() == false) || (h.pending().InstanceID != instanceID) {
		t.Error("the pending file was not kept")
	}

	if h.releaser.calls != 1 {
		t.Error("the lease was not released")
	}
}

// Intent: A pending file whose Secret is no longer there needs nothing from the cluster: the file goes and the
// command succeeds.
func TestUnenrol_PendingFileWhoseSecretIsGone_RemovesTheFile(t *testing.T) {
	h := newHarness(t)

	secret, _ := h.seedPendingSecret()

	h.api.mu.Lock()
	h.api.deleteLocked(secret)
	h.api.mu.Unlock()

	requireSuccess(t, h.reporter.Unenrol(context.Background()))

	if h.hasPending() {
		t.Error("the pending file was left")
	}
}

// Intent: When the Secret cannot be removed the pending file stays, so the command can be run again.
func TestUnenrol_PendingFile_KeptWhenTheSecretCannotBeRemoved(t *testing.T) {
	t.Run("unlabelled Secret of that name", func(t *testing.T) {
		h := newHarness(t)

		secret, instanceID := h.seedPendingSecret()
		h.api.seed(secret, map[string]any{"metadata": map[string]any{"name": tokenSecretPrefix + instanceID}})

		requireFailure(t, h.reporter.Unenrol(context.Background()), reasonNameConflict)

		if h.api.has(secret) == false {
			t.Error("the unlabelled Secret was deleted")
		}

		if h.hasPending() == false {
			t.Error("the pending file was removed although the Secret was not")
		}
	})

	t.Run("API unreachable", func(t *testing.T) {
		h := newHarness(t)
		h.seedPendingSecret()
		h.api.server.Close()

		requireFailure(t, h.reporter.Unenrol(context.Background()), reasonAPIUnreachable)

		if h.hasPending() == false {
			t.Error("the pending file was removed although the Secret was not")
		}
	})
}

// Intent: A pending file left beside a usable state file is stale, and unenrol removes both with the Secret the
// state file names.
func TestUnenrol_RemovesALeftoverPendingFileWithTheState(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	if err := savePending(h.dataDir, "a1b2c3d4e5f60718", hostMachineIDValue); err != nil {
		t.Fatal(err)
	}

	requireSuccess(t, h.reporter.Unenrol(context.Background()))

	if h.hasPending() || h.hasState() {
		t.Error("unenrol left a record file behind")
	}
}

// Intent: A state file copied from another host names that host's Secret, so unenrol removes the local file only:
// it makes no request to the cluster at all, never deletes the Secret, and the host stops reporting a state it
// never had.
func TestUnenrol_CopiedState_RemovesOnlyTheLocalFiles(t *testing.T) {
	h := newHarness(t)
	copiedID := h.seedForeignRecord()

	if err := savePending(h.dataDir, copiedID, otherHostMachineID); err != nil {
		t.Fatal(err)
	}

	before := len(h.api.requestLog())

	result := h.reporter.Unenrol(context.Background())

	requireSuccess(t, result)

	if got := len(h.api.requestLog()); got != before {
		t.Errorf("made %d API requests for a state file that is another host's", got-before)
	}

	for _, request := range h.api.requestLog() {
		if request.Method == http.MethodDelete {
			t.Errorf("unenrol deleted %s", request.Path)
		}
	}

	if h.api.has(secretPath(copiedID)) == false {
		t.Error("the other host's Secret is gone")
	}

	if h.hasState() || h.hasPending() {
		t.Error("the copied local files were left")
	}

	if h.changed != 1 {
		t.Errorf("state-changed hook ran %d times, want 1, since the host's reported state changed", h.changed)
	}
}

// Intent: A host without admin credentials removes a copied file just the same, and a copied file that cannot be
// loaded is treated alike.
func TestUnenrol_CopiedRecords_NeedNoAdminCredentialAndNeverCallTheCluster(t *testing.T) {
	cases := map[string]func(h *harness){
		"usable copied state": func(h *harness) { h.seedForeignRecord() },
		"damaged copied state": func(h *harness) {
			h.writeRecordFile(StateFileName, `{"instance_id":"f0f1f2f3f4f5f6f7","host_machine_id":"`+otherHostMachineID+`"}`)
		},
		"copied pending file": func(h *harness) {
			if err := savePending(h.dataDir, "f0f1f2f3f4f5f6f7", otherHostMachineID); err != nil {
				h.t.Fatal(err)
			}
		},
	}

	for name, arrange := range cases {
		t.Run(name, func(t *testing.T) {
			h := newHarness(t)
			arrange(h)

			h.facts.AdminCredentialPresent = false
			h.facts.AdminKubeconfigPath = ""

			before := len(h.api.requestLog())

			requireSuccess(t, h.reporter.Unenrol(context.Background()))

			if got := len(h.api.requestLog()); got != before {
				t.Errorf("made %d API requests", got-before)
			}

			if h.hasState() || h.hasPending() {
				t.Error("the copied local files were left")
			}
		})
	}
}

// Intent: A copied state file does not hide this host's own pending file. The own Secret is revoked and both
// files go, but the Secret the copy names is never asked about.
func TestUnenrol_CopiedStateBesideAnOwnPendingFile_RevokesOnlyTheOwnSecret(t *testing.T) {
	h := newHarness(t)
	copiedID := h.seedForeignRecord()
	ownSecret, _ := h.seedPendingSecret()

	before := len(h.api.requestLog())

	requireSuccess(t, h.reporter.Unenrol(context.Background()))

	if h.api.has(ownSecret) {
		t.Error("this host's own Secret was not deleted")
	}

	if touched := touchesSecret(h.api, before, secretPath(copiedID)); len(touched) != 0 {
		t.Errorf("unenrol sent requests naming the other host's Secret: %+v", touched)
	}

	if h.hasState() || h.hasPending() {
		t.Error("a record file was left")
	}
}
