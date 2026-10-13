// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"net/http"
	"reflect"
	"strings"
	"testing"

	"github.com/framlux/vord/internal/commands"
)

// deletedPaths lists the paths of the DELETE requests after the first start entries of the log.
func deletedPaths(api *fakeAPI, start int) []string {
	var paths []string

	for _, request := range api.requestLog()[start:] {
		if request.Method == http.MethodDelete {
			paths = append(paths, request.Path)
		}
	}

	return paths
}

// labelledNamespace is a namespace object that carries the Vord label.
func labelledNamespace(labelled bool) map[string]any {
	metadata := map[string]any{"name": reporterNamespace}
	if labelled {
		metadata["labels"] = map[string]any{managedByLabel: managedByValue}
	}

	return map[string]any{"apiVersion": "v1", "kind": "Namespace", "metadata": metadata}
}

// Intent: Uninstall revokes every candidate's token. Every labelled Secret named vord-reporter-token-* is
// deleted, including those of hosts that are paused or gone. A Secret without the label is never touched, and
// because deleting the labelled namespace would destroy it, the namespace is left in place and the command says
// something was left behind.
func TestUninstall_DeletesEveryLabelledTokenSecret(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	own := h.secretPath()
	second := h.seedSecret("1111111111111111", true)
	third := h.seedSecret("2222222222222222", true)
	fourth := h.seedSecret("3333333333333333", true)
	unlabelled := h.seedSecret("4444444444444444", false)

	otherName := "/api/v1/namespaces/vord-system/secrets/something-else"
	h.api.seed(otherName, map[string]any{"metadata": map[string]any{"name": "something-else", "labels": map[string]any{managedByLabel: managedByValue}}})

	if isManaged(h.api.get(namespacePath)) == false {
		t.Fatal("the namespace must be the labelled one enrol created")
	}

	before := len(h.api.requestLog())

	result := h.reporter.Uninstall(context.Background())

	requireSuccess(t, result)

	for _, path := range []string{own, second, third, fourth} {
		if h.api.has(path) {
			t.Errorf("%s survived uninstall", path)
		}
	}

	if h.api.has(unlabelled) == false {
		t.Error("an unlabelled Secret was destroyed")
	}

	if h.api.has(namespacePath) == false {
		t.Error("the namespace was deleted although it still held an unlabelled Secret")
	}

	if result.Message != messageUninstalledKeptUnmanaged {
		t.Errorf("message = %q, want %q", result.Message, messageUninstalledKeptUnmanaged)
	}

	for _, path := range deletedPaths(h.api, before) {
		if (path == unlabelled) || (path == otherName) || (path == namespacePath) {
			t.Errorf("uninstall sent a DELETE for %s", path)
		}
	}

	if h.hasState() {
		t.Error("the state file was not deleted")
	}
}

// Intent: Any other unlabelled object in the namespace is protected the same way, not only Secrets: an
// unlabelled ConfigMap keeps the namespace.
func TestUninstall_UnlabelledConfigMap_KeepsNamespace(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	configMap := "/api/v1/namespaces/vord-system/configmaps/theirs"
	h.api.seed(configMap, map[string]any{"metadata": map[string]any{"name": "theirs"}, "data": map[string]any{"k": "v"}})

	result := h.reporter.Uninstall(context.Background())

	requireSuccess(t, result)

	if h.api.has(configMap) == false {
		t.Error("an unlabelled ConfigMap was destroyed")
	}

	if h.api.has(namespacePath) == false {
		t.Error("the namespace was deleted although it still held an unlabelled ConfigMap")
	}

	if result.Message != messageUninstalledKeptUnmanaged {
		t.Errorf("message = %q", result.Message)
	}

	for _, path := range sharedObjectPaths()[1:] {
		if h.api.has(path) {
			t.Errorf("%s survived uninstall", path)
		}
	}
}

// Intent: A namespaced shared object that carries our name but not our label is also protected: it keeps the
// namespace, which would otherwise delete it.
func TestUninstall_UnlabelledNamespacedSharedObject_KeepsNamespace(t *testing.T) {
	for name, path := range map[string]string{"role": rolePath, "lease": leasePath, "serviceaccount": serviceAccountPath} {
		t.Run(name, func(t *testing.T) {
			h := newHarness(t)
			h.enrol()

			h.api.seed(path, map[string]any{"metadata": map[string]any{"name": "theirs"}})

			result := h.reporter.Uninstall(context.Background())

			requireSuccess(t, result)

			if h.api.has(path) == false {
				t.Error("the unlabelled object was destroyed")
			}

			if h.api.has(namespacePath) == false {
				t.Error("the namespace was deleted although it still held an unlabelled object")
			}

			if result.Message != messageUninstalledKeptUnmanaged {
				t.Errorf("message = %q", result.Message)
			}
		})
	}
}

// Intent: The objects the cluster itself puts in every namespace are not the user's, so they do not keep the
// namespace alive.
func TestUninstall_PlatformObjectsDoNotBlockNamespaceDeletion(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	h.api.seed("/api/v1/namespaces/vord-system/configmaps/kube-root-ca.crt", map[string]any{"metadata": map[string]any{"name": "kube-root-ca.crt"}})
	h.api.seed("/api/v1/namespaces/vord-system/serviceaccounts/default", map[string]any{"metadata": map[string]any{"name": "default"}})

	result := h.reporter.Uninstall(context.Background())

	requireSuccess(t, result)

	if h.api.has(namespacePath) {
		t.Error("the namespace survived although it held only labelled and platform objects")
	}

	if result.Message != "uninstalled" {
		t.Errorf("message = %q", result.Message)
	}
}

// widgetsCollection is where the tests' custom kind lives in the reporter namespace.
const widgetsCollection = "/apis/example.com/v1/namespaces/vord-system/widgets"

// serveWidgets makes the cluster serve a custom kind, as installing a CRD does.
func (h *harness) serveWidgets() {
	h.api.addResource(namespacedKind("example.com", "v1", "widgets", "Widget"))
}

// seedWidget stores a custom object in the reporter namespace and returns its path.
func (h *harness) seedWidget(name string, labelled bool) string {
	path := widgetsCollection + "/" + name

	metadata := map[string]any{"name": name, "namespace": reporterNamespace}
	if labelled {
		metadata["labels"] = map[string]any{managedByLabel: managedByValue}
	}

	h.api.seed(path, map[string]any{"apiVersion": "example.com/v1", "kind": "Widget", "metadata": metadata})

	return path
}

// requireOnlyTheNamespaceKept fails the test unless uninstall succeeded, left the namespace and reported
// something kept, and removed every other object and the state file.
func requireOnlyTheNamespaceKept(t *testing.T, h *harness, result commands.Result) {
	t.Helper()

	requireSuccess(t, result)

	if result.Message != messageUninstalledKeptUnmanaged {
		t.Errorf("message = %q, want %q", result.Message, messageUninstalledKeptUnmanaged)
	}

	if h.api.has(namespacePath) == false {
		t.Error("the namespace was deleted")
	}

	for _, path := range sharedObjectPaths()[1:] {
		if h.api.has(path) {
			t.Errorf("%s survived uninstall", path)
		}
	}

	if h.hasState() {
		t.Error("the state file was not deleted")
	}
}

// Intent: The namespace is only deleted when nothing in it belongs to anyone else, whatever the kind. A custom
// resource kind that the cluster serves, with one object that is not the agent's, keeps the namespace, and the
// object is left alone.
func TestUninstall_UnlabelledCustomResource_KeepsNamespace(t *testing.T) {
	h := newHarness(t)
	h.enrol()
	h.serveWidgets()

	widget := h.seedWidget("theirs", false)
	before := len(h.api.requestLog())
	logs := captureLogs(t)

	result := h.reporter.Uninstall(context.Background())

	requireOnlyTheNamespaceKept(t, h, result)

	if (strings.Contains(logs.String(), "holds objects that are not the agent's") == false) || strings.Contains(logs.String(), "Widget.example.com theirs") == false {
		t.Errorf("the log does not name the foreign object:\n%s", logs.String())
	}

	if h.api.has(widget) == false {
		t.Error("an unlabelled custom object was destroyed")
	}

	for _, path := range deletedPaths(h.api, before) {
		if (path == widget) || (path == namespacePath) {
			t.Errorf("uninstall sent a DELETE for %s", path)
		}
	}
}

// Intent: The same kind holding only objects the agent labelled does not stand in the way: they go with the
// namespace, which is deleted.
func TestUninstall_LabelledCustomResource_AllowsNamespaceDeletion(t *testing.T) {
	h := newHarness(t)
	h.enrol()
	h.serveWidgets()
	h.seedWidget("ours", true)

	result := h.reporter.Uninstall(context.Background())

	requireSuccess(t, result)

	if h.api.has(namespacePath) {
		t.Error("the namespace survived although it held only labelled objects")
	}

	if result.Message != messageUninstalled {
		t.Errorf("message = %q, want %q", result.Message, messageUninstalled)
	}
}

// Intent: The namespace is kept for an object of any kind the cluster serves, not only the common workload
// kinds. Each of these keeps it, which is what finding the kinds through discovery is for.
func TestUninstall_ObjectOfAnyServedKind_KeepsNamespace(t *testing.T) {
	for _, tc := range []struct{ name, path string }{
		{"resource quota", "/api/v1/namespaces/vord-system/resourcequotas/theirs"},
		{"limit range", "/api/v1/namespaces/vord-system/limitranges/theirs"},
		{"replication controller", "/api/v1/namespaces/vord-system/replicationcontrollers/theirs"},
		{"endpoints", "/api/v1/namespaces/vord-system/endpoints/theirs"},
		{"replica set", "/apis/apps/v1/namespaces/vord-system/replicasets/theirs"},
		{"horizontal pod autoscaler", "/apis/autoscaling/v2/namespaces/vord-system/horizontalpodautoscalers/theirs"},
		{"pod disruption budget", "/apis/policy/v1/namespaces/vord-system/poddisruptionbudgets/theirs"},
		{"endpoint slice", "/apis/discovery.k8s.io/v1/namespaces/vord-system/endpointslices/theirs"},
		{"csi storage capacity", "/apis/storage.k8s.io/v1/namespaces/vord-system/csistoragecapacities/theirs"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			h := newHarness(t)
			h.enrol()

			h.api.seed(tc.path, map[string]any{"metadata": map[string]any{"name": "theirs"}})

			requireOnlyTheNamespaceKept(t, h, h.reporter.Uninstall(context.Background()))

			if h.api.has(tc.path) == false {
				t.Error("the object was destroyed")
			}
		})
	}
}

// Intent: Only the cluster's own event records are skipped. A custom kind whose plural is also "events" lives in
// another group, holds content like any other kind, and keeps the namespace.
func TestUninstall_EventsKindOfAnotherGroup_IsChecked(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	h.api.addResource(namespacedKind("example.com", "v1", "events", "Event"))

	path := "/apis/example.com/v1/namespaces/vord-system/events/theirs"
	h.api.seed(path, map[string]any{"metadata": map[string]any{"name": "theirs"}})

	requireOnlyTheNamespaceKept(t, h, h.reporter.Uninstall(context.Background()))

	if h.api.has(path) == false {
		t.Error("the object was destroyed")
	}
}

// Intent: A kind that can be deleted but not listed would be destroyed with the namespace and cannot be looked
// at, so it keeps the namespace as an unverified kind, and the reason names it.
func TestUninstall_KindThatCanBeDeletedButNotListed_KeepsNamespace(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	h.api.addResource(apiResource{group: "example.com", version: "v1", name: "opaques", kind: "Opaque", namespaced: true, verbs: []string{"get", "delete"}})

	logs := captureLogs(t)

	requireOnlyTheNamespaceKept(t, h, h.reporter.Uninstall(context.Background()))

	if (strings.Contains(logs.String(), "opaques") == false) || (strings.Contains(logs.String(), "could not be verified") == false) {
		t.Errorf("the log does not say the kind could not be verified:\n%s", logs.String())
	}
}

// Intent: A namespace that is kept anyway, because an object the agent left in place lives in it, is not looked
// through, so an API that cannot describe itself does not turn that into a failed command.
func TestUninstall_NamespaceAlreadyKept_NeedsNoDiscovery(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	h.api.seed(leasePath, map[string]any{"metadata": map[string]any{"name": "theirs"}})
	h.api.inject(fault{method: http.MethodGet, path: "/apis", status: http.StatusInternalServerError, reason: "InternalError", message: "boom", times: -1})

	result := h.reporter.Uninstall(context.Background())

	requireSuccess(t, result)

	if result.Message != messageUninstalledKeptUnmanaged {
		t.Errorf("message = %q, want %q", result.Message, messageUninstalledKeptUnmanaged)
	}

	if (h.api.has(namespacePath) == false) || (h.api.has(leasePath) == false) {
		t.Error("the namespace or the unlabelled Lease was destroyed")
	}

	if h.hasState() {
		t.Error("the state file was not deleted")
	}

	if h.api.requestCount(http.MethodGet, "/apis") != 0 {
		t.Error("uninstall asked what the API serves although the namespace was kept regardless")
	}
}

// Intent: A kind served only at a version that is not its group's preferred one is still found. Two kinds of one
// group can sit at different versions, and the namespace must not be deleted over an object of the second.
func TestUninstall_KindServedOnlyAtAnOlderVersion_KeepsNamespace(t *testing.T) {
	h := newHarness(t)
	h.enrol()
	h.serveWidgets()
	h.api.addResource(namespacedKind("example.com", "v1alpha2", "gizmos", "Gizmo"))

	gizmo := "/apis/example.com/v1alpha2/namespaces/vord-system/gizmos/theirs"
	h.api.seed(gizmo, map[string]any{"metadata": map[string]any{"name": "theirs"}})

	requireOnlyTheNamespaceKept(t, h, h.reporter.Uninstall(context.Background()))

	if h.api.has(gizmo) == false {
		t.Error("the object was destroyed")
	}
}

// Intent: Only the cluster's own objects are exempt, and they are told apart by group as well as by name: a
// custom kind that happens to be called ConfigMap, holding an object called kube-root-ca.crt, is someone else's.
func TestUninstall_PlatformObjectNamesInAnotherGroup_AreForeign(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	h.api.addResource(namespacedKind("example.com", "v1", "configmaps", "ConfigMap"))
	h.api.seed("/apis/example.com/v1/namespaces/vord-system/configmaps/kube-root-ca.crt", map[string]any{"metadata": map[string]any{"name": "kube-root-ca.crt"}})

	requireOnlyTheNamespaceKept(t, h, h.reporter.Uninstall(context.Background()))
}

// Intent: Events are records of what happened, not content, so an event the cluster or anyone else wrote in the
// namespace does not keep it, in either API that serves them.
func TestUninstall_EventsNeverKeepTheNamespace(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	h.api.seed("/api/v1/namespaces/vord-system/events/theirs.1", map[string]any{"metadata": map[string]any{"name": "theirs.1"}})
	h.api.seed("/apis/events.k8s.io/v1/namespaces/vord-system/events/theirs.2", map[string]any{"metadata": map[string]any{"name": "theirs.2"}})

	result := h.reporter.Uninstall(context.Background())

	requireSuccess(t, result)

	if h.api.has(namespacePath) {
		t.Error("the namespace survived although it held only events")
	}

	if result.Message != messageUninstalled {
		t.Errorf("message = %q, want %q", result.Message, messageUninstalled)
	}
}

// Intent: The metrics groups are read-only views and their server answers 503 while it starts, so uninstall never
// asks them anything and a starting metrics server does not keep the namespace.
func TestUninstall_MetricsGroupsAreNotConsulted(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	h.api.inject(fault{method: http.MethodGet, pathPrefix: "/apis/metrics.k8s.io", status: http.StatusServiceUnavailable, reason: "ServiceUnavailable", message: "the server is currently unable to handle the request", times: -1})

	result := h.reporter.Uninstall(context.Background())

	requireSuccess(t, result)

	if h.api.has(namespacePath) {
		t.Error("the namespace survived because of the metrics group")
	}

	if result.Message != messageUninstalled {
		t.Errorf("message = %q, want %q", result.Message, messageUninstalled)
	}

	for _, request := range h.api.requestLog() {
		if strings.Contains(request.Path, "metrics.k8s.io") {
			t.Errorf("uninstall asked for %s", request.Path)
		}
	}
}

// Intent: A group the cluster advertises but cannot describe leaves the namespace unverifiable, so it is kept,
// and the reason names the group. Everything else is still removed and the command still succeeds.
func TestUninstall_UnavailableGroup_KeepsNamespaceAndSucceeds(t *testing.T) {
	h := newHarness(t)
	h.enrol()
	h.serveWidgets()

	h.api.inject(fault{method: http.MethodGet, path: "/apis/example.com/v1", status: http.StatusServiceUnavailable, reason: "ServiceUnavailable", message: "the server is currently unable to handle the request", times: -1})

	logs := captureLogs(t)

	requireOnlyTheNamespaceKept(t, h, h.reporter.Uninstall(context.Background()))

	if reason := logs.String(); (strings.Contains(reason, "could not be verified") == false) || (strings.Contains(reason, "example.com/v1") == false) || (strings.Contains(reason, "503") == false) {
		t.Errorf("the log does not name the unverified group and its error:\n%s", reason)
	}
}

// Intent: When any one kind cannot be listed, whatever the error, the command cannot tell the namespace holds
// nothing of anyone else's, so the namespace is kept. The command still succeeds, removing everything else, and
// the reason names the kind.
func TestUninstall_CannotListAKind_KeepsNamespaceAndSucceeds(t *testing.T) {
	for _, tc := range []struct {
		name   string
		status int
		reason string
	}{
		{"forbidden", http.StatusForbidden, "Forbidden"},
		{"internal error", http.StatusInternalServerError, "InternalError"},
		{"service unavailable", http.StatusServiceUnavailable, "ServiceUnavailable"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			h := newHarness(t)
			h.enrol()

			h.api.inject(fault{
				method: http.MethodGet, path: "/api/v1/namespaces/vord-system/configmaps", status: tc.status,
				reason: tc.reason, message: "cannot list configmaps", times: -1,
			})

			logs := captureLogs(t)

			requireOnlyTheNamespaceKept(t, h, h.reporter.Uninstall(context.Background()))

			if strings.Contains(logs.String(), "configmaps") == false {
				t.Errorf("the log does not name the kind that could not be listed:\n%s", logs.String())
			}
		})
	}
}

// Intent: A command that is cancelled while it looks through the namespace fails, as any call to the API does,
// and keeps its state so it can run again. Every kind not yet read must not be passed off as unverified, which
// would end the command in success with the state file gone.
func TestUninstall_CancelledWhileListing_FailsAndKeepsState(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	h.api.inject(fault{method: http.MethodGet, path: "/api/v1/namespaces/vord-system/configmaps", onFire: func(*fakeAPI) { cancel() }})

	requireFailure(t, h.reporter.Uninstall(ctx), reasonAPIUnreachable)

	if h.hasState() == false {
		t.Error("the state file was deleted although uninstall was cancelled")
	}
}

// Intent: A kind that the cluster advertised but no longer serves (its CRD was deleted a moment ago) has no
// objects, so a 404 from its list is not uncertainty and does not keep the namespace.
func TestUninstall_KindThatIsGoneByListTime_DoesNotKeepNamespace(t *testing.T) {
	h := newHarness(t)
	h.enrol()
	h.serveWidgets()

	h.api.inject(fault{method: http.MethodGet, path: widgetsCollection, status: http.StatusNotFound, reason: "NotFound", message: "the server could not find the requested resource", times: -1})

	result := h.reporter.Uninstall(context.Background())

	requireSuccess(t, result)

	if h.api.has(namespacePath) {
		t.Error("the namespace survived a kind that was gone")
	}

	if result.Message != messageUninstalled {
		t.Errorf("message = %q, want %q", result.Message, messageUninstalled)
	}
}

// Intent: When the API cannot say what it serves at all, there is nothing to name as unverified, so the command
// fails like any other call to an API that cannot answer, deletes no namespace and keeps the state file so that
// it can be run again.
func TestUninstall_DiscoveryFails_FailsAndKeepsTheNamespace(t *testing.T) {
	for _, tc := range []struct {
		name   string
		path   string
		status int
		want   string
	}{
		{"group list", "/apis", http.StatusInternalServerError, reasonAPIUnreachable},
		{"core resources", "/api/v1", http.StatusForbidden, reasonRBACDenied},
	} {
		t.Run(tc.name, func(t *testing.T) {
			h := newHarness(t)
			h.enrol()

			h.api.inject(fault{method: http.MethodGet, path: tc.path, status: tc.status, reason: "Failure", message: "refused", times: -1})

			requireFailure(t, h.reporter.Uninstall(context.Background()), tc.want)

			if h.api.has(namespacePath) == false {
				t.Error("the namespace was deleted without knowing what it held")
			}

			if h.hasState() == false {
				t.Error("the state file was deleted although uninstall failed")
			}
		})
	}
}

// Intent: Uninstall removes everything the agent installed, in the specified order, and finishes with the
// namespace and the state file; the Lease is released before anything else happens.
func TestUninstall_RemovesEverythingLabelled_InOrder(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	own := h.secretPath()
	before := len(h.api.requestLog())

	requireSuccess(t, h.reporter.Uninstall(context.Background()))

	if h.releaser.calls != 1 {
		t.Fatalf("releaser ran %d times", h.releaser.calls)
	}

	if h.releaser.atRequestLen != before {
		t.Errorf("the lease was released after %d requests of this command; it must come before any of them", h.releaser.atRequestLen-before)
	}

	want := []string{own, leasePath, roleBindingPath, rolePath, clusterRoleBindingPath, clusterRolePath, serviceAccountPath, namespacePath}
	if got := deletedPaths(h.api, before); reflect.DeepEqual(got, want) == false {
		t.Errorf("DELETE order = %v\nwant %v", got, want)
	}

	for _, path := range sharedObjectPaths() {
		if h.api.has(path) {
			t.Errorf("%s survived uninstall", path)
		}
	}

	if h.hasState() {
		t.Error("the state file was not deleted")
	}

	if h.changed != 2 {
		t.Errorf("state-changed hook ran %d times, want 2", h.changed)
	}
}

// Intent: The release comes strictly before the first delete.
func TestUninstall_ReleasesLeaseBeforeDeleting(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	requests := h.api.requestLog()
	before := len(requests)

	requireSuccess(t, h.reporter.Uninstall(context.Background()))

	firstDelete := -1

	for i, request := range h.api.requestLog() {
		if (i >= before) && (request.Method == http.MethodDelete) {
			firstDelete = i

			break
		}
	}

	if (firstDelete < 0) || (h.releaser.atRequestLen > firstDelete) {
		t.Errorf("released at request %d, first delete at %d", h.releaser.atRequestLen, firstDelete)
	}
}

// Intent: A namespace without the Vord label was not created by Vord and is never deleted. Everything that is
// labelled is still removed, and the command says something was left behind.
func TestUninstall_RefusesUnlabelledNamespace(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	h.api.seed(namespacePath, labelledNamespace(false))
	before := len(h.api.requestLog())

	result := h.reporter.Uninstall(context.Background())

	requireSuccess(t, result)

	if h.api.has(namespacePath) == false {
		t.Fatal("an unlabelled namespace was deleted")
	}

	for _, path := range deletedPaths(h.api, before) {
		if path == namespacePath {
			t.Error("uninstall sent a DELETE for the unlabelled namespace")
		}
	}

	for _, path := range sharedObjectPaths()[1:] {
		if h.api.has(path) {
			t.Errorf("%s survived uninstall", path)
		}
	}

	if result.Message != messageUninstalledKeptUnmanaged {
		t.Errorf("message = %q, want %q", result.Message, messageUninstalledKeptUnmanaged)
	}

	if h.hasState() {
		t.Error("the state file was not deleted")
	}
}

// Intent: A shared object that carries our name but not our label is skipped, not deleted, and the rest of the
// teardown carries on.
func TestUninstall_UnlabelledSharedObject_IsKept(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	h.api.seed(clusterRolePath, map[string]any{"metadata": map[string]any{"name": reporterName}, "rules": []any{}})
	version := h.api.resourceVersion(clusterRolePath)

	result := h.reporter.Uninstall(context.Background())

	requireSuccess(t, result)

	if h.api.resourceVersion(clusterRolePath) != version {
		t.Error("the unlabelled ClusterRole was touched")
	}

	if h.api.has(clusterRoleBindingPath) || h.api.has(serviceAccountPath) {
		t.Error("labelled objects were not removed")
	}

	if result.Message != messageUninstalledKeptUnmanaged {
		t.Errorf("message = %q", result.Message)
	}
}

// Intent: An object that was replaced between the label check and the delete is not deleted: the delete carries
// the uid that was checked.
func TestUninstall_DeleteCarriesUIDPrecondition(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	before := len(h.api.requestLog())

	requireSuccess(t, h.reporter.Uninstall(context.Background()))

	for _, request := range h.api.requestLog()[before:] {
		if request.Method != http.MethodDelete {
			continue
		}

		if stringAt(request.Body, "preconditions", "uid") == "" {
			t.Errorf("DELETE %s carried no uid precondition", request.Path)
		}
	}
}

// Intent: If the API cannot be reached, the command fails with api_unreachable and keeps the state file, so it
// can be run again.
func TestUninstall_ApiUnreachable_KeepsFile(t *testing.T) {
	h := newHarness(t)
	h.enrol()
	h.api.server.Close()

	result := h.reporter.Uninstall(context.Background())

	requireFailure(t, result, reasonAPIUnreachable)

	if h.hasState() == false {
		t.Error("the state file was deleted although uninstall failed")
	}

	if h.changed != 1 {
		t.Errorf("state-changed hook ran %d times, want only the enrol's", h.changed)
	}
}

// Intent: Uninstall needs the admin credential and a control plane, as enrol does, and releases nothing and
// deletes nothing without them.
func TestUninstall_Ineligible(t *testing.T) {
	t.Run("no admin credential", func(t *testing.T) {
		h := newHarness(t)
		h.enrol()

		h.facts.AdminCredentialPresent = false
		h.facts.AdminKubeconfigPath = ""

		requireFailure(t, h.reporter.Uninstall(context.Background()), reasonNoAdminCredential)

		if h.releaser.calls != 0 {
			t.Error("the lease was released by an ineligible host")
		}

		if h.hasState() == false {
			t.Error("the state file was deleted")
		}
	})

	t.Run("not a control plane", func(t *testing.T) {
		h := newHarness(t)
		h.facts.ControlPlane = false

		requireFailure(t, h.reporter.Uninstall(context.Background()), reasonNotControlPlane)
	})
}

// Intent: A cluster with nothing installed is already uninstalled.
func TestUninstall_NothingInstalled_Succeeds(t *testing.T) {
	h := newHarness(t)

	result := h.reporter.Uninstall(context.Background())

	requireSuccess(t, result)

	if result.Message != "uninstalled" {
		t.Errorf("message = %q", result.Message)
	}
}

// Intent: A permission failure is reported as rbac_denied and keeps the file.
func TestUninstall_Forbidden_IsRbacDenied(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	h.api.inject(fault{method: http.MethodGet, path: secretsCollection, status: http.StatusForbidden, reason: "Forbidden", message: "cannot list secrets", times: -1})

	requireFailure(t, h.reporter.Uninstall(context.Background()), reasonRBACDenied)

	if h.hasState() == false {
		t.Error("the state file was deleted")
	}
}

// Intent: A name the cluster lists is put into a request path, so a labelled Secret whose name is not shaped like
// a Kubernetes name is skipped, not requested.
func TestUninstall_SkipsListedNamesThatAreNotNames(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	hostile := "/api/v1/namespaces/vord-system/secrets/hostile"
	h.api.seed(hostile, map[string]any{"metadata": map[string]any{
		"name":   tokenSecretPrefix + "x/../../../../../../apis/rbac.authorization.k8s.io/v1/clusterroles/vord-reporter",
		"labels": map[string]any{managedByLabel: managedByValue},
	}})

	before := len(h.api.requestLog())

	requireSuccess(t, h.reporter.Uninstall(context.Background()))

	for _, request := range h.api.requestLog()[before:] {
		if strings.Contains(request.Path, "x/..") || strings.Contains(request.Path, "%2F") {
			t.Errorf("a hostile name reached a request path: %s %s", request.Method, request.Path)
		}
	}
}

// Intent: Uninstall is the other way to clean up a host whose enrol never reached the state file: it revokes every
// labelled token Secret, the one the pending file names among them, and removes the pending file, so the host
// stops counting as one with a record.
func TestUninstall_RemovesThePendingFileAndTheSecretItNames(t *testing.T) {
	h := newHarness(t)

	secret, _ := h.seedPendingSecret()

	requireSuccess(t, h.reporter.Uninstall(context.Background()))

	if h.api.has(secret) {
		t.Error("the Secret the pending file names survived uninstall")
	}

	if h.hasPending() {
		t.Error("the pending file survived uninstall")
	}

	if localRecordPresent(h.dataDir, hostMachineIDValue) {
		t.Error("the host still holds a local record after uninstall")
	}
}

// Intent: A pending file beside a state file goes with it.
func TestUninstall_RemovesALeftoverPendingFileBesideTheState(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	if err := savePending(h.dataDir, "a1b2c3d4e5f60718", hostMachineIDValue); err != nil {
		t.Fatal(err)
	}

	requireSuccess(t, h.reporter.Uninstall(context.Background()))

	if h.hasPending() || h.hasState() {
		t.Error("a record file survived uninstall")
	}
}

// Intent: Uninstall works by label across the whole cluster, which is what was asked of it, so records copied from
// another host change nothing about it: the other host's Secret is revoked too, and the local copies go.
func TestUninstall_IsNotChangedByCopiedRecords(t *testing.T) {
	h := newHarness(t)
	copiedID := h.seedForeignRecord()

	if err := savePending(h.dataDir, copiedID, otherHostMachineID); err != nil {
		t.Fatal(err)
	}

	requireSuccess(t, h.reporter.Uninstall(context.Background()))

	if h.api.has(secretPath(copiedID)) {
		t.Error("the labelled Secret that the copied state names survived uninstall")
	}

	if h.hasState() || h.hasPending() {
		t.Error("a copied record file survived uninstall")
	}
}

// Intent: An uninstall that fails keeps the pending file, which may still be the only record of a live token.
func TestUninstall_Failure_KeepsThePendingFile(t *testing.T) {
	h := newHarness(t)
	h.seedPendingSecret()
	h.api.server.Close()

	requireFailure(t, h.reporter.Uninstall(context.Background()), reasonAPIUnreachable)

	if h.hasPending() == false {
		t.Error("the pending file was deleted although uninstall failed")
	}
}
