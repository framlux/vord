// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

//go:build k3s_integration

package kubereporter_test

import (
	"bytes"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/framlux/vord/internal/kube"
	"github.com/framlux/vord/internal/kubelocal"
	"github.com/framlux/vord/internal/kubereporter"
)

// serverLine finds the server entry of a kubeconfig.
var serverLine = regexp.MustCompile(`(?m)^([ \t]*server:[ \t]*)\S+[ \t]*$`)

// k3sCluster is the k3s container and the credentials to reach it.
type k3sCluster struct {
	kubeconfig    string
	config        kube.Config
	admin         *kube.Client
	caFingerprint string
	rawClient     *http.Client
}

// startK3s starts k3s in a Podman container, waits for its admin kubeconfig and for the node to be Ready, and
// copies the kubeconfig out with its server pointed at the published port. The container is removed when the test
// ends. Without podman the test skips.
func startK3s(t *testing.T) *k3sCluster {
	t.Helper()

	podman, err := exec.LookPath("podman")
	if err != nil {
		t.Skip("podman is not on PATH; this test starts k3s in a Podman container, so it cannot run here")
	}

	name := "vord-k3s-it-" + randomHex(t, 4)

	t.Cleanup(func() {
		if t.Failed() {
			logs, _ := exec.Command(podman, "logs", "--tail", "40", name).CombinedOutput()
			t.Logf("the last lines of the k3s log:\n%s", logs)
		}

		if out, err := exec.Command(podman, "rm", "--force", "--volumes", "--ignore", name).CombinedOutput(); err != nil {
			t.Logf("could not remove the k3s container %s: %v\n%s", name, err, out)
		}
	})

	runPodman(t, podman, "run", "--privileged", "--detach", "--rm",
		"--name", name,
		"--label", "vord-test=k3s-integration",
		"--publish", "127.0.0.1::6443",
		k3sImage, "server", "--disable=traefik", "--disable=servicelb", "--disable=metrics-server")

	mapping := strings.SplitN(runPodman(t, podman, "port", name, "6443/tcp"), "\n", 2)[0]

	_, port, err := net.SplitHostPort(strings.TrimSpace(mapping))
	if err != nil {
		t.Fatalf("podman port gave %q: %v", mapping, err)
	}

	eventually(t, 2*time.Minute, "k3s to write its admin kubeconfig", func() bool {
		return exec.Command(podman, "exec", name, "test", "-s", k3sKubeconfigFile).Run() == nil
	})

	local := filepath.Join(t.TempDir(), "k3s.yaml")
	runPodman(t, podman, "cp", name+":"+k3sKubeconfigFile, local)

	original, err := os.ReadFile(local)
	if err != nil {
		t.Fatalf("read the copied kubeconfig: %v", err)
	}

	rewritten := serverLine.ReplaceAll(original, []byte("${1}https://127.0.0.1:"+port))
	if bytes.Contains(rewritten, []byte("https://127.0.0.1:"+port)) == false {
		t.Fatalf("the copied kubeconfig has no server entry to point at port %s", port)
	}

	if err := os.WriteFile(local, rewritten, 0o600); err != nil {
		t.Fatalf("write the kubeconfig: %v", err)
	}

	config, err := kube.LoadKubeconfig(local, nil)
	if err != nil {
		t.Fatalf("load the kubeconfig: %v", err)
	}

	admin, err := kube.New(config)
	if err != nil {
		t.Fatalf("build the admin client: %v", err)
	}

	fingerprint, err := kubelocal.CAFingerprint(config.CAData)
	if err != nil {
		t.Fatalf("fingerprint of the cluster CA: %v", err)
	}

	rawClient, err := newRawClient(config)
	if err != nil {
		t.Fatalf("build the raw client: %v", err)
	}

	eventually(t, 3*time.Minute, "the k3s node to be Ready", func() bool {
		nodes, err := admin.GetRaw(context.Background(), "/api/v1/nodes")

		return (err == nil) && anyNodeReady(nodes)
	})

	return &k3sCluster{kubeconfig: local, config: config, admin: admin, caFingerprint: fingerprint, rawClient: rawClient}
}

// newRawClient builds an HTTP client that authenticates like the admin kubeconfig, for the calls whose exact
// status code is the question and not just whether they failed.
func newRawClient(config kube.Config) (*http.Client, error) {
	roots := x509.NewCertPool()
	if roots.AppendCertsFromPEM(config.CAData) == false {
		return nil, errors.New("the cluster CA holds no certificate")
	}

	pair, err := tls.X509KeyPair(config.ClientCert, config.ClientKey)
	if err != nil {
		return nil, err
	}

	return &http.Client{
		Timeout: 30 * time.Second,
		Transport: &http.Transport{
			TLSClientConfig: &tls.Config{MinVersion: tls.VersionTLS12, RootCAs: roots, Certificates: []tls.Certificate{pair}},
		},
	}, nil
}

// factsSource says this host is a control plane node with an admin kubeconfig at the copied path, which is what
// lets the reporter commands run on a machine that is not a k3s server.
func (k *k3sCluster) factsSource() kubereporter.FactsSource {
	facts := kubelocal.Facts{
		Distro:                 "k3s",
		NodeName:               "k3s-integration",
		NodeNameSource:         "hostname",
		CaFingerprint:          k.caFingerprint,
		HasKubelet:             true,
		ControlPlane:           true,
		AdminCredentialPresent: true,
		AdminKubeconfigPath:    k.kubeconfig,
	}

	return func(context.Context) (kubelocal.Facts, bool) { return facts, true }
}

// raw sends one request as the cluster admin and returns the HTTP status and the JSON object that came back.
func (k *k3sCluster) raw(t *testing.T, method string, path string, body any) (int, map[string]any) {
	t.Helper()

	var payload io.Reader

	if body != nil {
		encoded, err := json.Marshal(body)
		if err != nil {
			t.Fatalf("encode the %s %s body: %v", method, path, err)
		}

		payload = bytes.NewReader(encoded)
	}

	request, err := http.NewRequest(method, k.config.Server+path, payload)
	if err != nil {
		t.Fatalf("build %s %s: %v", method, path, err)
	}

	request.Header.Set("Accept", "application/json")

	if body != nil {
		request.Header.Set("Content-Type", "application/json")
	}

	response, err := k.rawClient.Do(request)
	if err != nil {
		t.Fatalf("%s %s: %v", method, path, err)
	}

	defer func() { _ = response.Body.Close() }()

	data, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatalf("read the %s %s response: %v", method, path, err)
	}

	decoded := map[string]any{}
	_ = json.Unmarshal(data, &decoded)

	return response.StatusCode, decoded
}

func (k *k3sCluster) mustGet(t *testing.T, path string) map[string]any {
	t.Helper()

	obj, err := k.admin.GetRaw(context.Background(), path)
	if err != nil {
		t.Fatalf("GET %s: %v", path, err)
	}

	return obj
}

// deleteObject deletes the object at path with the uid precondition uninstall uses.
func (k *k3sCluster) deleteObject(t *testing.T, path string) {
	t.Helper()

	uid := stringAt(k.mustGet(t, path), "metadata", "uid")

	if err := k.admin.Do(context.Background(), http.MethodDelete, path, nil, deleteOptions(uid), nil); err != nil {
		t.Fatalf("DELETE %s: %v", path, err)
	}
}

// leaseView is the part of the reporter Lease the test looks at.
type leaseView struct {
	holder      string
	transitions int64
	renewTime   string
	uid         string
	created     string
	roleVersion string
}

func parseLease(obj map[string]any) leaseView {
	view := leaseView{
		holder:      stringAt(obj, "spec", "holderIdentity"),
		renewTime:   stringAt(obj, "spec", "renewTime"),
		uid:         stringAt(obj, "metadata", "uid"),
		created:     stringAt(obj, "metadata", "creationTimestamp"),
		roleVersion: stringAt(obj, "metadata", "annotations", roleVersionAnnotation),
	}

	if spec, ok := obj["spec"].(map[string]any); ok {
		if number, ok := spec["leaseTransitions"].(json.Number); ok {
			view.transitions, _ = number.Int64()
		}
	}

	return view
}

// lease reads the reporter Lease as the admin.
func (k *k3sCluster) lease() (leaseView, error) {
	obj, err := k.admin.GetRaw(context.Background(), leasePath)
	if err != nil {
		return leaseView{}, err
	}

	return parseLease(obj), nil
}

func (k *k3sCluster) mustLease(t *testing.T) leaseView {
	t.Helper()

	lease, err := k.lease()
	if err != nil {
		t.Fatalf("read the Lease: %v", err)
	}

	return lease
}

// names lists the names of the objects in a collection, optionally only the ones with the Vord label. A
// collection in a namespace that does not exist lists nothing.
func (k *k3sCluster) names(path string, onlyManaged bool) ([]string, error) {
	var query url.Values
	if onlyManaged {
		query = url.Values{"labelSelector": {managedByLabel + "=" + managedByValue}}
	}

	var names []string

	err := k.admin.List(context.Background(), path, query, 100, func(body []byte) error {
		var page struct {
			Items []struct {
				Metadata struct {
					Name string `json:"name"`
				} `json:"metadata"`
			} `json:"items"`
		}

		if err := json.Unmarshal(body, &page); err != nil {
			return err
		}

		for _, item := range page.Items {
			names = append(names, item.Metadata.Name)
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

// requireInstalled checks that the cluster holds exactly one of every shared object and one token Secret for each
// of the given instance ids, all with the Vord label, and that the roles are at the compiled version.
func (k *k3sCluster) requireInstalled(t *testing.T, instanceIDs ...string) {
	t.Helper()

	for _, path := range []string{namespacePath, serviceAccountPath, clusterRolePath, clusterRoleBindingPath, rolePath, roleBindingPath, leasePath} {
		obj := k.mustGet(t, path)

		if stringAt(obj, "metadata", "labels", managedByLabel) != managedByValue {
			t.Errorf("%s lacks the %s label", path, managedByLabel)
		}
	}

	for _, collection := range []struct {
		path string
		want int
	}{
		{namespacesPath, 1},
		{serviceAccountsPath, 1},
		{clusterRolesPath, 1},
		{clusterRoleBindingsPath, 1},
		{rolesPath, 1},
		{roleBindingsPath, 1},
		{leasesPath, 1},
		{secretsPath, len(instanceIDs)},
	} {
		names, err := k.names(collection.path, true)
		if err != nil {
			t.Fatalf("list %s: %v", collection.path, err)
		}

		if len(names) != collection.want {
			t.Errorf("%s holds %d labelled objects %v, want %d", collection.path, len(names), names, collection.want)
		}
	}

	for _, id := range instanceIDs {
		secret := k.mustGet(t, secretsPath+"/"+tokenSecretPrefix+id)

		if stringAt(secret, "type") != "kubernetes.io/service-account-token" {
			t.Errorf("the token Secret of %s has type %q", id, stringAt(secret, "type"))
		}
	}

	if got := k.mustLease(t).roleVersion; got != "1" {
		t.Errorf("the Lease records role version %q, want 1", got)
	}
}

// requireNothingInstalled checks that nothing the agent installed is left: the named objects are gone, nothing in
// vord-system carries the Vord label or is anything but what the cluster itself puts in a namespace, and the
// namespace is gone or terminating.
func (k *k3sCluster) requireNothingInstalled(t *testing.T) {
	t.Helper()

	for _, path := range []string{serviceAccountPath, clusterRolePath, clusterRoleBindingPath, rolePath, roleBindingPath, leasePath} {
		if _, err := k.admin.GetRaw(context.Background(), path); kube.IsNotFound(err) == false {
			t.Errorf("GET %s after uninstall = %v, want not found", path, err)
		}
	}

	for _, collection := range []string{secretsPath, configMapsPath, serviceAccountsPath, rolesPath, roleBindingsPath, leasesPath} {
		names, err := k.names(collection, false)
		if err != nil {
			t.Fatalf("list %s: %v", collection, err)
		}

		for _, name := range names {
			if (name != "default") && (name != "kube-root-ca.crt") {
				t.Errorf("%s still holds %q after uninstall", collection, name)
			}
		}
	}

	for _, collection := range []string{namespacesPath, clusterRolesPath, clusterRoleBindingsPath} {
		names, err := k.names(collection, true)
		if err != nil {
			t.Fatalf("list %s: %v", collection, err)
		}

		for _, name := range names {
			if (collection == namespacesPath) && (name == reporterNamespace) {
				continue
			}

			t.Errorf("%s still holds the labelled %q after uninstall", collection, name)
		}
	}

	namespace, err := k.admin.GetRaw(context.Background(), namespacePath)

	switch {
	case kube.IsNotFound(err):
		t.Log("after uninstall the vord-system namespace is already gone")
	case err != nil:
		t.Fatalf("GET the namespace after uninstall: %v", err)
	case stringAt(namespace, "metadata", "deletionTimestamp") == "":
		t.Errorf("after uninstall the vord-system namespace is neither gone nor terminating (phase %q)", stringAt(namespace, "status", "phase"))
	default:
		t.Log("after uninstall the vord-system namespace is terminating")
	}
}

// exercisePrecondition runs the delete-by-uid cases on one kind of object.
func (k *k3sCluster) exercisePrecondition(t *testing.T, kind string, collection string, manifest func() map[string]any) {
	t.Helper()

	ctx := context.Background()
	name := stringAt(manifest(), "metadata", "name")
	path := collection + "/" + name

	create := func() string {
		var created map[string]any

		if err := k.admin.Do(ctx, http.MethodPost, collection, nil, manifest(), &created); err != nil {
			t.Fatalf("create the %s: %v", kind, err)
		}

		return stringAt(created, "metadata", "uid")
	}

	gone := func() {
		eventually(t, namespaceGoneTimeout, "the "+kind+" to be gone", func() bool {
			_, err := k.admin.GetRaw(ctx, path)

			return kube.IsNotFound(err)
		})
	}

	refused := func(uid string, label string) {
		err := k.admin.Do(ctx, http.MethodDelete, path, nil, deleteOptions(uid), nil)

		var apiError *kube.APIError
		if errors.As(err, &apiError) == false {
			t.Fatalf("%s: a delete with %s gave %v, want a Conflict", kind, label, err)
		}

		if (apiError.Code != http.StatusConflict) || (apiError.Reason != "Conflict") || (kube.IsConflict(err) == false) {
			t.Errorf("%s: a delete with %s answers %d %q, want 409 Conflict", kind, label, apiError.Code, apiError.Reason)
		}

		t.Logf("api fact: DELETE of a %s with %s answers HTTP %d reason %q (%s)", kind, label, apiError.Code, apiError.Reason, apiError.Message)
	}

	first := create()

	refused("00000000-0000-0000-0000-000000000000", "a wrong uid")

	if still := stringAt(k.mustGet(t, path), "metadata", "uid"); still != first {
		t.Fatalf("%s: the object changed after a refused delete: uid %q, want %q", kind, still, first)
	}

	status, _ := k.raw(t, http.MethodDelete, path, deleteOptions(first))
	if status != http.StatusOK {
		t.Errorf("%s: a delete with the right uid answers %d, want 200", kind, status)
	}

	t.Logf("api fact: DELETE of a %s with the right uid answers HTTP %d", kind, status)

	gone()

	second := create()
	if second == first {
		t.Fatalf("%s: the replacement has the old uid %q", kind, first)
	}

	refused(first, "the uid of the object it replaced")

	if still := stringAt(k.mustGet(t, path), "metadata", "uid"); still != second {
		t.Fatalf("%s: the replacement was deleted by the old uid", kind)
	}

	if err := k.admin.Do(ctx, http.MethodDelete, path, nil, deleteOptions(second), nil); err != nil {
		t.Fatalf("%s: delete the replacement: %v", kind, err)
	}

	gone()
}

// remainingObjects lists every object in the reporter namespace, across every namespaced kind the API serves
// that can be listed and deleted, as "resource/name". Events are left out, as uninstall leaves them out: they
// record what happened and are not content.
func (k *k3sCluster) remainingObjects(t *testing.T) []string {
	t.Helper()

	discovery, err := k.admin.DiscoverNamespaced(context.Background())
	if err != nil {
		t.Fatalf("discover the namespaced kinds: %v", err)
	}

	var remaining []string

	for _, resource := range discovery.Resources {
		if resource.Name == "events" {
			continue
		}

		names, err := k.names(resource.Collection(reporterNamespace), false)
		if err != nil {
			t.Fatalf("list %s in %s: %v", resource, reporterNamespace, err)
		}

		for _, name := range names {
			remaining = append(remaining, resource.String()+"/"+name)
		}
	}

	slices.Sort(remaining)

	return remaining
}
