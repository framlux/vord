// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
	"time"
)

// postedPaths lists the paths of the POST requests in the log, in order.
func postedPaths(api *fakeAPI) []string {
	var paths []string

	for _, request := range api.requestLog() {
		if request.Method == http.MethodPost {
			paths = append(paths, request.Path)
		}
	}

	return paths
}

// sharedObjectPaths is every shared object's path.
func sharedObjectPaths() []string {
	return []string{namespacePath, serviceAccountPath, clusterRolePath, clusterRoleBindingPath, rolePath, roleBindingPath, leasePath}
}

// Intent: A first enrol installs every shared object, labelled, in order; creates this candidate's token
// Secret; waits for the token to be populated; writes the state file with mode 0600; and proves the token works
// with a node list before it reports success.
func TestEnrol_HappyPath_CreatesObjectsSecretAndState(t *testing.T) {
	h := newHarness(t)

	result := h.reporter.Enrol(context.Background())

	requireSuccess(t, result)

	wantPosts := []string{
		namespacesCollection, serviceAccountsCollection, clusterRolesCollection, clusterRoleBindingsCollection,
		rolesCollection, roleBindingsCollection, leasesCollection, secretsCollection,
	}
	if got := postedPaths(h.api); reflect.DeepEqual(got, wantPosts) == false {
		t.Errorf("POST order = %v, want %v", got, wantPosts)
	}

	for _, path := range sharedObjectPaths() {
		obj := h.api.get(path)
		if obj == nil {
			t.Fatalf("%s was not created", path)
		}

		if stringAt(obj, "metadata", "labels", managedByLabel) != managedByValue {
			t.Errorf("%s is not labelled %s=%s", path, managedByLabel, managedByValue)
		}
	}

	if got := stringAt(h.api.get(leasePath), "metadata", "annotations", roleVersionAnnotation); got != "1" {
		t.Errorf("Lease role-version = %q, want 1", got)
	}

	if writes := h.api.writesSince(0); countMethod(writes, http.MethodPut) != 0 {
		t.Errorf("a first enrol created the roles at the compiled version and should not rewrite them, PUTs: %+v", writes)
	}

	state := h.state()

	if hexInstanceID.MatchString(state.InstanceID) == false {
		t.Errorf("instance id %q is not 16 hex characters", state.InstanceID)
	}

	secret := h.api.get(secretPath(state.InstanceID))
	if secret == nil {
		t.Fatal("the candidate's token Secret was not created")
	}

	if stringAt(secret, "type") != serviceAccountTokenType {
		t.Errorf("Secret type = %q", stringAt(secret, "type"))
	}

	if stringAt(secret, "metadata", "annotations", serviceAccountNameAnnotation) != reporterName {
		t.Errorf("Secret is not bound to the %s service account", reporterName)
	}

	if stringAt(secret, "metadata", "labels", managedByLabel) != managedByValue {
		t.Error("the token Secret is not labelled")
	}

	if state.Server != h.api.url() {
		t.Errorf("state server = %q, want %q", state.Server, h.api.url())
	}

	if state.CACert != string(h.api.caPEM) {
		t.Error("state CA is not the Secret's ca.crt")
	}

	if h.api.issuedTokens()[state.Token] != secretPath(state.InstanceID) {
		t.Error("state token is not the token the cluster issued for the Secret")
	}

	info, err := os.Stat(filepath.Join(h.dataDir, StateFileName))
	if err != nil {
		t.Fatalf("stat state file: %v", err)
	}

	if info.Mode().Perm() != 0o600 {
		t.Errorf("state file mode = %v, want 0600", info.Mode().Perm())
	}

	verifies := 0

	for _, request := range h.api.requestLog() {
		if (request.Method == http.MethodGet) && (request.Path == "/api/v1/nodes") {
			verifies++

			if request.Principal != "reporter" {
				t.Errorf("verify was sent by %q, want the reporter token", request.Principal)
			}

			if request.RawQuery != "limit=1" {
				t.Errorf("verify query = %q, want limit=1", request.RawQuery)
			}

			if request.Status != http.StatusOK {
				t.Errorf("verify status = %d", request.Status)
			}
		}
	}

	if verifies != 1 {
		t.Errorf("verify calls = %d, want 1", verifies)
	}

	if h.changed != 1 {
		t.Errorf("state-changed hook ran %d times, want 1", h.changed)
	}

	if result.Message != "enrolled" {
		t.Errorf("message = %q", result.Message)
	}
}

func countMethod(requests []recordedRequest, method string) int {
	count := 0

	for _, request := range requests {
		if request.Method == method {
			count++
		}
	}

	return count
}

// Intent: Enrolling again changes nothing shared. Only this candidate's Secret is replaced, so the shared
// objects keep their resourceVersions and receive no write at all.
func TestEnrol_Idempotent_SecondRunChangesNothingShared(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	versions := map[string]string{}
	for _, path := range sharedObjectPaths() {
		versions[path] = h.api.resourceVersion(path)
	}

	instanceID := h.state().InstanceID
	before := len(h.api.requestLog())

	h.enrol()

	for _, path := range sharedObjectPaths() {
		if got := h.api.resourceVersion(path); got != versions[path] {
			t.Errorf("%s changed from resourceVersion %s to %s", path, versions[path], got)
		}
	}

	ownSecret := secretPath(instanceID)

	for _, write := range h.api.writesSince(before) {
		deletesOwn := (write.Method == http.MethodDelete) && (write.Path == ownSecret)
		createsOwn := (write.Method == http.MethodPost) && (write.Path == secretsCollection) &&
			(stringAt(write.Body, "metadata", "name") == tokenSecretPrefix+instanceID)

		if (deletesOwn || createsOwn) == false {
			t.Errorf("second enrol wrote outside its own Secret: %s %s", write.Method, write.Path)
		}
	}

	if got := h.state().InstanceID; got != instanceID {
		t.Errorf("instance id changed from %s to %s on re-enrol", instanceID, got)
	}
}

// Intent: The role only moves upward. When the Lease says a newer agent installed a newer role, an older agent
// leaves the ClusterRole, the Role and the annotation alone.
func TestEnrol_RoleUpwardOnly_OlderAgentLeavesNewerRole(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	newer := map[string]any{"apiGroups": []any{""}, "resources": []any{"secrets"}, "verbs": []any{"get"}}

	for _, path := range []string{clusterRolePath, rolePath} {
		obj := h.api.get(path)
		obj["rules"] = []any{newer}
		h.api.seed(path, obj)
	}

	lease := h.api.get(leasePath)
	lease["metadata"].(map[string]any)["annotations"] = map[string]any{roleVersionAnnotation: "2"}
	h.api.seed(leasePath, lease)

	versions := map[string]string{}
	for _, path := range []string{clusterRolePath, rolePath, leasePath} {
		versions[path] = h.api.resourceVersion(path)
	}

	before := len(h.api.requestLog())

	h.enrol()

	for path, version := range versions {
		if got := h.api.resourceVersion(path); got != version {
			t.Errorf("%s was rewritten (resourceVersion %s -> %s)", path, version, got)
		}
	}

	if puts := countMethod(h.api.writesSince(before), http.MethodPut); puts != 0 {
		t.Errorf("an older agent issued %d PUTs against a newer role", puts)
	}
}

// Intent: A role installed by an older agent is raised to the compiled rules, with the annotation set last, and
// a stale resourceVersion is retried against a fresh read.
func TestEnrol_OutdatedRole_IsRaisedAndAnnotationSet(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	old := []any{map[string]any{"apiGroups": []any{""}, "resources": []any{"nodes"}, "verbs": []any{"get"}}}

	for _, path := range []string{clusterRolePath, rolePath} {
		obj := h.api.get(path)
		obj["rules"] = old
		h.api.seed(path, obj)
	}

	lease := h.api.get(leasePath)
	lease["metadata"].(map[string]any)["annotations"] = map[string]any{roleVersionAnnotation: "0", "keep": "me"}
	h.api.seed(leasePath, lease)

	h.api.inject(fault{method: http.MethodPut, path: clusterRolePath, status: http.StatusConflict, reason: "Conflict", message: "stale"})
	h.api.inject(fault{method: http.MethodPut, path: leasePath, status: http.StatusConflict, reason: "Conflict", message: "stale"})

	h.enrol()

	wantCluster, _ := json.Marshal(clusterRoleRules())
	gotCluster, _ := json.Marshal(h.api.get(clusterRolePath)["rules"])

	if string(gotCluster) != string(wantCluster) {
		t.Errorf("ClusterRole rules = %s, want %s", gotCluster, wantCluster)
	}

	wantRole, _ := json.Marshal(leaseRoleRules())
	gotRole, _ := json.Marshal(h.api.get(rolePath)["rules"])

	if string(gotRole) != string(wantRole) {
		t.Errorf("Role rules = %s, want %s", gotRole, wantRole)
	}

	annotations := h.api.get(leasePath)["metadata"].(map[string]any)["annotations"].(map[string]any)
	if annotations[roleVersionAnnotation] != "1" {
		t.Errorf("role-version = %v, want 1", annotations[roleVersionAnnotation])
	}

	if annotations["keep"] != "me" {
		t.Error("the annotation update dropped an annotation it does not own")
	}

	if got := h.api.requestCount(http.MethodPut, clusterRolePath); got != 2 {
		t.Errorf("ClusterRole PUTs = %d, want 2 (one conflict, one success)", got)
	}

	if got := h.api.requestCount(http.MethodPut, leasePath); got != 2 {
		t.Errorf("Lease PUTs = %d, want 2 (one conflict, one success)", got)
	}
}

// Intent: A role that can never be written stops after five attempts and fails the command before a Secret is
// minted.
func TestEnrol_RoleUpdateConflictsForever_FailsAfterFiveAttempts(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	lease := h.api.get(leasePath)
	lease["metadata"].(map[string]any)["annotations"] = map[string]any{roleVersionAnnotation: "0"}
	h.api.seed(leasePath, lease)
	h.api.inject(fault{method: http.MethodPut, path: clusterRolePath, status: http.StatusConflict, reason: "Conflict", message: "stale", times: -1})

	instanceID := h.state().InstanceID
	secretsBefore := h.api.requestCount(http.MethodPost, secretsCollection)

	requireFailure(t, h.reporter.Enrol(context.Background()), reasonAPIUnreachable)

	if got := h.api.requestCount(http.MethodPut, clusterRolePath); got != 5 {
		t.Errorf("ClusterRole PUT attempts = %d, want 5", got)
	}

	if got := h.api.requestCount(http.MethodPost, secretsCollection); got != secretsBefore {
		t.Errorf("a Secret was created although the role could not be reconciled")
	}

	if h.hasState() == false {
		t.Error("the state file from the earlier enrol must survive a failure before the Secret step")
	}

	if h.state().InstanceID != instanceID {
		t.Error("state changed")
	}
}

// Intent: An AlreadyExists from a concurrent enrol is tolerated when the object that won the race carries the
// Vord label: the command re-reads it and carries on.
func TestEnrol_ConcurrentAlreadyExists_IsTolerated(t *testing.T) {
	for _, collection := range []string{namespacesCollection, serviceAccountsCollection, clusterRolesCollection, rolesCollection, leasesCollection} {
		t.Run(collection, func(t *testing.T) {
			h := newHarness(t)
			h.api.inject(fault{method: http.MethodPost, path: collection, raceCreate: true})

			requireSuccess(t, h.reporter.Enrol(context.Background()))

			if got := h.api.requestCount(http.MethodPost, collection); got != 1 {
				t.Errorf("POSTs to %s = %d, want exactly 1 (the raced one)", collection, got)
			}

			for _, path := range sharedObjectPaths() {
				if h.api.has(path) == false {
					t.Errorf("%s is missing", path)
				}
			}
		})
	}
}

// Intent: An AlreadyExists whose winner is not ours is a name conflict, not something to adopt.
func TestEnrol_ConcurrentAlreadyExists_UnlabelledWinner_IsRefused(t *testing.T) {
	h := newHarness(t)
	h.api.inject(fault{
		method: http.MethodPost, path: clusterRolesCollection, status: http.StatusConflict, reason: "AlreadyExists", message: "exists",
		onFire: func(api *fakeAPI) {
			api.storeLocked(clusterRolePath, map[string]any{"metadata": map[string]any{"name": reporterName}})
		},
	})

	requireFailure(t, h.reporter.Enrol(context.Background()), reasonNameConflict)

	if len(h.api.paths("/api/v1/namespaces/vord-system/secrets/")) != 0 {
		t.Error("a Secret was created after the conflict")
	}
}

// Intent: An object that has our name but not our label is never adopted, modified or deleted. The command
// refuses, writes nothing further and leaves the object byte for byte as it was.
func TestEnrol_UnlabelledConflict_RefusesAndLeavesObject(t *testing.T) {
	cases := map[string]string{
		"namespace":          namespacePath,
		"serviceaccount":     serviceAccountPath,
		"clusterrole":        clusterRolePath,
		"clusterrolebinding": clusterRoleBindingPath,
		"role":               rolePath,
		"rolebinding":        roleBindingPath,
		"lease":              leasePath,
	}

	for name, path := range cases {
		t.Run(name, func(t *testing.T) {
			h := newHarness(t)

			h.api.seed(path, map[string]any{"metadata": map[string]any{"name": "taken"}, "data": "theirs"})

			if path != namespacePath {
				h.api.seed(namespacePath, map[string]any{"metadata": map[string]any{"name": reporterNamespace, "labels": map[string]any{managedByLabel: managedByValue}}})
			}

			version := h.api.resourceVersion(path)
			before := len(h.api.requestLog())

			requireFailure(t, h.reporter.Enrol(context.Background()), reasonNameConflict)

			if got := h.api.resourceVersion(path); got != version {
				t.Errorf("the conflicting object was modified (resourceVersion %s -> %s)", version, got)
			}

			if h.api.get(path)["data"] != "theirs" {
				t.Error("the conflicting object lost its content")
			}

			for _, write := range h.api.writesSince(before) {
				if (write.Method == http.MethodPut) || (write.Method == http.MethodDelete) {
					t.Errorf("the command issued %s %s after finding a conflict", write.Method, write.Path)
				}
			}

			if len(h.api.paths("/api/v1/namespaces/vord-system/secrets/")) != 0 {
				t.Error("a Secret was created although a shared object conflicts")
			}

			if h.hasState() {
				t.Error("a state file was written for a refused enrol")
			}

			if h.changed != 0 {
				t.Error("the state-changed hook ran for a refused enrol")
			}
		})
	}
}

// Intent: This candidate's Secret name is never taken over either. An unlabelled Secret of that name is left in
// place, and nothing but the state file this run might have written is removed.
func TestEnrol_UnlabelledOwnSecret_IsRefusedAndKept(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	state := h.state()
	path := secretPath(state.InstanceID)

	h.api.seed(path, map[string]any{"metadata": map[string]any{"name": tokenSecretPrefix + state.InstanceID}, "stringData": "theirs"})
	version := h.api.resourceVersion(path)

	requireFailure(t, h.reporter.Enrol(context.Background()), reasonNameConflict)

	if h.api.resourceVersion(path) != version {
		t.Error("the unlabelled Secret was modified")
	}

	if h.api.requestCount(http.MethodDelete, path) != 0 {
		t.Error("the unlabelled Secret was deleted")
	}
}

// Intent: A namespace that is being deleted is waited on every 2 seconds for 60 seconds on the injected clock,
// then reported as namespace_terminating without writing anything.
func TestEnrol_NamespaceTerminating_FailsAfterWait(t *testing.T) {
	h := newHarness(t)
	h.api.seed(namespacePath, map[string]any{"metadata": map[string]any{"name": reporterNamespace, "labels": map[string]any{managedByLabel: managedByValue}}})
	h.api.markNamespaceTerminating(1 << 30)

	requireFailure(t, h.reporter.Enrol(context.Background()), reasonNamespaceTerminating)

	if total := h.clock.totalSlept(); total != 60*time.Second {
		t.Errorf("waited %v, want 60s", total)
	}

	for _, d := range h.clock.slept() {
		if d != 2*time.Second {
			t.Fatalf("polled every %v, want 2s", d)
		}
	}

	if writes := h.api.writesSince(0); len(writes) != 0 {
		t.Errorf("wrote while the namespace was terminating: %+v", writes)
	}

	if h.hasState() {
		t.Error("state file written")
	}
}

// Intent: A write into a namespace that is being deleted is refused with 403 Forbidden and a
// NamespaceTerminating cause. That is a terminating namespace to wait out, not a permission problem: once the
// namespace is gone the command recreates it and finishes.
func TestEnrol_ForbiddenNamespaceTerminating_IsTreatedAsTerminating(t *testing.T) {
	h := newHarness(t)
	h.api.seed(namespacePath, map[string]any{"metadata": map[string]any{"name": reporterNamespace, "labels": map[string]any{managedByLabel: managedByValue}}})
	h.api.inject(fault{
		method: http.MethodPost, path: serviceAccountsCollection, status: http.StatusForbidden, reason: "Forbidden",
		message: "unable to create new content in namespace vord-system because it is being terminated", causes: []string{"NamespaceTerminating"},
		onFire: func(api *fakeAPI) { api.markNamespaceTerminatingLocked(2) },
	})

	result := h.reporter.Enrol(context.Background())

	requireSuccess(t, result)

	if stringAt(h.api.get(namespacePath), "metadata", "deletionTimestamp") != "" {
		t.Error("the namespace is still the terminating one")
	}

	if len(h.clock.slept()) == 0 {
		t.Error("the command did not wait for the namespace")
	}

	for _, path := range sharedObjectPaths() {
		if h.api.has(path) == false {
			t.Errorf("%s was not recreated", path)
		}
	}
}

// Intent: A namespace that never finishes terminating is reported as namespace_terminating even when the
// refusal arrives as a 403, never as rbac_denied.
func TestEnrol_ForbiddenNamespaceTerminating_NeverEnds_IsNotRbacDenied(t *testing.T) {
	h := newHarness(t)
	h.api.seed(namespacePath, map[string]any{"metadata": map[string]any{"name": reporterNamespace, "labels": map[string]any{managedByLabel: managedByValue}}})
	h.api.inject(fault{
		method: http.MethodPost, path: serviceAccountsCollection, status: http.StatusForbidden, reason: "Forbidden",
		message: "being terminated", causes: []string{"NamespaceTerminating"}, times: -1,
		onFire: func(api *fakeAPI) { api.markNamespaceTerminatingLocked(1 << 30) },
	})

	requireFailure(t, h.reporter.Enrol(context.Background()), reasonNamespaceTerminating)
}

// Intent: A forbidden request that is a real permission problem is rbac_denied.
func TestEnrol_AdminForbidden_IsRbacDenied(t *testing.T) {
	h := newHarness(t)
	h.api.inject(fault{method: http.MethodPost, path: namespacesCollection, status: http.StatusForbidden, reason: "Forbidden", message: "cannot create namespaces"})

	requireFailure(t, h.reporter.Enrol(context.Background()), reasonRBACDenied)
}

// Intent: When the verify call fails after the Secret was created, only this candidate's own Secret and the
// state file are removed. Shared objects and other candidates' Secrets stay.
func TestEnrol_VerifyFails_RollsBackOnlyOwnSecretAndFile(t *testing.T) {
	h := newHarness(t)
	other := h.seedSecret("aaaaaaaaaaaaaaaa", true)
	h.api.inject(fault{method: http.MethodGet, path: "/api/v1/nodes", status: http.StatusUnauthorized, reason: "Unauthorized", message: "Unauthorized", times: -1})

	result := h.reporter.Enrol(context.Background())

	requireFailure(t, result, reasonCredentialsRejected)

	if h.hasState() {
		t.Error("the state file survived a failed enrol")
	}

	if got := h.api.paths("/api/v1/namespaces/vord-system/secrets/" + tokenSecretPrefix); len(got) != 1 || got[0] != other {
		t.Errorf("Secrets left = %v, want only the other candidate's %s", got, other)
	}

	for _, path := range sharedObjectPaths() {
		if h.api.has(path) == false {
			t.Errorf("rollback removed the shared object %s", path)
		}
	}

	for _, request := range h.api.requestLog() {
		if request.Method == http.MethodDelete && (request.Path != secretPath(h.lastInstanceID())) {
			t.Errorf("rollback deleted %s", request.Path)
		}
	}

	if got := h.api.requestCount(http.MethodGet, "/api/v1/nodes"); got != 16 {
		t.Errorf("verify attempts = %d, want 16 (every second for 15s)", got)
	}

	if h.changed != 0 {
		t.Error("the state-changed hook ran for a failed enrol")
	}
}

// lastInstanceID returns the instance id in the Secret path of the most recent Secret POST.
func (h *harness) lastInstanceID() string {
	var id string

	for _, request := range h.api.requestLog() {
		if (request.Method == http.MethodPost) && (request.Path == secretsCollection) {
			id = strings.TrimPrefix(stringAt(request.Body, "metadata", "name"), tokenSecretPrefix)
		}
	}

	return id
}

// Intent: A command that was cancelled while verifying still cleans up after itself, on a fresh context.
func TestEnrol_CancelledCommand_StillRollsBack(t *testing.T) {
	h := newHarness(t)

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	h.api.inject(fault{method: http.MethodGet, path: "/api/v1/nodes", status: http.StatusUnauthorized, reason: "Unauthorized", message: "Unauthorized", onFire: func(*fakeAPI) { cancel() }})

	result := h.reporter.Enrol(ctx)

	if result.Success {
		t.Fatal("a cancelled enrol must not succeed")
	}

	if h.hasState() {
		t.Error("the state file survived")
	}

	if got := h.api.paths("/api/v1/namespaces/vord-system/secrets/" + tokenSecretPrefix); len(got) != 0 {
		t.Errorf("the candidate's Secret survived a cancelled enrol: %v", got)
	}
}

// Intent: The cluster is given 30 seconds, polled every second, to populate the token. Without it the command
// fails with token_not_populated and removes its own Secret.
func TestEnrol_TokenNeverPopulated_Fails(t *testing.T) {
	h := newHarness(t)
	h.api.populateAfter = -1

	requireFailure(t, h.reporter.Enrol(context.Background()), reasonTokenNotPopulated)

	if total := h.clock.totalSlept(); total != 30*time.Second {
		t.Errorf("waited %v, want 30s", total)
	}

	if got := h.api.paths("/api/v1/namespaces/vord-system/secrets/" + tokenSecretPrefix); len(got) != 0 {
		t.Errorf("the unpopulated Secret was left behind: %v", got)
	}

	if h.hasState() {
		t.Error("state file written")
	}
}

// Intent: A host that is not a control plane, or that is not a node at all, never touches the API.
func TestEnrol_Ineligible_NotControlPlane(t *testing.T) {
	h := newHarness(t)
	h.facts.ControlPlane = false

	requireFailure(t, h.reporter.Enrol(context.Background()), reasonNotControlPlane)

	h.factsOK = false

	requireFailure(t, h.reporter.Enrol(context.Background()), reasonNotControlPlane)

	if got := len(h.api.requestLog()); got != 0 {
		t.Errorf("an ineligible host made %d API requests", got)
	}

	if h.hasState() {
		t.Error("state file written")
	}
}

// Intent: A control plane host with no admin credential reports no_admin_credential.
func TestEnrol_NoAdminCredential(t *testing.T) {
	h := newHarness(t)
	h.facts.AdminCredentialPresent = false
	h.facts.AdminKubeconfigPath = ""

	requireFailure(t, h.reporter.Enrol(context.Background()), reasonNoAdminCredential)

	if got := len(h.api.requestLog()); got != 0 {
		t.Errorf("made %d API requests", got)
	}
}

// Intent: An admin kubeconfig the agent cannot use, such as an exec plugin, fails with a readable reason that
// names the problem, and still touches nothing.
func TestEnrol_UnsupportedKubeconfig_FailsReadably(t *testing.T) {
	h := newHarness(t)

	config := `apiVersion: v1
kind: Config
current-context: default
clusters:
- name: test
  cluster:
    server: ` + h.api.url() + `
    certificate-authority-data: Zm9v
users:
- name: admin
  user:
    exec:
      command: aws
contexts:
- name: default
  context: {cluster: test, user: admin}
`
	if err := os.WriteFile(h.kubeconfig, []byte(config), 0o600); err != nil {
		t.Fatal(err)
	}

	result := h.reporter.Enrol(context.Background())

	requireFailure(t, result, reasonNoAdminCredential)

	if strings.Contains(result.Message, "exec") == false {
		t.Errorf("message %q does not say why the kubeconfig was refused", result.Message)
	}

	if got := len(h.api.requestLog()); got != 0 {
		t.Errorf("made %d API requests", got)
	}
}

// Intent: When the admin API cannot be reached, as on a load balancer without hairpin support, the command
// fails with api_unreachable and the message names the server it tried.
func TestEnrol_AdminApiUnreachable_NamesServer(t *testing.T) {
	h := newHarness(t)
	h.api.server.Close()

	result := h.reporter.Enrol(context.Background())

	requireFailure(t, result, reasonAPIUnreachable)

	if strings.Contains(result.Message, h.api.url()) == false {
		t.Errorf("message %q does not name the server %s", result.Message, h.api.url())
	}

	if h.hasState() {
		t.Error("state file written")
	}
}

// Intent: Re-enrolling a host that is already enrolled mints a new token: the old Secret is deleted and
// created fresh, so the old token stops working, and the instance id is kept.
func TestEnrol_ExistingOwnSecret_IsRecreatedWithFreshToken(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	first := h.state()
	firstUID := stringAt(h.api.get(secretPath(first.InstanceID)), "metadata", "uid")

	h.enrol()

	second := h.state()

	if second.InstanceID != first.InstanceID {
		t.Errorf("instance id changed from %s to %s", first.InstanceID, second.InstanceID)
	}

	if second.Token == first.Token {
		t.Error("re-enrol kept the old token")
	}

	if _, stillValid := h.api.issuedTokens()[first.Token]; stillValid {
		t.Error("the old token is still valid")
	}

	if uid := stringAt(h.api.get(secretPath(second.InstanceID)), "metadata", "uid"); uid == firstUID {
		t.Error("the Secret was not recreated")
	}
}

// Intent: An apiserver that lags behind a new token or binding answers 403 for a moment. Verify retries a 403
// every second for up to 15 seconds and succeeds as soon as the cluster agrees.
func TestEnrol_VerifyRetriesTransient403ThenSucceeds(t *testing.T) {
	h := newHarness(t)
	h.api.inject(fault{method: http.MethodGet, path: "/api/v1/nodes", status: http.StatusForbidden, reason: "Forbidden", message: "not yet", times: 3})

	requireSuccess(t, h.reporter.Enrol(context.Background()))

	if got := h.api.requestCount(http.MethodGet, "/api/v1/nodes"); got != 4 {
		t.Errorf("verify attempts = %d, want 4", got)
	}

	if h.hasState() == false {
		t.Error("state file missing after a successful retry")
	}
}

// Intent: A token the cluster keeps rejecting, or a TLS failure, are reported by cause once the retries are
// spent.
func TestEnrol_VerifyFailureCauses(t *testing.T) {
	t.Run("forbidden for the whole retry window", func(t *testing.T) {
		h := newHarness(t)
		h.api.inject(fault{method: http.MethodGet, path: "/api/v1/nodes", status: http.StatusForbidden, reason: "Forbidden", message: "no", times: -1})

		requireFailure(t, h.reporter.Enrol(context.Background()), reasonCredentialsRejected)
	})

	t.Run("server error is not retried", func(t *testing.T) {
		h := newHarness(t)
		h.api.inject(fault{method: http.MethodGet, path: "/api/v1/nodes", status: http.StatusInternalServerError, reason: "InternalError", message: "boom", times: -1})

		requireFailure(t, h.reporter.Enrol(context.Background()), reasonAPIUnreachable)

		if got := h.api.requestCount(http.MethodGet, "/api/v1/nodes"); got != 1 {
			t.Errorf("verify attempts = %d, want 1", got)
		}
	})
}

// Intent: When the CA the cluster puts in the token Secret does not verify the API server, the command reports
// tls_ca_changed and removes its own Secret and file.
func TestEnrol_VerifyTLSFailure_IsTlsCaChanged(t *testing.T) {
	h := newHarness(t)
	h.api.secretCAPEM = unrelatedCAPEM(t)

	requireFailure(t, h.reporter.Enrol(context.Background()), reasonTLSCAChanged)

	if h.hasState() {
		t.Error("the state file survived")
	}

	if got := h.api.paths("/api/v1/namespaces/vord-system/secrets/" + tokenSecretPrefix); len(got) != 0 {
		t.Errorf("the candidate's Secret survived: %v", got)
	}
}

// Intent: Neither the admin credential nor the reporter token or CA ever appears in a log line, a failure
// message or an error returned to the command channel.
func TestEnrol_NeverLogsOrReturnsCredentials(t *testing.T) {
	logs := captureLogs(t)

	scenarios := map[string]func(h *harness){
		"success": func(h *harness) {},
		"verify rejected": func(h *harness) {
			h.api.inject(fault{method: http.MethodGet, path: "/api/v1/nodes", status: http.StatusUnauthorized, reason: "Unauthorized", message: "Unauthorized", times: -1})
		},
		"role conflicts": func(h *harness) {
			h.api.inject(fault{method: http.MethodPost, path: clusterRolesCollection, status: http.StatusForbidden, reason: "Forbidden", message: "denied", times: -1})
		},
	}

	for name, arrange := range scenarios {
		t.Run(name, func(t *testing.T) {
			h := newHarness(t)
			arrange(h)

			result := h.reporter.Enrol(context.Background())
			text := result.Message

			if result.Error != nil {
				text += " " + result.Error.Error()
			}

			tokens := []string{fakeAdminToken}
			for token := range h.api.issuedTokens() {
				tokens = append(tokens, token)
			}

			if h.hasState() {
				tokens = append(tokens, h.state().Token)
			}

			for _, secret := range tokens {
				if strings.Contains(text, secret) || strings.Contains(logs.String(), secret) {
					t.Errorf("credential %q leaked into a result or a log", secret)
				}
			}

			if strings.Contains(text, "BEGIN CERTIFICATE") || strings.Contains(logs.String(), "BEGIN CERTIFICATE") {
				t.Error("a certificate leaked into a result or a log")
			}
		})
	}
}
