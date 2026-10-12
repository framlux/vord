// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"log/slog"
	"path"

	"github.com/framlux/vord/internal/commands"
)

// Unenrol takes this host out of the reporter set. It hands the Lease back if this host holds it, deletes this
// host's own token Secret and removes the state file. It never touches a shared object or another candidate's
// Secret, so unenrols on several hosts cannot race.
//
// The reporter token cannot delete Secrets, so deleting needs the admin kubeconfig. A host that has no admin
// credential at all removes its file and leaves the Secret, and says so: uninstall revokes that token later. A
// credential that is there but cannot be used is different, because the Secret may well still be live: the
// command fails with the reason and keeps the file. Everything that can be checked without the cluster is
// checked before the Lease is released, so a command that is going to fail changes nothing.
func (r *Reporter) Unenrol(ctx context.Context) commands.Result {
	r.mu.Lock()
	defer r.mu.Unlock()

	state, found, err := LoadState(r.dataDir)
	if err != nil {
		// The file cannot say which Secret is this host's, so there is nothing to delete by name.
		slog.Warn("the reporter state file cannot be used, removing it without deleting a Secret", "error", err)

		r.releaser.ReleaseAndStop(ctx)

		return r.removeStateOnly(ctx)
	}

	if found == false {
		r.releaser.ReleaseAndStop(ctx)
		r.stateChanged(ctx)

		return commands.Result{Success: true, Message: messageNotEnrolled}
	}

	facts, isNode := r.facts(ctx)
	if (isNode == false) || (facts.AdminCredentialPresent == false) || (facts.AdminKubeconfigPath == "") {
		r.releaser.ReleaseAndStop(ctx)

		return r.removeStateOnly(ctx)
	}

	session, fail := r.adminFromFacts(facts)
	if fail != nil {
		return fail.result()
	}

	r.releaser.ReleaseAndStop(ctx)

	secret := secretPath(state.InstanceID)

	outcome, err := deleteManaged(ctx, session.client, secret)
	if err != nil {
		return session.classify(err).result()
	}

	if outcome == outcomeUnmanaged {
		return session.classify(&conflictError{kind: "Secret", name: path.Base(secret)}).result()
	}

	if err := DeleteState(r.dataDir); err != nil {
		return newFailure(reasonStateWriteFailed, "", err).result()
	}

	r.stateChanged(ctx)

	return commands.Result{Success: true, Message: messageUnenrolled}
}

// removeStateOnly deletes the state file and reports that the token Secret was left in the cluster.
func (r *Reporter) removeStateOnly(ctx context.Context) commands.Result {
	if err := DeleteState(r.dataDir); err != nil {
		return newFailure(reasonStateWriteFailed, "", err).result()
	}

	r.stateChanged(ctx)

	return commands.Result{Success: true, Message: messageSecretLeftForUninstall}
}
