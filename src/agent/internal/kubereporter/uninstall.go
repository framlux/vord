// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"encoding/json"
	"log/slog"
	"slices"
	"strings"

	"github.com/framlux/vord/internal/commands"
	"github.com/framlux/vord/internal/kube"
)

// listPageSize is how many objects one list request asks for.
const listPageSize = 200

// What uninstall reports having left of the namespace.
const (
	keptNamespace           = "Namespace"
	keptNamespaceUnverified = "Namespace (its contents could not be verified)"
)

// Uninstall removes everything the agent installed in the cluster, using the host's admin kubeconfig: every
// candidate's token Secret (which revokes the tokens of hosts that are paused, deleted or unreachable), then the
// Lease, the roles and bindings and the ServiceAccount, then the namespace, and finally this host's state file and
// pending file. It works by label across the whole cluster, so a record copied from another host changes nothing
// about it.
//
// Each object is deleted by its fixed name after its label has been checked, and the delete names the uid that
// was checked. An object without the Vord label is left alone. That includes the namespace, when the agent did
// not create it, and when it still holds an object that is not the agent's: deleting a namespace deletes
// everything in it, so such a namespace is kept and the command says so. The check covers every namespaced kind
// the API serves, custom resources included, and a kind it cannot read keeps the namespace too, without failing
// the command: the cost of being wrong is an empty namespace left behind, never someone else's objects destroyed.
// If the API cannot be reached the command fails and keeps the state file and the pending file so that it can be
// run again.
func (r *Reporter) Uninstall(ctx context.Context) commands.Result {
	r.mu.Lock()
	defer r.mu.Unlock()

	session, fail := r.openAdmin(ctx)
	if fail != nil {
		return fail.result()
	}

	r.releaser.ReleaseAndStop(ctx)

	names, err := listTokenSecrets(ctx, session.client)
	if err != nil {
		return session.classify(err).result()
	}

	var kept []string

	// keptInNamespace is set when an object left in place lives inside the namespace, which deleting the
	// namespace would destroy along with it.
	keptInNamespace := false

	for _, name := range names {
		outcome, err := deleteManaged(ctx, session.client, secretsCollection+"/"+name)
		if err != nil {
			return session.classify(err).result()
		}

		if outcome == outcomeUnmanaged {
			kept = append(kept, "Secret "+name)
			keptInNamespace = true
		}
	}

	removals := []sharedObjectSpec{}

	for _, spec := range sharedObjectSpecs() {
		if spec.path != namespacePath {
			removals = append(removals, spec)
		}
	}

	// The shared objects go in reverse of the order they were installed, and the namespace goes last.
	for i := len(removals) - 1; i >= 0; i-- {
		outcome, err := deleteManaged(ctx, session.client, removals[i].path)
		if err != nil {
			return session.classify(err).result()
		}

		if outcome == outcomeUnmanaged {
			kept = append(kept, removals[i].kind)

			if strings.Contains(removals[i].path, "/namespaces/"+reporterNamespace+"/") {
				keptInNamespace = true
			}
		}
	}

	namespaceKept, err := removeNamespace(ctx, session.client, keptInNamespace)
	if err != nil {
		return session.classify(err).result()
	}

	if namespaceKept != "" {
		kept = append(kept, namespaceKept)
	}

	if err := removeRecords(r.dataDir); err != nil {
		return newFailure(reasonStateWriteFailed, "", err).result()
	}

	r.stateChanged(ctx)

	if len(kept) > 0 {
		slog.Info("uninstall left objects in place", "objects", strings.Join(kept, ", "))

		return commands.Result{Success: true, Message: messageUninstalledKeptUnmanaged}
	}

	return commands.Result{Success: true, Message: messageUninstalled}
}

// listTokenSecrets returns the names of the labelled Secrets in the namespace that are candidates' token
// Secrets. A namespace that does not exist has none.
func listTokenSecrets(ctx context.Context, client *kube.Client) ([]string, error) {
	var names []string

	err := client.List(ctx, secretsCollection, managedBySelector(), listPageSize, func(body []byte) error {
		var page objectList
		if err := json.Unmarshal(body, &page); err != nil {
			return err
		}

		for _, item := range page.Items {
			name := item.Metadata.Name

			if strings.HasPrefix(name, tokenSecretPrefix) && (item.Metadata.Labels[managedByLabel] == managedByValue) && kube.IsValidName(name) {
				names = append(names, name)
			}
		}

		return nil
	}, func() { names = nil })
	if err != nil {
		if kube.IsNotFound(err) {
			return nil, nil
		}

		return nil, err
	}

	return names, nil
}

// objectList is the part of a list that uninstall reads: names and labels only, so the content of a Secret or
// ConfigMap is dropped as each page is decoded.
type objectList struct {
	Items []struct {
		Metadata struct {
			Name   string            `json:"name"`
			Labels map[string]string `json:"labels"`
		} `json:"metadata"`
	} `json:"items"`
}

// platformObject names an object the cluster itself puts in every namespace, by the API group and kind it is
// served as.
type platformObject struct{ group, kind, name string }

// platformObjects are the objects every namespace gets from Kubernetes itself, which makes them nobody's to
// protect. A freshly created namespace on k3s holds exactly these two and nothing else; a mesh or another platform
// adds objects of its own, and those are found as foreign, which keeps the namespace.
var platformObjects = []platformObject{
	{group: "", kind: "ConfigMap", name: "kube-root-ca.crt"},
	{group: "", kind: "ServiceAccount", name: "default"},
}

// isPlatformObject reports whether the cluster itself puts this object in every namespace.
func isPlatformObject(resource kube.Resource, name string) bool {
	return slices.Contains(platformObjects, platformObject{group: resource.Group, kind: resource.Kind, name: name})
}

// isTransientRecord reports whether the resource holds events, which record what happened in the namespace and
// are not content to protect. The core API and events.k8s.io both serve them.
func isTransientRecord(resource kube.Resource) bool {
	return (resource.Name == "events") && ((resource.Group == "") || (resource.Group == "events.k8s.io"))
}

// namespaceInventory is what a look through the namespace found.
type namespaceInventory struct {
	// foreign are the objects without the Vord label, apart from the ones the cluster creates itself, each as
	// its kind and name.
	foreign []string

	// unverified are the kinds and groups that could not be read, each with the reason. The namespace cannot be
	// shown to be free of anyone else's objects while there is one.
	unverified []string
}

// inspectNamespace lists every namespaced kind the API serves in the reporter namespace and collects what is not
// the agent's. A kind the cluster does not serve (404) has no objects. Any other failure to list a kind, or to
// describe a group, is recorded as unverified rather than failing the command, because the namespace is then kept.
// Discovery failing altogether, and the context ending, are errors, as for any other call to the API.
func inspectNamespace(ctx context.Context, client *kube.Client) (namespaceInventory, error) {
	discovery, err := client.DiscoverNamespaced(ctx)
	if err != nil {
		return namespaceInventory{}, err
	}

	var inventory namespaceInventory

	for _, group := range discovery.Unverified {
		inventory.unverified = append(inventory.unverified, group.GroupVersion+": "+group.Err.Error())
	}

	for _, resource := range discovery.Resources {
		if isTransientRecord(resource) {
			continue
		}

		found, err := listForeign(ctx, client, resource)
		if err != nil {
			if kube.IsNotFound(err) {
				continue
			}

			if ctx.Err() != nil {
				return namespaceInventory{}, err
			}

			inventory.unverified = append(inventory.unverified, resource.String()+": "+err.Error())

			continue
		}

		inventory.foreign = append(inventory.foreign, found...)
	}

	return inventory, nil
}

// listForeign lists one kind in the reporter namespace and returns the objects that do not carry the Vord label,
// apart from the ones the cluster creates itself.
func listForeign(ctx context.Context, client *kube.Client, resource kube.Resource) ([]string, error) {
	var found []string

	err := client.List(ctx, resource.Collection(reporterNamespace), nil, listPageSize, func(body []byte) error {
		var page objectList
		if err := json.Unmarshal(body, &page); err != nil {
			return err
		}

		for _, item := range page.Items {
			if (item.Metadata.Labels[managedByLabel] != managedByValue) && (isPlatformObject(resource, item.Metadata.Name) == false) {
				found = append(found, resource.KindName()+" "+item.Metadata.Name)
			}
		}

		return nil
	}, func() { found = nil })
	if err != nil {
		return nil, err
	}

	return found, nil
}

// removeNamespace deletes the namespace when it carries the Vord label and holds nothing that is not the
// agent's, and returns what it left in place, or "" when the namespace is gone. A namespace the agent did not
// create is kept, as is one that holds an object without the label, which deleting it would destroy, and one that
// cannot be shown to hold none because a kind or group could not be read. A namespace that is kept anyway because
// an object the agent left in place lives in it is not inspected. The objects are logged by kind and name and the
// unverified kinds with their reason.
func removeNamespace(ctx context.Context, client *kube.Client, keptInNamespace bool) (string, error) {
	namespace, err := client.GetRaw(ctx, namespacePath)
	if err != nil {
		if kube.IsNotFound(err) {
			return "", nil
		}

		return "", err
	}

	if isManaged(namespace) == false {
		return keptNamespace, nil
	}

	if keptInNamespace {
		slog.Info("the namespace is kept because an object in it is not the agent's")

		return keptNamespace, nil
	}

	inventory, err := inspectNamespace(ctx, client)
	if err != nil {
		return "", err
	}

	switch {
	case len(inventory.foreign) > 0:
		slog.Info("the namespace is kept because it holds objects that are not the agent's",
			"objects", strings.Join(inventory.foreign, ", "), "unverified", strings.Join(inventory.unverified, "; "))

		return keptNamespace, nil
	case len(inventory.unverified) > 0:
		slog.Info("the namespace is kept because its contents could not be verified", "unverified", strings.Join(inventory.unverified, "; "))

		return keptNamespaceUnverified, nil
	}

	outcome, err := deleteManaged(ctx, client, namespacePath)
	if err != nil {
		return "", err
	}

	if outcome == outcomeUnmanaged {
		return keptNamespace, nil
	}

	return "", nil
}
