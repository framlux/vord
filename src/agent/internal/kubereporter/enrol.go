// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"encoding/base64"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"net/url"
	"path"
	"strconv"
	"time"

	"github.com/framlux/vord/internal/commands"
	"github.com/framlux/vord/internal/kube"
)

// Enrol installs the reporter objects in the cluster, or brings them up to date, creates this host's own token
// Secret, writes the state file and proves the token works. It uses the host's admin kubeconfig, which it reads
// and keeps in memory only.
//
// Everything shared between candidates is created only when it is missing, and an object that has the name of
// one of ours but not the Vord label is a conflict that stops the command before anything else is written. The
// instance id is written to the pending file before the Secret is created, so that no Secret can exist that no
// local file names. A failure after the Secret step removes this host's Secret, the pending file and the state
// file and nothing shared. A record written on another host is ignored as if it were not there.
func (r *Reporter) Enrol(ctx context.Context) commands.Result {
	r.mu.Lock()
	defer r.mu.Unlock()

	session, fail := r.openAdmin(ctx)
	if fail != nil {
		return fail.result()
	}

	if fail := r.ensureShared(ctx, session); fail != nil {
		return fail.result()
	}

	hadState := stateFileExists(r.dataDir)
	host := r.hostMachineID()

	instanceID, stateHoldsID, err := r.instanceID(host)
	if err != nil {
		return newFailure(reasonStateWriteFailed, "", err).result()
	}

	run := &enrolRun{
		reporter:      r,
		session:       session,
		instanceID:    instanceID,
		hostMachineID: host,
		stateHoldsID:  stateHoldsID,
		hadState:      hadState,
	}

	if fail := run.execute(ctx); fail != nil {
		if run.secretTouched {
			run.rollback(ctx)
		}

		return fail.result()
	}

	r.stateChanged(ctx)

	return commands.Result{Success: true, Message: messageEnrolled}
}

// instanceID returns the id this enrol uses, and whether a usable state file already holds it. A host that
// enrolled before keeps its id, so a re-enrol replaces its Secret instead of leaking it. The first of these that
// has an id wins: the usable state file, then the pending file, then the instance id in a state file that cannot
// be loaded as a whole, and otherwise a new id is made. A record written on another host is skipped at every step,
// because the Secret it names is that host's.
func (r *Reporter) instanceID(host string) (string, bool, error) {
	state, found, err := LoadState(r.dataDir)
	if err != nil {
		slog.Warn("the reporter state file cannot be used", "error", err)
	}

	if (err == nil) && found {
		if copiedFromAnotherHost(state.HostMachineID, host) == false {
			return state.InstanceID, true, nil
		}

		slog.Warn("the reporter state file was written on another host, ignoring it", "instance", state.InstanceID)
	}

	if id, ok := recordedInstanceID(r.dataDir, host); ok {
		slog.Info("enrolling with the instance id this host recorded earlier", "instance", id)

		return id, false, nil
	}

	id, err := newInstanceID()

	return id, false, err
}

// enrolRun is the part of an enrol that starts with this host's Secret: what it has changed, so a failure can
// undo exactly that and no more.
type enrolRun struct {
	reporter   *Reporter
	session    *adminSession
	instanceID string

	// hostMachineID is this host's machine id, written into the pending and state files so that a copy of either
	// on another host can be told from the original.
	hostMachineID string
	// stateHoldsID is whether a usable state file of this host already names instanceID. It is then the record
	// of the Secret, and no pending file is needed.
	stateHoldsID bool
	// hadState is whether a state file existed before the run, which is what the server last heard about.
	hadState bool
	// secretTouched is set once the old Secret is confirmed gone (or there was none) and the instance id is
	// recorded on disk, immediately before the new Secret is created. From that point an earlier state file names
	// a dead token and a new Secret may exist, so a failure is rolled back. Until then a failure changes nothing,
	// and the files that record the instance id must stay.
	secretTouched bool
	// secretCreated and secretUID identify the Secret this run created.
	secretCreated bool
	secretUID     string
	// secretMaybeCreated is set when a create failed in a way that may still have written the Secret.
	secretMaybeCreated bool
}

// execute runs the Secret, the wait for its token, the shared objects again, the state file and the
// verification.
func (run *enrolRun) execute(ctx context.Context) *failure {
	if fail := run.replaceSecret(ctx); fail != nil {
		return fail
	}

	token, caPEM, fail := run.awaitToken(ctx)
	if fail != nil {
		return fail
	}

	// The shared objects are read again because an uninstall can have raced the first pass.
	if fail := run.reporter.ensureShared(ctx, run.session); fail != nil {
		return fail
	}

	state := State{
		InstanceID:    run.instanceID,
		Server:        run.session.serverURL,
		CACert:        caPEM,
		Token:         token,
		TLSServerName: run.session.tlsServerName,
		HostMachineID: run.hostMachineID,
	}

	if err := SaveState(run.reporter.dataDir, state); err != nil {
		return newFailure(reasonStateWriteFailed, "", err)
	}

	// The state file is the record from here on. A pending file that cannot be removed is only a leftover: the
	// next successful enrol, unenrol or uninstall removes it.
	if err := deletePending(run.reporter.dataDir); err != nil {
		slog.Warn("could not remove the reporter pending file after writing the state file", "error", err)
	}

	return run.reporter.verify(ctx, state)
}

// replaceSecret deletes this host's old token Secret, when it has one that is ours, and creates a fresh one, so
// a re-enrol always mints a new token. An unlabelled Secret of that name is never touched. The instance id is
// written to the pending file just before the create, unless a usable state file already holds it, so a Secret
// never exists without a local file naming it. The run only counts as having touched the Secret once the old one
// is confirmed gone and the id is recorded: if the delete fails the old token may be live, and nothing may be
// undone.
func (run *enrolRun) replaceSecret(ctx context.Context) *failure {
	client := run.session.client
	secret := secretPath(run.instanceID)

	existing, err := client.GetRaw(ctx, secret)

	switch {
	case err == nil:
		if isManaged(existing) == false {
			return run.session.classify(&conflictError{kind: "Secret", name: path.Base(secret)})
		}

		outcome, err := deleteManaged(ctx, client, secret)
		if err != nil {
			return run.session.classify(err)
		}

		if outcome == outcomeUnmanaged {
			return run.session.classify(&conflictError{kind: "Secret", name: path.Base(secret)})
		}
	case kube.IsNotFound(err):
	default:
		return run.session.classify(err)
	}

	if run.stateHoldsID == false {
		if err := savePending(run.reporter.dataDir, run.instanceID, run.hostMachineID); err != nil {
			return newFailure(reasonStateWriteFailed, "", err)
		}
	}

	run.secretTouched = true

	var created map[string]any

	if err := client.Do(ctx, http.MethodPost, secretsCollection, nil, tokenSecretManifest(run.instanceID), &created); err != nil {
		if kube.IsAlreadyExists(err) {
			return run.session.classify(&conflictError{kind: "Secret", name: path.Base(secret)})
		}

		run.secretMaybeCreated = mayHaveCreated(err)

		return run.session.classify(err)
	}

	run.secretCreated = true
	run.secretUID = stringAt(created, "metadata", "uid")

	return nil
}

// mayHaveCreated reports whether a create that returned err may nevertheless have created the object: the
// request can reach the server before a connection fails, and a server can fail after it has written. Only an
// answer below 500 says for certain that nothing was written.
func mayHaveCreated(err error) bool {
	var apiError *kube.APIError
	if errors.As(err, &apiError) {
		return apiError.Code >= http.StatusInternalServerError
	}

	return true
}

// awaitToken waits for the cluster to fill in the Secret's token and CA, and returns them decoded.
func (run *enrolRun) awaitToken(ctx context.Context) (string, string, *failure) {
	r := run.reporter
	secret := secretPath(run.instanceID)

	var token, caPEM string

	err := r.poll(ctx, tokenPoll, r.now().Add(tokenWait), func(ctx context.Context) (bool, error) {
		obj, err := run.session.client.GetRaw(ctx, secret)
		if err != nil {
			return false, err
		}

		token = decodeSecretField(obj, "token")
		caPEM = decodeSecretField(obj, "ca.crt")

		return (token != "") && (caPEM != ""), nil
	})

	switch {
	case err == nil:
		return token, caPEM, nil
	case errors.Is(err, errPollExpired) || kube.IsNotFound(err):
		return "", "", newFailure(reasonTokenNotPopulated, "", err)
	}

	return "", "", run.session.classify(err)
}

// decodeSecretField returns the base64-decoded value of a key in a Secret's data, or "" when it is missing or
// not valid base64.
func decodeSecretField(obj map[string]any, key string) string {
	data, ok := obj["data"].(map[string]any)
	if ok == false {
		return ""
	}

	text, _ := data[key].(string)

	decoded, err := base64.StdEncoding.DecodeString(text)
	if err != nil {
		return ""
	}

	return string(decoded)
}

// rollback removes the Secret this run created, or may have created, and then the pending file and the state
// file, on a context of its own so that a cancelled command still cleans up. It never touches a shared object, nor
// a Secret this run did not create, nor a state file written on another host.
//
// When the Secret's fate is unknown because deleting it failed, both files stay: they are the only record of the
// instance id, so a later unenrol or re-enrol can still find and revoke the Secret. The hook runs when the state
// file's presence ended up different from what it was before the run.
func (run *enrolRun) rollback(ctx context.Context) {
	cleanup, cancel := context.WithTimeout(context.WithoutCancel(ctx), cleanupTimeout)
	defer cancel()

	if run.removeOwnSecret(cleanup) {
		run.removeOwnRecords()
	} else {
		slog.Warn("the reporter token Secret could not be removed, keeping the record of its instance id so that it can still be unenrolled")
	}

	if stateFileExists(run.reporter.dataDir) != run.hadState {
		run.reporter.stateChanged(ctx)
	}
}

// removeOwnRecords deletes the pending file and this host's own state file once the Secret they name is gone. A
// state file that another host wrote is left alone, because it was never this run's to replace.
func (run *enrolRun) removeOwnRecords() {
	dataDir := run.reporter.dataDir

	if err := deletePending(dataDir); err != nil {
		slog.Warn("could not remove the reporter pending file while rolling back an enrol", "error", err)
	}

	if copiedFromAnotherHost(recordedHostMachineID(statePath(dataDir)), run.hostMachineID) {
		return
	}

	if err := DeleteState(dataDir); err != nil {
		slog.Warn("could not remove the reporter state file while rolling back an enrol", "error", err)
	}
}

// removeOwnSecret deletes the Secret this run created, by the uid it was given, or, when a create failed
// ambiguously, whichever labelled Secret has this host's name. It reports false when the Secret may still exist.
func (run *enrolRun) removeOwnSecret(ctx context.Context) bool {
	client := run.session.client
	secret := secretPath(run.instanceID)

	switch {
	case run.secretCreated:
		err := client.Do(ctx, http.MethodDelete, secret, nil, deleteOptions(run.secretUID), nil)

		// A missing Secret is gone, and one that no longer has our uid is not ours to delete.
		if (err != nil) && (kube.IsNotFound(err) == false) && (kube.IsConflict(err) == false) {
			slog.Warn("could not remove the reporter token Secret while rolling back an enrol", "error", err)

			return false
		}
	case run.secretMaybeCreated:
		if _, err := deleteManaged(ctx, client, secret); err != nil {
			slog.Warn("could not remove the reporter token Secret while rolling back an enrol", "error", err)

			return false
		}
	}

	return true
}

// verify proves the new token works by listing one node with it. An apiserver behind a load balancer can lag a
// new token or binding for a moment, so a 401 or 403 is retried every second for up to 15 seconds.
func (r *Reporter) verify(ctx context.Context, state State) *failure {
	client, err := kube.New(kube.Config{
		Server:        state.Server,
		CAData:        []byte(state.CACert),
		TLSServerName: state.TLSServerName,
		Token:         state.Token,
	})
	if err != nil {
		return newFailure(reasonTokenNotPopulated, "", err)
	}

	server := redactedServer(state.Server)
	query := url.Values{"limit": {"1"}}

	var last error

	err = r.poll(ctx, verifyPoll, r.now().Add(verifyWait), func(ctx context.Context) (bool, error) {
		callErr := client.Do(ctx, http.MethodGet, "/api/v1/nodes", query, nil, nil)
		if callErr == nil {
			return true, nil
		}

		last = callErr

		// Only a refusal can be a cluster that has not caught up yet. Anything else retrying will not cure.
		if kube.IsAuthRejected(callErr) {
			return false, nil
		}

		return false, callErr
	})

	switch {
	case err == nil:
		return nil
	case errors.Is(err, errPollExpired):
		err = last
	}

	return classifyReporterError(err, server)
}

// classifyReporterError turns an error from a call made with the reporter token into the failure it deserves.
func classifyReporterError(err error, server string) *failure {
	switch {
	case kube.IsAuthRejected(err):
		return newFailure(reasonCredentialsRejected, "", err)
	case kube.IsTLSVerify(err):
		return newFailure(reasonTLSCAChanged, "", err)
	}

	return newFailure(reasonAPIUnreachable, unreachableDetail(server, err), err)
}

// ensureShared creates every shared object that is missing and brings the roles up to date, waiting for a
// terminating namespace to finish disappearing. The wait is budgeted once per call.
func (r *Reporter) ensureShared(ctx context.Context, session *adminSession) *failure {
	deadline := r.now().Add(namespaceWait)

	for round := 0; round < terminatingRounds; round++ {
		err := r.ensureSharedOnce(ctx, session.client)
		if err == nil {
			return nil
		}

		if (errors.Is(err, errNamespaceTerminating) || kube.IsNamespaceTerminating(err)) == false {
			return session.classify(err)
		}

		if err := r.waitNamespaceGone(ctx, session.client, deadline); err != nil {
			// A command that runs out of time while waiting was still waiting on a terminating namespace.
			if errors.Is(err, errPollExpired) || (ctx.Err() != nil) {
				return session.classify(errNamespaceTerminating)
			}

			return session.classify(err)
		}
	}

	return session.classify(errNamespaceTerminating)
}

// waitNamespaceGone polls the namespace every 2 seconds until it is gone or no longer terminating.
func (r *Reporter) waitNamespaceGone(ctx context.Context, client *kube.Client, deadline time.Time) error {
	return r.poll(ctx, namespacePoll, deadline, func(ctx context.Context) (bool, error) {
		obj, err := client.GetRaw(ctx, namespacePath)
		if err != nil {
			if kube.IsNotFound(err) {
				return true, nil
			}

			return false, err
		}

		return isTerminating(obj) == false, nil
	})
}

// ensureSharedOnce makes one pass over the shared objects in order. The Lease is created with the compiled role
// version only when this pass created both roles itself.
func (r *Reporter) ensureSharedOnce(ctx context.Context, client *kube.Client) error {
	rolesCurrent := true

	var lease map[string]any

	for _, spec := range sharedObjectSpecs() {
		body := spec.build()
		if spec.path == leasePath {
			body = leaseManifest(rolesCurrent)
		}

		obj, created, err := ensureObject(ctx, client, spec, body)
		if err != nil {
			return err
		}

		switch spec.path {
		case clusterRolePath, rolePath:
			if created == false {
				rolesCurrent = false
			}
		case leasePath:
			lease = obj
		}
	}

	return reconcileRoles(ctx, client, lease)
}

// ensureObject returns the object at spec.path, creating it from body when it is missing. An object that exists
// without the Vord label is a conflict. A create that loses a race (AlreadyExists) reads the winner and checks
// its label like any other existing object.
func ensureObject(ctx context.Context, client *kube.Client, spec sharedObjectSpec, body map[string]any) (map[string]any, bool, error) {
	var lastErr error

	for attempt := 0; attempt < 3; attempt++ {
		existing, err := client.GetRaw(ctx, spec.path)

		switch {
		case err == nil:
			if isManaged(existing) == false {
				return nil, false, &conflictError{kind: spec.kind, name: path.Base(spec.path)}
			}

			if (spec.path == namespacePath) && isTerminating(existing) {
				return nil, false, errNamespaceTerminating
			}

			return existing, false, nil
		case kube.IsNotFound(err):
			var made map[string]any

			err = client.Do(ctx, http.MethodPost, spec.collection, nil, body, &made)
			if err == nil {
				return made, true, nil
			}

			if kube.IsAlreadyExists(err) {
				lastErr = err

				continue
			}

			return nil, false, err
		default:
			return nil, false, err
		}
	}

	return nil, false, fmt.Errorf("%s %s keeps being created and removed: %w", spec.kind, path.Base(spec.path), lastErr)
}

// roleVersionOf reads the role version the Lease records, and is 0 when there is none or it is not a number.
func roleVersionOf(lease map[string]any) int {
	version, err := strconv.Atoi(stringAt(lease, "metadata", "annotations", roleVersionAnnotation))
	if (err != nil) || (version < 0) {
		return 0
	}

	return version
}

// reconcileRoles raises the ClusterRole and Role to the compiled rules when the Lease says they are older, and
// then records the compiled version on the Lease. A cluster whose Lease already records the compiled version or
// a newer one is left alone, so agents of different versions never flip the roles back and forth.
func reconcileRoles(ctx context.Context, client *kube.Client, lease map[string]any) error {
	if roleVersionOf(lease) >= CompiledRoleVersion {
		return nil
	}

	if err := replaceRules(ctx, client, "ClusterRole", clusterRolePath, clusterRoleRules()); err != nil {
		return err
	}

	if err := replaceRules(ctx, client, "Role", rolePath, leaseRoleRules()); err != nil {
		return err
	}

	return recordRoleVersion(ctx, client)
}

// replaceRules sets the rules of a labelled role with a compare-and-swap, reading it again after each Conflict.
// Before every attempt it reads the Lease's role version again: the version that decided to update may be stale
// by now, and a Lease that has reached the compiled version, or passed it, means another agent installed
// roles that must not be overwritten, so the update ends without writing.
func replaceRules(ctx context.Context, client *kube.Client, kind string, objectPath string, rules []map[string]any) error {
	var last error

	for attempt := 0; attempt < updateAttempts; attempt++ {
		lease, err := client.GetRaw(ctx, leasePath)
		if err != nil {
			return err
		}

		if roleVersionOf(lease) >= CompiledRoleVersion {
			return nil
		}

		obj, err := client.GetRaw(ctx, objectPath)
		if err != nil {
			return err
		}

		if isManaged(obj) == false {
			return &conflictError{kind: kind, name: path.Base(objectPath)}
		}

		obj["rules"] = rules

		_, err = client.PutRaw(ctx, objectPath, obj)
		if err == nil {
			return nil
		}

		if kube.IsConflict(err) == false {
			return err
		}

		last = err
	}

	return last
}

// recordRoleVersion writes the compiled role version into the Lease's annotations with a compare-and-swap,
// reading the Lease again after each Conflict. Everything else in the Lease is kept as the cluster has it.
func recordRoleVersion(ctx context.Context, client *kube.Client) error {
	var last error

	for attempt := 0; attempt < updateAttempts; attempt++ {
		lease, err := client.GetRaw(ctx, leasePath)
		if err != nil {
			return err
		}

		if isManaged(lease) == false {
			return &conflictError{kind: "Lease", name: path.Base(leasePath)}
		}

		if roleVersionOf(lease) >= CompiledRoleVersion {
			return nil
		}

		meta, ok := lease["metadata"].(map[string]any)
		if ok == false {
			meta = map[string]any{}
			lease["metadata"] = meta
		}

		annotations, ok := meta["annotations"].(map[string]any)
		if ok == false {
			annotations = map[string]any{}
			meta["annotations"] = annotations
		}

		annotations[roleVersionAnnotation] = strconv.Itoa(CompiledRoleVersion)

		_, err = client.PutRaw(ctx, leasePath, lease)
		if err == nil {
			return nil
		}

		if kube.IsConflict(err) == false {
			return err
		}

		last = err
	}

	return last
}
