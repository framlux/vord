// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"encoding/json"
	"log/slog"
	"regexp"
	"strings"

	"github.com/framlux/vord/internal/commands"
	"github.com/framlux/vord/internal/kube"
)

// secretListPageSize is how many Secrets one list request asks for.
const secretListPageSize = 200

// objectNamePattern is what a Kubernetes object name looks like. A name the cluster lists is put into a request
// path, so one that is not shaped like a name is skipped rather than trusted.
var objectNamePattern = regexp.MustCompile(`^[a-z0-9]([-a-z0-9.]{0,251}[a-z0-9])?$`)

// Uninstall removes everything the agent installed in the cluster, using the host's admin kubeconfig: every
// candidate's token Secret (which revokes the tokens of hosts that are paused, deleted or unreachable), then the
// Lease, the roles and bindings and the ServiceAccount, then the namespace, and finally this host's state file.
//
// Each object is deleted by its fixed name after its label has been checked, and the delete names the uid that
// was checked. An object without the Vord label is left alone. That includes the namespace, when the agent did
// not create it, and when it still holds an object that is not the agent's: deleting a namespace deletes
// everything in it, so such a namespace is kept and the command says so. If the API cannot be reached the
// command fails and keeps the state file so that it can be run again.
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

	if namespaceKept {
		kept = append(kept, "Namespace")
	}

	if err := DeleteState(r.dataDir); err != nil {
		return newFailure(reasonStateWriteFailed, "", err).result()
	}

	r.stateChanged(ctx)

	if len(kept) > 0 {
		slog.Info("uninstall left objects that do not carry the Vord label", "objects", strings.Join(kept, ", "))

		return commands.Result{Success: true, Message: messageUninstalledKeptUnmanaged}
	}

	return commands.Result{Success: true, Message: messageUninstalled}
}

// listTokenSecrets returns the names of the labelled Secrets in the namespace that are candidates' token
// Secrets. A namespace that does not exist has none.
func listTokenSecrets(ctx context.Context, client *kube.Client) ([]string, error) {
	var names []string

	err := client.List(ctx, secretsCollection, managedBySelector(), secretListPageSize, func(body []byte) error {
		var page objectList
		if err := json.Unmarshal(body, &page); err != nil {
			return err
		}

		for _, item := range page.Items {
			name := item.Metadata.Name

			if strings.HasPrefix(name, tokenSecretPrefix) && (item.Metadata.Labels[managedByLabel] == managedByValue) && objectNamePattern.MatchString(name) {
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

// namespaceContents lists the namespaced collections that deleting the namespace would destroy.
var namespaceContents = []struct{ kind, path string }{
	{"ConfigMap", "/api/v1/namespaces/" + reporterNamespace + "/configmaps"},
	{"Secret", secretsCollection},
	{"ServiceAccount", serviceAccountsCollection},
	{"Service", "/api/v1/namespaces/" + reporterNamespace + "/services"},
	{"PersistentVolumeClaim", "/api/v1/namespaces/" + reporterNamespace + "/persistentvolumeclaims"},
	{"Pod", "/api/v1/namespaces/" + reporterNamespace + "/pods"},
	{"Deployment", "/apis/apps/v1/namespaces/" + reporterNamespace + "/deployments"},
	{"DaemonSet", "/apis/apps/v1/namespaces/" + reporterNamespace + "/daemonsets"},
	{"StatefulSet", "/apis/apps/v1/namespaces/" + reporterNamespace + "/statefulsets"},
	{"Job", "/apis/batch/v1/namespaces/" + reporterNamespace + "/jobs"},
	{"CronJob", "/apis/batch/v1/namespaces/" + reporterNamespace + "/cronjobs"},
	{"Ingress", "/apis/networking.k8s.io/v1/namespaces/" + reporterNamespace + "/ingresses"},
	{"NetworkPolicy", "/apis/networking.k8s.io/v1/namespaces/" + reporterNamespace + "/networkpolicies"},
	{"Lease", leasesCollection},
	{"Role", rolesCollection},
	{"RoleBinding", roleBindingsCollection},
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

// isPlatformObject reports whether the cluster itself puts this object in every namespace, which makes it
// nobody's to protect.
func isPlatformObject(kind string, name string) bool {
	return ((kind == "ConfigMap") && (name == "kube-root-ca.crt")) || ((kind == "ServiceAccount") && (name == "default"))
}

// foreignObjects lists the objects in the namespace that do not carry the Vord label, apart from the ones the
// cluster creates itself. A collection the cluster does not serve has none; any other failure to list one is an
// error, because the namespace cannot then be shown to hold nothing of anyone else's.
func foreignObjects(ctx context.Context, client *kube.Client) ([]string, error) {
	var foreign []string

	for _, collection := range namespaceContents {
		var found []string

		err := client.List(ctx, collection.path, nil, secretListPageSize, func(body []byte) error {
			var page objectList
			if err := json.Unmarshal(body, &page); err != nil {
				return err
			}

			for _, item := range page.Items {
				if (item.Metadata.Labels[managedByLabel] != managedByValue) && (isPlatformObject(collection.kind, item.Metadata.Name) == false) {
					found = append(found, collection.kind+" "+item.Metadata.Name)
				}
			}

			return nil
		}, func() { found = nil })
		if err != nil {
			if kube.IsNotFound(err) {
				continue
			}

			return nil, err
		}

		foreign = append(foreign, found...)
	}

	return foreign, nil
}

// removeNamespace deletes the namespace when it carries the Vord label and holds nothing that is not the
// agent's, and reports whether it was kept. A namespace the agent did not create is kept, as is one that holds
// an object without the label, which deleting it would destroy; the objects are logged by kind and name.
func removeNamespace(ctx context.Context, client *kube.Client, keptInNamespace bool) (bool, error) {
	namespace, err := client.GetRaw(ctx, namespacePath)
	if err != nil {
		if kube.IsNotFound(err) {
			return false, nil
		}

		return false, err
	}

	if isManaged(namespace) == false {
		return true, nil
	}

	foreign, err := foreignObjects(ctx, client)
	if err != nil {
		return false, err
	}

	if (len(foreign) > 0) || keptInNamespace {
		slog.Info("the namespace is kept because it holds objects that are not the agent's", "objects", strings.Join(foreign, ", "))

		return true, nil
	}

	outcome, err := deleteManaged(ctx, client, namespacePath)
	if err != nil {
		return false, err
	}

	return outcome == outcomeUnmanaged, nil
}
