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

// Intent: When the namespace's contents cannot be listed, the command cannot tell whether deleting the
// namespace is safe, so it fails and deletes nothing more, keeping the state file for a retry.
func TestUninstall_CannotListNamespaceContents_FailsAndKeepsNamespace(t *testing.T) {
	h := newHarness(t)
	h.enrol()

	h.api.inject(fault{
		method: http.MethodGet, path: "/api/v1/namespaces/vord-system/configmaps", status: http.StatusForbidden,
		reason: "Forbidden", message: "cannot list configmaps", times: -1,
	})

	requireFailure(t, h.reporter.Uninstall(context.Background()), reasonRBACDenied)

	if h.api.has(namespacePath) == false {
		t.Error("the namespace was deleted without knowing what it held")
	}

	if h.hasState() == false {
		t.Error("the state file was deleted although uninstall failed")
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
