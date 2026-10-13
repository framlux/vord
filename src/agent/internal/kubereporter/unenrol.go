// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"errors"
	"log/slog"
	"path"

	"github.com/framlux/vord/internal/commands"
)

// Unenrol takes this host out of the reporter set. It hands the Lease back if this host holds it, deletes this
// host's own token Secret and removes the state file and the pending file. It never touches a shared object or
// another candidate's Secret, so unenrols on several hosts cannot race.
//
// The Secret is found by the instance id this host recorded: the state file's, else the pending file's, else the
// one in a state file that cannot be loaded, so a Secret created by an enrol that never wrote the state file is
// revoked as well. A record written on another host is not this host's: it names that host's Secret, so it is
// removed from this disk and the Secret is never asked about.
//
// The reporter token cannot delete Secrets, so deleting needs the admin kubeconfig. A host that has no admin
// credential at all removes its state file and leaves the Secret, and says so: uninstall revokes that token later.
// The pending file is kept in that case when it is what names the Secret. A credential that is there but cannot be
// used is different, because the Secret may well still be live: the command fails with the reason and keeps the
// files. Everything that can be checked without the cluster is checked before the Lease is released, so a command
// that is going to fail changes nothing.
func (r *Reporter) Unenrol(ctx context.Context) commands.Result {
	r.mu.Lock()
	defer r.mu.Unlock()

	host := r.hostMachineID()

	state, found, err := LoadState(r.dataDir)
	if err != nil {
		slog.Warn("the reporter state file cannot be used, looking for the instance id in the other records", "error", err)
	}

	own := (err == nil) && found && (copiedFromAnotherHost(state.HostMachineID, host) == false)

	instanceID := state.InstanceID

	if own == false {
		var recorded bool

		instanceID, recorded = recordedInstanceID(r.dataDir, host)
		if recorded == false {
			return r.unenrolWithoutASecret(ctx, host)
		}
	}

	facts, isNode := r.facts(ctx)
	if (isNode == false) || (facts.AdminCredentialPresent == false) || (facts.AdminKubeconfigPath == "") {
		r.releaser.ReleaseAndStop(ctx)

		return r.removeStateOnly(ctx, pendingNamesInstance(r.dataDir, instanceID, host))
	}

	session, fail := r.adminFromFacts(facts)
	if fail != nil {
		return fail.result()
	}

	r.releaser.ReleaseAndStop(ctx)

	secret := secretPath(instanceID)

	outcome, err := deleteManaged(ctx, session.client, secret)
	if err != nil {
		return session.classify(err).result()
	}

	if outcome == outcomeUnmanaged {
		return session.classify(&conflictError{kind: "Secret", name: path.Base(secret)}).result()
	}

	if err := removeRecords(r.dataDir); err != nil {
		return newFailure(reasonStateWriteFailed, "", err).result()
	}

	r.stateChanged(ctx)

	return commands.Result{Success: true, Message: messageUnenrolled}
}

// unenrolWithoutASecret ends an unenrol that found no record of a Secret of this host's. What it removes depends on
// what is on the disk: a state file written on another host goes without a word to the cluster, one that cannot be
// loaded and names no Secret goes with the note that a Secret may be left, and a host with nothing but a stray
// pending file is simply not enrolled.
func (r *Reporter) unenrolWithoutASecret(ctx context.Context, host string) commands.Result {
	r.releaser.ReleaseAndStop(ctx)

	message := messageNotEnrolled

	switch {
	case stateFileExists(r.dataDir) && copiedFromAnotherHost(recordedHostMachineID(statePath(r.dataDir)), host):
		message = messageUnenrolled
	case stateFileExists(r.dataDir):
		message = messageSecretLeftForUninstall
	}

	if err := removeRecords(r.dataDir); err != nil {
		return newFailure(reasonStateWriteFailed, "", err).result()
	}

	r.stateChanged(ctx)

	return commands.Result{Success: true, Message: message}
}

// pendingNamesInstance reports whether this host's own pending file names instanceID, which makes it the only
// record of that Secret.
func pendingNamesInstance(dataDir string, instanceID string, host string) bool {
	record, ok := readRecordedInstance(dataDir, PendingFileName)

	return ok && (record.InstanceID == instanceID) && (copiedFromAnotherHost(record.HostMachineID, host) == false)
}

// removeStateOnly deletes the state file, and the pending file unless it is the only record of a Secret that was
// left in the cluster, and reports that the token Secret stays there.
func (r *Reporter) removeStateOnly(ctx context.Context, keepPending bool) commands.Result {
	err := DeleteState(r.dataDir)

	if keepPending == false {
		err = errors.Join(err, deletePending(r.dataDir))
	}

	if err != nil {
		return newFailure(reasonStateWriteFailed, "", err).result()
	}

	r.stateChanged(ctx)

	return commands.Result{Success: true, Message: messageSecretLeftForUninstall}
}
