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
// was checked. An object without the Vord label is left alone, the namespace above all: a namespace the agent
// did not create is not the agent's to delete. If the API cannot be reached the command fails and keeps the
// state file so that it can be run again.
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

	for _, name := range names {
		outcome, err := deleteManaged(ctx, session.client, secretsCollection+"/"+name)
		if err != nil {
			return session.classify(err).result()
		}

		if outcome == outcomeUnmanaged {
			kept = append(kept, "Secret "+name)
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
		}
	}

	outcome, err := deleteManaged(ctx, session.client, namespacePath)
	if err != nil {
		return session.classify(err).result()
	}

	if outcome == outcomeUnmanaged {
		kept = append(kept, "Namespace")
	}

	if err := DeleteState(r.dataDir); err != nil {
		return newFailure(reasonStateWriteFailed, err.Error(), err).result()
	}

	r.stateChanged(ctx)

	if len(kept) > 0 {
		slog.Info("uninstall left objects that do not carry the Vord label", "objects", strings.Join(kept, ", "))

		return commands.Result{Success: true, Message: messageUninstalledKeptUnmanaged}
	}

	return commands.Result{Success: true, Message: messageUninstalled}
}

// secretList is the part of a Secret list that uninstall reads. Everything else, the token above all, is
// dropped as the page is decoded.
type secretList struct {
	Items []struct {
		Metadata struct {
			Name   string            `json:"name"`
			Labels map[string]string `json:"labels"`
		} `json:"metadata"`
	} `json:"items"`
}

// listTokenSecrets returns the names of the labelled Secrets in the namespace that are candidates' token
// Secrets. A namespace that does not exist has none.
func listTokenSecrets(ctx context.Context, client *kube.Client) ([]string, error) {
	var names []string

	err := client.List(ctx, secretsCollection, managedBySelector(), secretListPageSize, func(body []byte) error {
		var page secretList
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
