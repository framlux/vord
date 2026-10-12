// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package kubereporter installs, removes and tears down the cluster reporter on a control plane host, on signed
// command. It is the only code in the agent that writes to a customer's cluster, so everything it creates is
// compiled in, carries one label, and is only ever changed or deleted after that label has been checked.
package kubereporter

import (
	"net/url"
	"strconv"
)

// CompiledRoleVersion is the version of the ClusterRole and Role this agent installs. The Lease records the
// version a cluster has, and an agent never lowers it.
const CompiledRoleVersion = 1

const (
	// managedByLabel and managedByValue mark every object the agent creates. An object that has the name of one
	// of ours but not this label belongs to someone else and is never touched.
	managedByLabel = "app.kubernetes.io/managed-by"
	managedByValue = "vord-agent"

	// reporterNamespace holds the namespaced objects.
	reporterNamespace = "vord-system"

	// reporterName names the ServiceAccount, ClusterRole, ClusterRoleBinding and Lease.
	reporterName = "vord-reporter"

	// leaseRoleName names the Role and RoleBinding that let the reporter use the one Lease.
	leaseRoleName = "vord-reporter-lease"

	// tokenSecretPrefix starts the name of every candidate's token Secret; the instance id follows.
	tokenSecretPrefix = "vord-reporter-token-"

	// roleVersionAnnotation records on the Lease which role version the cluster has.
	roleVersionAnnotation = "vord.framlux.io/role-version"

	// serviceAccountNameAnnotation binds a service-account token Secret to its account.
	serviceAccountNameAnnotation = "kubernetes.io/service-account.name"

	// serviceAccountTokenType is the Secret type the cluster fills with a token.
	serviceAccountTokenType = "kubernetes.io/service-account-token"

	rbacAPIGroup = "rbac.authorization.k8s.io"
)

// Collection and object paths of everything the commands touch.
const (
	namespacesCollection          = "/api/v1/namespaces"
	namespacePath                 = namespacesCollection + "/" + reporterNamespace
	serviceAccountsCollection     = namespacePath + "/serviceaccounts"
	serviceAccountPath            = serviceAccountsCollection + "/" + reporterName
	clusterRolesCollection        = "/apis/" + rbacAPIGroup + "/v1/clusterroles"
	clusterRolePath               = clusterRolesCollection + "/" + reporterName
	clusterRoleBindingsCollection = "/apis/" + rbacAPIGroup + "/v1/clusterrolebindings"
	clusterRoleBindingPath        = clusterRoleBindingsCollection + "/" + reporterName
	rolesCollection               = "/apis/" + rbacAPIGroup + "/v1/namespaces/" + reporterNamespace + "/roles"
	rolePath                      = rolesCollection + "/" + leaseRoleName
	roleBindingsCollection        = "/apis/" + rbacAPIGroup + "/v1/namespaces/" + reporterNamespace + "/rolebindings"
	roleBindingPath               = roleBindingsCollection + "/" + leaseRoleName
	leasesCollection              = "/apis/coordination.k8s.io/v1/namespaces/" + reporterNamespace + "/leases"
	leasePath                     = leasesCollection + "/" + reporterName
	secretsCollection             = namespacePath + "/secrets"
)

// secretPath is the path of the token Secret of the candidate with the given instance id.
func secretPath(instanceID string) string {
	return secretsCollection + "/" + tokenSecretPrefix + instanceID
}

// managedBySelector selects the objects that carry the Vord label.
func managedBySelector() url.Values {
	return url.Values{"labelSelector": {managedByLabel + "=" + managedByValue}}
}

// sharedObjectSpec describes one object every candidate shares: where it is read, where it is created, and what
// it is created from.
type sharedObjectSpec struct {
	kind       string
	path       string
	collection string
	build      func() map[string]any
}

// sharedObjectSpecs lists the shared objects in the order they are installed.
func sharedObjectSpecs() []sharedObjectSpec {
	return []sharedObjectSpec{
		{kind: "Namespace", path: namespacePath, collection: namespacesCollection, build: namespaceManifest},
		{kind: "ServiceAccount", path: serviceAccountPath, collection: serviceAccountsCollection, build: serviceAccountManifest},
		{kind: "ClusterRole", path: clusterRolePath, collection: clusterRolesCollection, build: clusterRoleManifest},
		{kind: "ClusterRoleBinding", path: clusterRoleBindingPath, collection: clusterRoleBindingsCollection, build: clusterRoleBindingManifest},
		{kind: "Role", path: rolePath, collection: rolesCollection, build: roleManifest},
		{kind: "RoleBinding", path: roleBindingPath, collection: roleBindingsCollection, build: roleBindingManifest},
		{kind: "Lease", path: leasePath, collection: leasesCollection, build: func() map[string]any { return leaseManifest(false) }},
	}
}

func managedLabels() map[string]any {
	return map[string]any{managedByLabel: managedByValue}
}

func objectMetadata(name string, namespace string) map[string]any {
	meta := map[string]any{"name": name, "labels": managedLabels()}
	if namespace != "" {
		meta["namespace"] = namespace
	}

	return meta
}

func namespaceManifest() map[string]any {
	return map[string]any{"apiVersion": "v1", "kind": "Namespace", "metadata": objectMetadata(reporterNamespace, "")}
}

func serviceAccountManifest() map[string]any {
	return map[string]any{"apiVersion": "v1", "kind": "ServiceAccount", "metadata": objectMetadata(reporterName, reporterNamespace)}
}

// clusterRoleRules is what the reporter may do cluster-wide: read nodes, list pods and read namespaces. It
// cannot read Secrets or ConfigMaps.
func clusterRoleRules() []map[string]any {
	return []map[string]any{
		{"apiGroups": []string{""}, "resources": []string{"nodes"}, "verbs": []string{"get", "list"}},
		{"apiGroups": []string{""}, "resources": []string{"pods"}, "verbs": []string{"list"}},
		{"apiGroups": []string{""}, "resources": []string{"namespaces"}, "verbs": []string{"get"}},
	}
}

func clusterRoleManifest() map[string]any {
	return map[string]any{
		"apiVersion": rbacAPIGroup + "/v1",
		"kind":       "ClusterRole",
		"metadata":   objectMetadata(reporterName, ""),
		"rules":      clusterRoleRules(),
	}
}

// leaseRoleRules lets the reporter read and update the one Lease by name. It cannot create or delete Leases.
func leaseRoleRules() []map[string]any {
	return []map[string]any{
		{
			"apiGroups":     []string{"coordination.k8s.io"},
			"resources":     []string{"leases"},
			"resourceNames": []string{reporterName},
			"verbs":         []string{"get", "update"},
		},
	}
}

func roleManifest() map[string]any {
	return map[string]any{
		"apiVersion": rbacAPIGroup + "/v1",
		"kind":       "Role",
		"metadata":   objectMetadata(leaseRoleName, reporterNamespace),
		"rules":      leaseRoleRules(),
	}
}

func reporterSubjects() []map[string]any {
	return []map[string]any{{"kind": "ServiceAccount", "name": reporterName, "namespace": reporterNamespace}}
}

func clusterRoleBindingManifest() map[string]any {
	return map[string]any{
		"apiVersion": rbacAPIGroup + "/v1",
		"kind":       "ClusterRoleBinding",
		"metadata":   objectMetadata(reporterName, ""),
		"roleRef":    map[string]any{"apiGroup": rbacAPIGroup, "kind": "ClusterRole", "name": reporterName},
		"subjects":   reporterSubjects(),
	}
}

func roleBindingManifest() map[string]any {
	return map[string]any{
		"apiVersion": rbacAPIGroup + "/v1",
		"kind":       "RoleBinding",
		"metadata":   objectMetadata(leaseRoleName, reporterNamespace),
		"roleRef":    map[string]any{"apiGroup": rbacAPIGroup, "kind": "Role", "name": leaseRoleName},
		"subjects":   reporterSubjects(),
	}
}

// leaseManifest is the Lease the reporters elect a holder on. It carries the compiled role version when the
// ClusterRole and Role it is created beside are at that version, and no annotation otherwise, which makes the
// next reconcile bring the roles up to date before the annotation is written.
func leaseManifest(withRoleVersion bool) map[string]any {
	meta := objectMetadata(reporterName, reporterNamespace)

	if withRoleVersion {
		meta["annotations"] = map[string]any{roleVersionAnnotation: strconv.Itoa(CompiledRoleVersion)}
	}

	return map[string]any{
		"apiVersion": "coordination.k8s.io/v1",
		"kind":       "Lease",
		"metadata":   meta,
		"spec":       map[string]any{},
	}
}

// tokenSecretManifest is one candidate's token Secret. The cluster fills in the token and the CA.
func tokenSecretManifest(instanceID string) map[string]any {
	meta := objectMetadata(tokenSecretPrefix+instanceID, reporterNamespace)
	meta["annotations"] = map[string]any{serviceAccountNameAnnotation: reporterName}

	return map[string]any{
		"apiVersion": "v1",
		"kind":       "Secret",
		"metadata":   meta,
		"type":       serviceAccountTokenType,
	}
}
