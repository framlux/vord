// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"encoding/json"
	"reflect"
	"testing"
)

// roundTrip converts a manifest to the generic form the API returns.
func roundTrip(t *testing.T, value any) map[string]any {
	t.Helper()

	encoded, err := json.Marshal(value)
	if err != nil {
		t.Fatal(err)
	}

	var out map[string]any
	if err := json.Unmarshal(encoded, &out); err != nil {
		t.Fatal(err)
	}

	return out
}

// Intent: The compiled manifests carry exact names, the Vord label and nothing the server could change. A
// change to any of these values is a change to what every enrol installs, so each is pinned.
func TestManifests_ExactNamesAndLabels(t *testing.T) {
	cases := []struct {
		name     string
		manifest map[string]any
		kind     string
		objName  string
		ns       string
	}{
		{"namespace", namespaceManifest(), "Namespace", "vord-system", ""},
		{"serviceaccount", serviceAccountManifest(), "ServiceAccount", "vord-reporter", "vord-system"},
		{"clusterrole", clusterRoleManifest(), "ClusterRole", "vord-reporter", ""},
		{"clusterrolebinding", clusterRoleBindingManifest(), "ClusterRoleBinding", "vord-reporter", ""},
		{"role", roleManifest(), "Role", "vord-reporter-lease", "vord-system"},
		{"rolebinding", roleBindingManifest(), "RoleBinding", "vord-reporter-lease", "vord-system"},
		{"lease", leaseManifest(true), "Lease", "vord-reporter", "vord-system"},
		{"secret", tokenSecretManifest("0123456789abcdef"), "Secret", "vord-reporter-token-0123456789abcdef", "vord-system"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			obj := roundTrip(t, tc.manifest)

			if stringAt(obj, "kind") != tc.kind {
				t.Errorf("kind = %q, want %q", stringAt(obj, "kind"), tc.kind)
			}

			if stringAt(obj, "metadata", "name") != tc.objName {
				t.Errorf("name = %q, want %q", stringAt(obj, "metadata", "name"), tc.objName)
			}

			if stringAt(obj, "metadata", "namespace") != tc.ns {
				t.Errorf("namespace = %q, want %q", stringAt(obj, "metadata", "namespace"), tc.ns)
			}

			if stringAt(obj, "metadata", "labels", "app.kubernetes.io/managed-by") != "vord-agent" {
				t.Errorf("missing label app.kubernetes.io/managed-by=vord-agent: %v", obj["metadata"])
			}
		})
	}
}

// Intent: The ClusterRole grants exactly nodes get and list, pods list and namespaces get.
func TestManifests_ClusterRoleRules_AreExact(t *testing.T) {
	got := roundTrip(t, clusterRoleManifest())["rules"]

	want := []any{
		map[string]any{"apiGroups": []any{""}, "resources": []any{"nodes"}, "verbs": []any{"get", "list"}},
		map[string]any{"apiGroups": []any{""}, "resources": []any{"pods"}, "verbs": []any{"list"}},
		map[string]any{"apiGroups": []any{""}, "resources": []any{"namespaces"}, "verbs": []any{"get"}},
	}

	if reflect.DeepEqual(got, want) == false {
		t.Errorf("rules = %v\nwant %v", got, want)
	}
}

// Intent: The Role grants get and update on the one Lease by name, and nothing else, so a holder cannot create
// or delete Leases.
func TestManifests_RoleRules_OnlyTheOneLease(t *testing.T) {
	got := roundTrip(t, roleManifest())["rules"]

	want := []any{
		map[string]any{
			"apiGroups":     []any{"coordination.k8s.io"},
			"resources":     []any{"leases"},
			"resourceNames": []any{"vord-reporter"},
			"verbs":         []any{"get", "update"},
		},
	}

	if reflect.DeepEqual(got, want) == false {
		t.Errorf("rules = %v\nwant %v", got, want)
	}
}

// Intent: The bindings tie the service account to the roles by exact reference.
func TestManifests_Bindings_ReferenceTheRolesAndServiceAccount(t *testing.T) {
	subjects := []any{map[string]any{"kind": "ServiceAccount", "name": "vord-reporter", "namespace": "vord-system"}}

	cluster := roundTrip(t, clusterRoleBindingManifest())
	if reflect.DeepEqual(cluster["subjects"], subjects) == false {
		t.Errorf("ClusterRoleBinding subjects = %v", cluster["subjects"])
	}

	if reflect.DeepEqual(cluster["roleRef"], map[string]any{"apiGroup": "rbac.authorization.k8s.io", "kind": "ClusterRole", "name": "vord-reporter"}) == false {
		t.Errorf("ClusterRoleBinding roleRef = %v", cluster["roleRef"])
	}

	namespaced := roundTrip(t, roleBindingManifest())
	if reflect.DeepEqual(namespaced["subjects"], subjects) == false {
		t.Errorf("RoleBinding subjects = %v", namespaced["subjects"])
	}

	if reflect.DeepEqual(namespaced["roleRef"], map[string]any{"apiGroup": "rbac.authorization.k8s.io", "kind": "Role", "name": "vord-reporter-lease"}) == false {
		t.Errorf("RoleBinding roleRef = %v", namespaced["roleRef"])
	}
}

// Intent: The Lease carries the compiled role version as a string annotation when asked, and none otherwise.
func TestManifests_Lease_RoleVersionAnnotation(t *testing.T) {
	if CompiledRoleVersion != 1 {
		t.Fatalf("CompiledRoleVersion = %d, want 1", CompiledRoleVersion)
	}

	if got := stringAt(roundTrip(t, leaseManifest(true)), "metadata", "annotations", "vord.framlux.io/role-version"); got != "1" {
		t.Errorf("annotation = %q, want 1", got)
	}

	if got := stringAt(roundTrip(t, leaseManifest(false)), "metadata", "annotations", "vord.framlux.io/role-version"); got != "" {
		t.Errorf("annotation = %q, want none", got)
	}
}

// Intent: The token Secret is a service-account token for the reporter account and nothing more.
func TestManifests_TokenSecret_IsBoundToReporterAccount(t *testing.T) {
	obj := roundTrip(t, tokenSecretManifest("0123456789abcdef"))

	if stringAt(obj, "type") != "kubernetes.io/service-account-token" {
		t.Errorf("type = %q", stringAt(obj, "type"))
	}

	if stringAt(obj, "metadata", "annotations", "kubernetes.io/service-account.name") != "vord-reporter" {
		t.Errorf("annotations = %v", obj["metadata"])
	}

	if _, hasData := obj["data"]; hasData {
		t.Error("the Secret must not carry data; the cluster fills in the token")
	}
}

// Intent: The seven shared objects are installed in a fixed order, each with its create and read paths.
func TestManifests_SharedObjects_OrderAndPaths(t *testing.T) {
	got := []string{}
	paths := []string{}

	for _, spec := range sharedObjectSpecs() {
		got = append(got, spec.kind)
		paths = append(paths, spec.collection+"|"+spec.path)
	}

	wantKinds := []string{"Namespace", "ServiceAccount", "ClusterRole", "ClusterRoleBinding", "Role", "RoleBinding", "Lease"}
	if reflect.DeepEqual(got, wantKinds) == false {
		t.Errorf("kinds = %v, want %v", got, wantKinds)
	}

	wantPaths := []string{
		"/api/v1/namespaces|/api/v1/namespaces/vord-system",
		"/api/v1/namespaces/vord-system/serviceaccounts|/api/v1/namespaces/vord-system/serviceaccounts/vord-reporter",
		"/apis/rbac.authorization.k8s.io/v1/clusterroles|/apis/rbac.authorization.k8s.io/v1/clusterroles/vord-reporter",
		"/apis/rbac.authorization.k8s.io/v1/clusterrolebindings|/apis/rbac.authorization.k8s.io/v1/clusterrolebindings/vord-reporter",
		"/apis/rbac.authorization.k8s.io/v1/namespaces/vord-system/roles|/apis/rbac.authorization.k8s.io/v1/namespaces/vord-system/roles/vord-reporter-lease",
		"/apis/rbac.authorization.k8s.io/v1/namespaces/vord-system/rolebindings|/apis/rbac.authorization.k8s.io/v1/namespaces/vord-system/rolebindings/vord-reporter-lease",
		"/apis/coordination.k8s.io/v1/namespaces/vord-system/leases|/apis/coordination.k8s.io/v1/namespaces/vord-system/leases/vord-reporter",
	}

	if reflect.DeepEqual(paths, wantPaths) == false {
		t.Errorf("paths =\n%v\nwant\n%v", paths, wantPaths)
	}

	if secretPath("0123456789abcdef") != "/api/v1/namespaces/vord-system/secrets/vord-reporter-token-0123456789abcdef" {
		t.Errorf("secret path = %q", secretPath("0123456789abcdef"))
	}
}
