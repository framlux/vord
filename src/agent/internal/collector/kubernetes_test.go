// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"syscall"
	"testing"
	"time"

	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"

	"github.com/framlux/vord/internal/db"
	pb "github.com/framlux/vord/internal/proto/agent"
)

// k8sHost is a fake machine: a directory tree standing in for the filesystem root, plus fakes for the
// version commands and the hostname the collector asks the operating system for.
type k8sHost struct {
	t           *testing.T
	root        string
	dataDir     string
	hostname    string
	hostnameErr error
	outputs     map[string]string
	failures    map[string]error
	calls       []string
}

func newK8sHost(t *testing.T) *k8sHost {
	t.Helper()

	return &k8sHost{
		t:        t,
		root:     t.TempDir(),
		dataDir:  t.TempDir(),
		hostname: "fallback-host",
		outputs:  map[string]string{},
		failures: map[string]error{},
	}
}

// path returns where a root-relative path lives on disk.
func (h *k8sHost) path(p string) string {
	return filepath.Join(h.root, p)
}

func (h *k8sHost) write(p string, data []byte) {
	h.t.Helper()

	full := h.path(p)
	if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
		h.t.Fatalf("create directory for %s: %v", p, err)
	}

	if err := os.WriteFile(full, data, 0o644); err != nil {
		h.t.Fatalf("write %s: %v", p, err)
	}
}

func (h *k8sHost) writeString(p string, data string) {
	h.t.Helper()

	h.write(p, []byte(data))
}

// writeUnreadable creates a file the collector may stat but not open, for files it must never read.
func (h *k8sHost) writeUnreadable(p string) {
	h.t.Helper()

	full := h.path(p)
	if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
		h.t.Fatalf("create directory for %s: %v", p, err)
	}

	if err := os.WriteFile(full, []byte("credential that must not be read"), 0o000); err != nil {
		h.t.Fatalf("write %s: %v", p, err)
	}
}

func (h *k8sHost) mkdir(p string) {
	h.t.Helper()

	if err := os.MkdirAll(h.path(p), 0o755); err != nil {
		h.t.Fatalf("create directory %s: %v", p, err)
	}
}

// binary creates an executable at p whose fake run prints output.
func (h *k8sHost) binary(p string, output string) {
	h.t.Helper()

	h.writeString(p, "#!/bin/sh\n")
	h.outputs[h.path(p)] = output
}

// failingBinary creates an executable at p whose fake run fails with err.
func (h *k8sHost) failingBinary(p string, err error) {
	h.t.Helper()

	h.writeString(p, "#!/bin/sh\n")
	h.failures[h.path(p)] = err
}

func (h *k8sHost) exec(_ context.Context, name string, args ...string) ([]byte, error) {
	h.calls = append(h.calls, strings.TrimSpace(name+" "+strings.Join(args, " ")))

	if err, failed := h.failures[name]; failed {
		return []byte("partial output"), err
	}

	output, found := h.outputs[name]
	if found == false {
		return nil, fmt.Errorf("unexpected command %s", name)
	}

	return []byte(output), nil
}

// wantCalls asserts the exact commands the collector ran, each given as a root-relative binary path
// followed by its arguments.
func (h *k8sHost) wantCalls(commands ...string) {
	h.t.Helper()

	want := []string{}
	for _, command := range commands {
		want = append(want, h.path(command))
	}

	got := h.calls
	if got == nil {
		got = []string{}
	}

	if reflect.DeepEqual(got, want) == false {
		h.t.Errorf("commands run = %q, want %q", got, want)
	}
}

func (h *k8sHost) collector() *KubernetesCollector {
	return &KubernetesCollector{
		root:    h.root,
		dataDir: h.dataDir,
		execCmd: h.exec,
		hostname: func() (string, error) {
			return h.hostname, h.hostnameErr
		},
	}
}

// collectItems runs one collection and returns the telemetry it enqueued, checking that the collector
// also recorded that it ran.
func collectItems(t *testing.T, c *KubernetesCollector, store *db.Store) []db.TelemetryQueueItem {
	t.Helper()

	if err := c.Collect(context.Background(), store); err != nil {
		t.Fatalf("Collect error: %v", err)
	}

	collectorState, err := store.GetCollectorState(c.Name())
	if err != nil {
		t.Fatalf("GetCollectorState error: %v", err)
	}

	if collectorState == nil {
		t.Errorf("Collect did not record collector state")
	}

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry error: %v", err)
	}

	return items
}

func decodeNodePayloads(t *testing.T, items []db.TelemetryQueueItem) []kubernetesNodePayload {
	t.Helper()

	payloads := []kubernetesNodePayload{}

	for _, item := range items {
		if item.ItemType != db.TelemetryKubernetesNode {
			t.Errorf("item type = %d, want %d", item.ItemType, db.TelemetryKubernetesNode)
		}

		var payload kubernetesNodePayload
		if err := json.Unmarshal([]byte(item.Payload), &payload); err != nil {
			t.Fatalf("unmarshal payload: %v", err)
		}

		payloads = append(payloads, payload)
	}

	return payloads
}

// collectNodes runs one collection and returns the node reports it enqueued.
func collectNodes(t *testing.T, c *KubernetesCollector, store *db.Store) []kubernetesNodePayload {
	t.Helper()

	return decodeNodePayloads(t, collectItems(t, c, store))
}

// collectOne runs one collection on a fresh queue and requires exactly one node report.
func collectOne(t *testing.T, h *k8sHost) kubernetesNodePayload {
	t.Helper()

	payloads := collectNodes(t, h.collector(), newTestStore(t))
	if len(payloads) != 1 {
		t.Fatalf("expected 1 node report, got %d", len(payloads))
	}

	return payloads[0]
}

func assertNodePayload(t *testing.T, got kubernetesNodePayload, want kubernetesNodePayload) {
	t.Helper()

	if got != want {
		t.Errorf("payload = %+v\nwant      %+v", got, want)
	}
}

func fingerprintOf(c testCert) string {
	return sha256Hex(c.cert.Raw)
}

func b64(data []byte) string {
	return base64.StdEncoding.EncodeToString(data)
}

// embeddedKubeconfig is a kubeconfig that carries the CA and the client certificate inline.
func embeddedKubeconfig(ca testCert, client testCert) string {
	return fmt.Sprintf(kubeconfigEmbeddedTemplate, b64(ca.certPEM), b64(client.certPEM), b64(client.keyPEM))
}

const kubeconfigPathTemplate = `apiVersion: v1
kind: Config
current-context: default
clusters:
- name: kubernetes
  cluster:
    server: https://10.0.0.1:6443
    certificate-authority: %q
contexts:
- name: default
  context:
    cluster: kubernetes
    user: kubelet
users:
- name: kubelet
  user:
    client-certificate: %q
    client-key: /etc/kubernetes/pki/never-read.key
`

// pathKubeconfig is a kubeconfig that points at its CA and client certificate by path.
func pathKubeconfig(caPath string, certPath string) string {
	return fmt.Sprintf(kubeconfigPathTemplate, caPath, certPath)
}

// caEmbeddedKubeconfig is a kubeconfig that carries the CA inline and points at the client certificate by path.
func caEmbeddedKubeconfig(ca testCert, certPath string) string {
	return strings.Replace(pathKubeconfig("", certPath), `certificate-authority: ""`, "certificate-authority-data: "+b64(ca.certPEM), 1)
}

const kubeconfigTokenTemplate = `apiVersion: v1
kind: Config
current-context: default
clusters:
- name: kubernetes
  cluster:
    server: https://10.0.0.1:6443
    certificate-authority-data: %s
contexts:
- name: default
  context:
    cluster: kubernetes
    user: kubelet
users:
- name: kubelet
  user:
    token: token-value-that-must-never-be-reported
`

// tokenKubeconfig is a kubeconfig whose user authenticates by token, so it has no client certificate.
func tokenKubeconfig(ca testCert) string {
	return fmt.Sprintf(kubeconfigTokenTemplate, b64(ca.certPEM))
}

// combinedPEM is a kubelet-client-current.pem: the key block, then the certificate.
func combinedPEM(c testCert) []byte {
	return append(append([]byte{}, c.keyPEM...), c.certPEM...)
}

// k3sAgentTree lays out a k3s agent node that has no server directory.
func (h *k8sHost) k3sAgentTree(ca testCert, node testCert) {
	h.t.Helper()

	h.writeString("/var/lib/rancher/k3s/agent/kubelet.kubeconfig", "kubelet kubeconfig")
	h.write("/var/lib/rancher/k3s/agent/server-ca.crt", ca.certPEM)
	h.write("/var/lib/rancher/k3s/agent/client-kubelet.crt", node.certPEM)
}

// kubeadmTree lays out a kubeadm worker whose kubelet.conf embeds the CA and the client certificate.
func (h *k8sHost) kubeadmTree(ca testCert, node testCert) {
	h.t.Helper()

	h.writeString("/etc/kubernetes/kubelet.conf", embeddedKubeconfig(ca, node))
}

// Intent: a name and a 15 minute interval identify the collector to the scheduler, and the
// production constructor reads the real filesystem root and the agent data directory.
func TestKubernetesCollector_NameIntervalAndConstructor(t *testing.T) {
	c := NewKubernetesCollector("/var/lib/vord-agent")

	if c.Name() != "kubernetes" {
		t.Errorf("Name = %q, want %q", c.Name(), "kubernetes")
	}

	if c.DefaultInterval() != 15*time.Minute {
		t.Errorf("DefaultInterval = %v, want 15m", c.DefaultInterval())
	}

	if c.root != "/" {
		t.Errorf("root = %q, want %q", c.root, "/")
	}

	if c.dataDir != "/var/lib/vord-agent" {
		t.Errorf("dataDir = %q, want %q", c.dataDir, "/var/lib/vord-agent")
	}
}

// Intent: a machine with no Kubernetes on it sends nothing and runs no commands, while the
// collector still records that it ran.
func TestKubernetesCollector_NoKubernetes_SendsNothing(t *testing.T) {
	h := newK8sHost(t)

	payloads := collectNodes(t, h.collector(), newTestStore(t))

	if len(payloads) != 0 {
		t.Errorf("expected 0 telemetry items, got %d", len(payloads))
	}

	h.wantCalls()
}

// Intent: a k3s data directory that exists only because "k3s kubectl" unpacked its binaries on a
// machine that is not a node has no kubelet, no admin credential and no control plane, so nothing
// is reported and no version command runs.
func TestKubernetesCollector_K3sDataDirOnly_SendsNothing(t *testing.T) {
	h := newK8sHost(t)
	h.mkdir("/var/lib/rancher/k3s/data")
	h.binary("/usr/local/bin/k3s", "k3s version v1.30.1+k3s1 (a1b2c3d4)\n")

	payloads := collectNodes(t, h.collector(), newTestStore(t))

	if len(payloads) != 0 {
		t.Errorf("expected 0 telemetry items, got %d", len(payloads))
	}

	h.wantCalls()
}

// Intent: an empty /var/lib/kubelet, left behind by an uninstalled kubelet, is not a marker for
// the unknown layout: the marker is the kubelet certificate or kubeconfig inside it.
func TestKubernetesCollector_EmptyKubeletDir_SendsNothing(t *testing.T) {
	h := newK8sHost(t)
	h.mkdir("/var/lib/kubelet")
	h.mkdir("/var/lib/kubelet/pki")

	payloads := collectNodes(t, h.collector(), newTestStore(t))

	if len(payloads) != 0 {
		t.Errorf("expected 0 telemetry items, got %d", len(payloads))
	}
}

// Intent: detection runs on every tick, so a machine that becomes a node after the agent started is
// reported by the same collector on its next run.
func TestKubernetesCollector_AppearsAfterStart(t *testing.T) {
	h := newK8sHost(t)
	store := newTestStore(t)
	c := h.collector()

	if payloads := collectNodes(t, c, store); len(payloads) != 0 {
		t.Fatalf("expected 0 telemetry items before Kubernetes is installed, got %d", len(payloads))
	}

	h.k3sAgentTree(newTestCert(t, "k3s-server-ca", 1), newTestCert(t, "system:node:agent-1", 2))

	payloads := collectNodes(t, c, store)
	if len(payloads) != 1 {
		t.Fatalf("expected 1 telemetry item after Kubernetes is installed, got %d", len(payloads))
	}

	if payloads[0].Distro != "k3s" {
		t.Errorf("distro = %q, want %q", payloads[0].Distro, "k3s")
	}
}

// Intent: a k3s server reports control plane, kubelet, admin credential, the identity from the
// kubelet certificate, the fingerprint of the agent's CA (not the server directory's), and the
// version from the first existing binary, which is the only command run.
func TestKubernetesCollector_K3sServer(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "k3s-server-ca", 1)
	otherCA := newTestCert(t, "k3s-other-ca", 2)
	node := newTestCert(t, "system:node:k3s-server-1", 3)

	h.k3sAgentTree(ca, node)
	h.write("/var/lib/rancher/k3s/server/tls/server-ca.crt", otherCA.certPEM)
	h.writeUnreadable("/etc/rancher/k3s/k3s.yaml")
	h.binary("/usr/local/bin/k3s", "k3s version v1.30.1+k3s1 (a1b2c3d4)\ngo version go1.22.2\n")
	h.binary("/opt/bin/k3s", "k3s version v9.9.9+k3s9 (ffffffff)\n")

	assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
		Distro:                 "k3s",
		KubeletVersion:         "v1.30.1+k3s1",
		NodeName:               "k3s-server-1",
		NodeNameSource:         "certificate",
		HasKubelet:             true,
		ControlPlane:           true,
		CaFingerprint:          fingerprintOf(ca),
		AdminCredentialPresent: true,
	})

	h.wantCalls("/usr/local/bin/k3s --version")
}

// Intent: a k3s agent has no server directory, so it is not a control plane and has no admin
// credential, and its version comes from the second binary location when the first is absent.
func TestKubernetesCollector_K3sAgent(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "k3s-server-ca", 1)
	node := newTestCert(t, "system:node:k3s-agent-1", 2)

	h.k3sAgentTree(ca, node)
	h.binary("/opt/bin/k3s", "k3s version v1.30.1+k3s1 (a1b2c3d4)\n")

	assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
		Distro:         "k3s",
		KubeletVersion: "v1.30.1+k3s1",
		NodeName:       "k3s-agent-1",
		NodeNameSource: "certificate",
		HasKubelet:     true,
		CaFingerprint:  fingerprintOf(ca),
	})

	h.wantCalls("/opt/bin/k3s --version")
}

// Intent: a k3s server whose kubelet has not joined yet (no agent directory) is still reported
// through its control plane and admin credential, taking the CA from the server directory.
func TestKubernetesCollector_K3sServerWithoutKubelet(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "k3s-server-ca", 1)

	h.write("/var/lib/rancher/k3s/server/tls/server-ca.crt", ca.certPEM)
	h.writeUnreadable("/etc/rancher/k3s/k3s.yaml")

	assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
		Distro:                 "k3s",
		NodeName:               "fallback-host",
		NodeNameSource:         "hostname",
		ControlPlane:           true,
		CaFingerprint:          fingerprintOf(ca),
		AdminCredentialPresent: true,
	})
}

// Intent: an RKE2 server is a control plane when the static pod manifest or the server TLS
// directory exists, uses the RKE2 admin credential, and runs the first existing RKE2 binary.
func TestKubernetesCollector_Rke2Server(t *testing.T) {
	ca := newTestCert(t, "rke2-server-ca", 1)
	node := newTestCert(t, "system:node:rke2-server-1", 2)

	markers := map[string]func(h *k8sHost){
		"pod manifest": func(h *k8sHost) {
			h.writeString("/var/lib/rancher/rke2/agent/pod-manifests/kube-apiserver.yaml", "apiVersion: v1")
		},
		"server tls directory": func(h *k8sHost) {
			h.mkdir("/var/lib/rancher/rke2/server/tls")
		},
	}

	for name, addControlPlaneMarker := range markers {
		t.Run(name, func(t *testing.T) {
			h := newK8sHost(t)

			h.writeString("/var/lib/rancher/rke2/agent/kubelet.kubeconfig", "kubelet kubeconfig")
			h.write("/var/lib/rancher/rke2/agent/server-ca.crt", ca.certPEM)
			h.write("/var/lib/rancher/rke2/agent/client-kubelet.crt", node.certPEM)
			h.writeUnreadable("/etc/rancher/rke2/rke2.yaml")
			h.binary("/opt/rke2/bin/rke2", "rke2 version v1.30.1+rke2r1 (a1b2c3d4)\ngo version go1.22.2 X:boringcrypto\n")
			h.binary("/usr/bin/rke2", "rke2 version v9.9.9+rke2r9 (ffffffff)\n")
			addControlPlaneMarker(h)

			assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
				Distro:                 "rke2",
				KubeletVersion:         "v1.30.1+rke2r1",
				NodeName:               "rke2-server-1",
				NodeNameSource:         "certificate",
				HasKubelet:             true,
				ControlPlane:           true,
				CaFingerprint:          fingerprintOf(ca),
				AdminCredentialPresent: true,
			})

			h.wantCalls("/opt/rke2/bin/rke2 --version")
		})
	}
}

// Intent: an RKE2 agent has neither control plane marker nor an admin credential.
func TestKubernetesCollector_Rke2Agent(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "rke2-server-ca", 1)
	node := newTestCert(t, "system:node:rke2-agent-1", 2)

	h.writeString("/var/lib/rancher/rke2/agent/kubelet.kubeconfig", "kubelet kubeconfig")
	h.write("/var/lib/rancher/rke2/agent/server-ca.crt", ca.certPEM)
	h.write("/var/lib/rancher/rke2/agent/client-kubelet.crt", node.certPEM)

	assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
		Distro:         "rke2",
		NodeName:       "rke2-agent-1",
		NodeNameSource: "certificate",
		HasKubelet:     true,
		CaFingerprint:  fingerprintOf(ca),
	})
}

// Intent: the kubeadm kubelet.conf may carry the CA and client certificate inline or point at
// files (a combined key-then-certificate PEM, and the cluster CA on disk), and both forms of the
// same certificates give the same node name and CA fingerprint.
func TestKubernetesCollector_KubeadmInlineAndPathFormsAgree(t *testing.T) {
	ca := newTestCert(t, "kubernetes", 1)
	node := newTestCert(t, "system:node:kubeadm-worker-1", 2)

	inline := newK8sHost(t)
	inline.kubeadmTree(ca, node)

	byPath := newK8sHost(t)
	byPath.writeString("/etc/kubernetes/kubelet.conf",
		pathKubeconfig("/etc/kubernetes/pki/ca.crt", "/var/lib/kubelet/pki/kubelet-client-current.pem"))
	byPath.write("/etc/kubernetes/pki/ca.crt", ca.certPEM)
	byPath.write("/var/lib/kubelet/pki/kubelet-client-current.pem", combinedPEM(node))

	want := kubernetesNodePayload{
		Distro:         "kubeadm",
		NodeName:       "kubeadm-worker-1",
		NodeNameSource: "certificate",
		HasKubelet:     true,
		CaFingerprint:  fingerprintOf(ca),
	}

	assertNodePayload(t, collectOne(t, inline), want)
	assertNodePayload(t, collectOne(t, byPath), want)
}

// Intent: a relative path in a kubeconfig is resolved against the directory of the kubeconfig that
// names it, as kubectl does, not against the working directory or the root.
func TestKubernetesCollector_KubeadmRelativeKubeconfigPaths(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "kubernetes", 1)
	node := newTestCert(t, "system:node:relative-1", 2)

	h.writeString("/etc/kubernetes/kubelet.conf", pathKubeconfig("pki/ca.crt", "pki/kubelet-client.pem"))
	h.write("/etc/kubernetes/pki/ca.crt", ca.certPEM)
	h.write("/etc/kubernetes/pki/kubelet-client.pem", combinedPEM(node))

	assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
		Distro:         "kubeadm",
		NodeName:       "relative-1",
		NodeNameSource: "certificate",
		HasKubelet:     true,
		CaFingerprint:  fingerprintOf(ca),
	})
}

// Intent: when a kubeconfig holds both embedded data and a file path for the same certificate, the
// embedded data is the one used, so a stale file on disk cannot override what the kubelet really loads.
func TestKubernetesCollector_KubeconfigEmbeddedDataWinsOverPath(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "kubernetes", 1)
	node := newTestCert(t, "system:node:embedded-1", 2)
	staleCA := newTestCert(t, "stale-ca", 3)
	staleNode := newTestCert(t, "system:node:stale-1", 4)

	kubeconfig := strings.Replace(embeddedKubeconfig(ca, node),
		"    certificate-authority-data:", "    certificate-authority: /etc/kubernetes/pki/ca.crt\n    certificate-authority-data:", 1)
	kubeconfig = strings.Replace(kubeconfig,
		"    client-certificate-data:", "    client-certificate: /var/lib/kubelet/pki/stale.pem\n    client-certificate-data:", 1)

	h.writeString("/etc/kubernetes/kubelet.conf", kubeconfig)
	h.write("/etc/kubernetes/pki/ca.crt", staleCA.certPEM)
	h.write("/var/lib/kubelet/pki/stale.pem", staleNode.certPEM)

	assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
		Distro:         "kubeadm",
		NodeName:       "embedded-1",
		NodeNameSource: "certificate",
		HasKubelet:     true,
		CaFingerprint:  fingerprintOf(ca),
	})
}

// Intent: a kubeadm control plane is recognised by its static API server manifest, reports the
// admin credential by existence only, and takes its version from the first kubelet binary.
func TestKubernetesCollector_KubeadmControlPlane(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "kubernetes", 1)
	node := newTestCert(t, "system:node:kubeadm-cp-1", 2)

	h.kubeadmTree(ca, node)
	h.writeString("/etc/kubernetes/manifests/kube-apiserver.yaml", "apiVersion: v1")
	h.writeUnreadable("/etc/kubernetes/admin.conf")
	h.binary("/usr/bin/kubelet", "Kubernetes v1.30.1\n")
	h.binary("/usr/local/bin/kubelet", "Kubernetes v9.9.9\n")

	assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
		Distro:                 "kubeadm",
		KubeletVersion:         "v1.30.1",
		NodeName:               "kubeadm-cp-1",
		NodeNameSource:         "certificate",
		HasKubelet:             true,
		ControlPlane:           true,
		CaFingerprint:          fingerprintOf(ca),
		AdminCredentialPresent: true,
	})

	h.wantCalls("/usr/bin/kubelet --version")
}

// Intent: a kubeadm control plane whose admin.conf has been removed (a hardened install) still
// reports, with the admin credential marked absent.
func TestKubernetesCollector_AdminCredentialAbsent(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "kubernetes", 1)
	node := newTestCert(t, "system:node:kubeadm-cp-1", 2)

	h.kubeadmTree(ca, node)
	h.writeString("/etc/kubernetes/manifests/kube-apiserver.yaml", "apiVersion: v1")

	got := collectOne(t, h)

	if got.AdminCredentialPresent {
		t.Errorf("admin_credential_present = true, want false")
	}

	if got.ControlPlane == false {
		t.Errorf("control_plane = false, want true")
	}
}

// Intent: a k0s controller that runs no worker has no kubelet but is still reported, with its CA
// from pki/ca.crt, its admin credential, and the hostname as the node name; the CA key and admin
// credential are only stat'ed, never opened.
func TestKubernetesCollector_K0sControllerWithoutWorker(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "k0s-ca", 1)

	h.hostname = "  Controller-1\n"
	h.write("/var/lib/k0s/pki/ca.crt", ca.certPEM)
	h.writeUnreadable("/var/lib/k0s/pki/ca.key")
	h.writeUnreadable("/var/lib/k0s/pki/admin.conf")
	h.binary("/usr/local/bin/k0s", "v1.30.1+k0s.0\n")

	assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
		Distro:                 "k0s",
		KubeletVersion:         "v1.30.1+k0s.0",
		NodeName:               "controller-1",
		NodeNameSource:         "hostname",
		ControlPlane:           true,
		CaFingerprint:          fingerprintOf(ca),
		AdminCredentialPresent: true,
	})

	h.wantCalls("/usr/local/bin/k0s version")
}

// Intent: a k0s worker uses the CA embedded in its kubelet.conf even though a different, stale
// pki/ca.crt exists, and reads its client certificate from the path the kubeconfig names rather than
// the default location.
func TestKubernetesCollector_K0sWorker(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "k0s-ca", 1)
	staleCA := newTestCert(t, "k0s-stale-ca", 2)
	node := newTestCert(t, "system:node:k0s-worker-1", 3)
	staleNode := newTestCert(t, "system:node:stale-node", 4)

	h.writeString("/var/lib/k0s/kubelet.conf", caEmbeddedKubeconfig(ca, "/var/lib/k0s/kubelet/pki/worker-client.pem"))
	h.write("/var/lib/k0s/pki/ca.crt", staleCA.certPEM)
	h.write("/var/lib/k0s/kubelet/pki/worker-client.pem", combinedPEM(node))
	h.write("/var/lib/k0s/kubelet/pki/kubelet-client-current.pem", combinedPEM(staleNode))

	assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
		Distro:         "k0s",
		NodeName:       "k0s-worker-1",
		NodeNameSource: "certificate",
		HasKubelet:     true,
		CaFingerprint:  fingerprintOf(ca),
	})
}

// Intent: a MicroK8s control plane takes its CA from credentials/kubelet.config, not from the
// node's own install-time certs/ca.crt, takes the node name from certs/kubelet.crt, and reads its
// version from the snap metadata without running a command.
func TestKubernetesCollector_Microk8sControlPlane(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "microk8s-cluster-ca", 1)
	installCA := newTestCert(t, "microk8s-install-ca", 2)
	node := newTestCert(t, "system:node:mk8s-1", 3)

	h.writeString("/var/snap/microk8s/current/credentials/kubelet.config", tokenKubeconfig(ca))
	h.write("/var/snap/microk8s/current/certs/ca.crt", installCA.certPEM)
	h.write("/var/snap/microk8s/current/certs/kubelet.crt", node.certPEM)
	h.writeUnreadable("/var/snap/microk8s/current/credentials/client.config")
	h.writeString("/snap/microk8s/current/meta/snap.yaml", "name: microk8s\nversion: v1.30.1\nsummary: Kubernetes for workstations and appliances\n")

	assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
		Distro:                 "microk8s",
		KubeletVersion:         "v1.30.1",
		NodeName:               "mk8s-1",
		NodeNameSource:         "certificate",
		HasKubelet:             true,
		ControlPlane:           true,
		CaFingerprint:          fingerprintOf(ca),
		AdminCredentialPresent: true,
	})

	h.wantCalls()
}

// Intent: a MicroK8s node that has joined a cluster (the clustered lock exists) is a worker: it is
// not a control plane and reports no admin credential even though client.config is on disk.
func TestKubernetesCollector_Microk8sWorker(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "microk8s-cluster-ca", 1)
	node := newTestCert(t, "system:node:mk8s-2", 2)

	h.writeString("/var/snap/microk8s/current/credentials/kubelet.config", tokenKubeconfig(ca))
	h.write("/var/snap/microk8s/current/certs/kubelet.crt", node.certPEM)
	h.writeUnreadable("/var/snap/microk8s/current/credentials/client.config")
	h.writeString("/var/snap/microk8s/current/var/lock/clustered.lock", "")

	assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
		Distro:         "microk8s",
		NodeName:       "mk8s-2",
		NodeNameSource: "certificate",
		HasKubelet:     true,
		CaFingerprint:  fingerprintOf(ca),
	})
}

// Intent: a machine that only has a kubelet certificate or kubeconfig under /var/lib/kubelet is
// reported as the unknown distro with no control plane or admin credential and no version, with the
// CA taken from /etc/kubernetes/pki/ca.crt when present.
func TestKubernetesCollector_UnknownLayout(t *testing.T) {
	ca := newTestCert(t, "kubernetes", 1)
	node := newTestCert(t, "system:node:unknown-1", 2)

	t.Run("kubelet certificate only", func(t *testing.T) {
		h := newK8sHost(t)
		h.write("/var/lib/kubelet/pki/kubelet-client-current.pem", combinedPEM(node))
		h.binary("/usr/bin/kubelet", "Kubernetes v1.30.1\n")

		assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
			Distro:         "unknown",
			NodeName:       "unknown-1",
			NodeNameSource: "certificate",
			HasKubelet:     true,
		})

		h.wantCalls()
	})

	t.Run("kubelet kubeconfig with a cluster CA", func(t *testing.T) {
		h := newK8sHost(t)
		h.writeString("/var/lib/kubelet/kubeconfig", "kubelet kubeconfig")
		h.write("/etc/kubernetes/pki/ca.crt", ca.certPEM)

		assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
			Distro:         "unknown",
			NodeName:       "fallback-host",
			NodeNameSource: "hostname",
			HasKubelet:     true,
			CaFingerprint:  fingerprintOf(ca),
		})
	})
}

// Intent: the first matching marker decides the distro, in a fixed order, so a machine that carries
// both k3s and kubeadm files is reported once, as k3s.
func TestKubernetesCollector_FirstMatchingMarkerWins(t *testing.T) {
	h := newK8sHost(t)
	k3sCA := newTestCert(t, "k3s-server-ca", 1)
	kubeadmCA := newTestCert(t, "kubernetes", 2)
	node := newTestCert(t, "system:node:both-1", 3)

	h.k3sAgentTree(k3sCA, node)
	h.kubeadmTree(kubeadmCA, node)

	got := collectOne(t, h)

	if got.Distro != "k3s" {
		t.Errorf("distro = %q, want %q", got.Distro, "k3s")
	}

	if got.CaFingerprint != fingerprintOf(k3sCA) {
		t.Errorf("ca_fingerprint = %q, want the k3s CA %q", got.CaFingerprint, fingerprintOf(k3sCA))
	}
}

// Intent: a CA file of garbage bytes does not stop the report: the node is still sent with an
// empty fingerprint and every other field set.
func TestKubernetesCollector_UnreadableCA(t *testing.T) {
	h := newK8sHost(t)
	node := newTestCert(t, "system:node:k3s-agent-1", 1)

	h.writeString("/var/lib/rancher/k3s/agent/kubelet.kubeconfig", "kubelet kubeconfig")
	h.write("/var/lib/rancher/k3s/agent/server-ca.crt", []byte("\x00\x01 this is not a certificate \xff\xfe"))
	h.write("/var/lib/rancher/k3s/agent/client-kubelet.crt", node.certPEM)
	h.writeUnreadable("/etc/rancher/k3s/k3s.yaml")

	assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
		Distro:                 "k3s",
		NodeName:               "k3s-agent-1",
		NodeNameSource:         "certificate",
		HasKubelet:             true,
		AdminCredentialPresent: true,
	})
}

// Intent: a CA source that is missing, is not a regular file, is garbage, or is not a certificate
// falls through to the next source in order rather than ending the search.
func TestKubernetesCollector_CaSourceFallsThroughToNextSource(t *testing.T) {
	ca := newTestCert(t, "k3s-server-ca", 1)
	node := newTestCert(t, "system:node:k3s-agent-1", 2)

	firstSources := map[string]func(h *k8sHost){
		"missing":     func(h *k8sHost) {},
		"directory":   func(h *k8sHost) { h.mkdir("/var/lib/rancher/k3s/agent/server-ca.crt") },
		"garbage":     func(h *k8sHost) { h.writeString("/var/lib/rancher/k3s/agent/server-ca.crt", "garbage") },
		"private key": func(h *k8sHost) { h.write("/var/lib/rancher/k3s/agent/server-ca.crt", node.keyPEM) },
	}

	for name, firstSource := range firstSources {
		t.Run(name, func(t *testing.T) {
			h := newK8sHost(t)
			h.writeString("/var/lib/rancher/k3s/agent/kubelet.kubeconfig", "kubelet kubeconfig")
			h.write("/var/lib/rancher/k3s/agent/client-kubelet.crt", node.certPEM)
			h.write("/var/lib/rancher/k3s/server/tls/server-ca.crt", ca.certPEM)
			firstSource(h)

			got := collectOne(t, h)

			if got.CaFingerprint != fingerprintOf(ca) {
				t.Errorf("ca_fingerprint = %q, want the fallback CA %q", got.CaFingerprint, fingerprintOf(ca))
			}
		})
	}
}

// Intent: a named pipe at a certificate path is never opened, because opening one blocks until a
// writer appears and would hang the collector; the pipe is treated as unreadable and the next source
// is used. A writer is parked on the pipe, so a collector that did open it would read its certificate.
func TestKubernetesCollector_NamedPipeAtCertificatePathIsSkipped(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "k3s-server-ca", 1)
	node := newTestCert(t, "system:node:k3s-agent-1", 2)
	pipeCA := newTestCert(t, "pipe-ca", 3)

	h.writeString("/var/lib/rancher/k3s/agent/kubelet.kubeconfig", "kubelet kubeconfig")
	h.write("/var/lib/rancher/k3s/agent/client-kubelet.crt", node.certPEM)
	h.write("/var/lib/rancher/k3s/server/tls/server-ca.crt", ca.certPEM)

	pipe := h.path("/var/lib/rancher/k3s/agent/server-ca.crt")
	if err := syscall.Mkfifo(pipe, 0o644); err != nil {
		t.Fatalf("create named pipe: %v", err)
	}

	writerDone := make(chan struct{})

	go func() {
		defer close(writerDone)

		writer, err := os.OpenFile(pipe, os.O_WRONLY, 0)
		if err != nil {
			return
		}
		defer writer.Close()

		_, _ = writer.Write(pipeCA.certPEM)
	}()

	t.Cleanup(func() {
		reader, err := os.OpenFile(pipe, os.O_RDONLY|syscall.O_NONBLOCK, 0)
		if err != nil {
			t.Errorf("release named pipe writer: %v", err)

			return
		}

		<-writerDone
		reader.Close()
	})

	got := collectOne(t, h)

	if got.CaFingerprint != fingerprintOf(ca) {
		t.Errorf("ca_fingerprint = %q, want the fallback CA %q", got.CaFingerprint, fingerprintOf(ca))
	}
}

// Intent: every file the collector reads is capped at 1 MiB: a certificate file of exactly that
// size is read, and one a byte larger is treated as unreadable, for a CA and a kubelet certificate alike.
func TestKubernetesCollector_FilesOverOneMebibyteAreUnreadable(t *testing.T) {
	ca := newTestCert(t, "k3s-server-ca", 1)
	node := newTestCert(t, "system:node:k3s-agent-1", 2)

	pad := func(certPEM []byte, size int) []byte {
		return append(append([]byte{}, certPEM...), []byte(strings.Repeat("\n", size-len(certPEM)))...)
	}

	cases := map[string]struct {
		size            int
		wantFingerprint string
		wantName        string
		wantSource      string
	}{
		"exactly the cap": {1 << 20, fingerprintOf(ca), "k3s-agent-1", "certificate"},
		"one byte over":   {1<<20 + 1, "", "fallback-host", "hostname"},
	}

	for name, tc := range cases {
		t.Run(name, func(t *testing.T) {
			h := newK8sHost(t)
			h.writeString("/var/lib/rancher/k3s/agent/kubelet.kubeconfig", "kubelet kubeconfig")
			h.write("/var/lib/rancher/k3s/agent/server-ca.crt", pad(ca.certPEM, tc.size))
			h.write("/var/lib/rancher/k3s/agent/client-kubelet.crt", pad(node.certPEM, tc.size))

			got := collectOne(t, h)

			if got.CaFingerprint != tc.wantFingerprint {
				t.Errorf("ca_fingerprint = %q, want %q", got.CaFingerprint, tc.wantFingerprint)
			}

			if (got.NodeName != tc.wantName) || (got.NodeNameSource != tc.wantSource) {
				t.Errorf("node = %q from %q, want %q from %q", got.NodeName, got.NodeNameSource, tc.wantName, tc.wantSource)
			}
		})
	}

	t.Run("kubeconfig over the cap", func(t *testing.T) {
		h := newK8sHost(t)
		kubeconfig := embeddedKubeconfig(ca, node) + "# " + strings.Repeat("x", 1<<20) + "\n"
		h.writeString("/etc/kubernetes/kubelet.conf", kubeconfig)
		h.write("/etc/kubernetes/pki/ca.crt", ca.certPEM)

		got := collectOne(t, h)

		if got.NodeNameSource != "hostname" {
			t.Errorf("node_name_source = %q, want %q from an unreadable kubeconfig", got.NodeNameSource, "hostname")
		}

		if got.CaFingerprint != fingerprintOf(ca) {
			t.Errorf("ca_fingerprint = %q, want the fallback CA %q", got.CaFingerprint, fingerprintOf(ca))
		}
	})
}

// Intent: a kubelet.conf that does not parse is skipped, and the default CA and kubelet client
// certificate locations are used instead.
func TestKubernetesCollector_MalformedKubeconfigFallsThroughToDefaults(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "kubernetes", 1)
	node := newTestCert(t, "system:node:kubeadm-worker-1", 2)

	h.writeString("/etc/kubernetes/kubelet.conf", "clusters: [unterminated")
	h.write("/etc/kubernetes/pki/ca.crt", ca.certPEM)
	h.write("/var/lib/kubelet/pki/kubelet-client-current.pem", combinedPEM(node))

	assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
		Distro:         "kubeadm",
		NodeName:       "kubeadm-worker-1",
		NodeNameSource: "certificate",
		HasKubelet:     true,
		CaFingerprint:  fingerprintOf(ca),
	})
}

// Intent: with no kubelet certificate the node name is the lowercased, trimmed hostname, whether the
// certificate is absent, the kubeconfig authenticates by token, or the certificate is not a node
// identity (an admin certificate is never taken as a node name).
func TestKubernetesCollector_HostnameFallback(t *testing.T) {
	ca := newTestCert(t, "kubernetes", 1)

	cases := map[string]func(h *k8sHost){
		"no kubelet certificate": func(h *k8sHost) {
			h.writeString("/var/lib/rancher/k3s/agent/kubelet.kubeconfig", "kubelet kubeconfig")
			h.write("/var/lib/rancher/k3s/agent/server-ca.crt", ca.certPEM)
		},
		"token kubeconfig on kubeadm": func(h *k8sHost) {
			h.writeString("/etc/kubernetes/kubelet.conf", tokenKubeconfig(ca))
		},
		"admin certificate on kubeadm": func(h *k8sHost) {
			h.kubeadmTree(ca, newTestCert(t, "kubernetes-admin", 2))
		},
	}

	for name, build := range cases {
		t.Run(name, func(t *testing.T) {
			h := newK8sHost(t)
			h.hostname = "  Worker-7\n"
			build(h)

			got := collectOne(t, h)

			if (got.NodeName != "worker-7") || (got.NodeNameSource != "hostname") {
				t.Errorf("node = %q from %q, want %q from %q", got.NodeName, got.NodeNameSource, "worker-7", "hostname")
			}
		})
	}
}

// Intent: when there is no certificate and the hostname cannot be a node name (the lookup fails, it
// is empty, or it is not an RFC 1123 name), the collector reports an error and sends nothing rather
// than reporting a blank or unsafe name.
func TestKubernetesCollector_UnusableHostnameFailsWithoutSending(t *testing.T) {
	cases := map[string]struct {
		hostname string
		err      error
	}{
		"lookup fails": {"", errors.New("hostname unavailable")},
		"empty":        {"   ", nil},
		"underscore":   {"Build_Host", nil},
		"not utf-8":    {"host\xff\xfe", nil},
	}

	for name, tc := range cases {
		t.Run(name, func(t *testing.T) {
			h := newK8sHost(t)
			h.hostname = tc.hostname
			h.hostnameErr = tc.err
			h.writeString("/var/lib/rancher/k3s/agent/kubelet.kubeconfig", "kubelet kubeconfig")

			store := newTestStore(t)

			if err := h.collector().Collect(context.Background(), store); err == nil {
				t.Errorf("Collect returned nil, want an error for hostname %q", tc.hostname)
			}

			items, err := store.DequeueTelemetry(10)
			if err != nil {
				t.Fatalf("DequeueTelemetry error: %v", err)
			}

			if len(items) != 0 {
				t.Errorf("expected 0 telemetry items, got %d", len(items))
			}
		})
	}
}

// Intent: a k8s-reporter.json in the agent data directory marks the host as an enrolled cluster
// reporter, and its absence leaves the flag false.
func TestKubernetesCollector_ReporterEnrolled(t *testing.T) {
	ca := newTestCert(t, "kubernetes", 1)
	node := newTestCert(t, "system:node:kubeadm-worker-1", 2)

	t.Run("state file present", func(t *testing.T) {
		h := newK8sHost(t)
		h.kubeadmTree(ca, node)

		if err := os.WriteFile(filepath.Join(h.dataDir, "k8s-reporter.json"), []byte("{}"), 0o600); err != nil {
			t.Fatalf("write reporter state: %v", err)
		}

		if got := collectOne(t, h); got.ReporterEnrolled == false {
			t.Errorf("reporter_enrolled = false, want true")
		}
	})

	t.Run("state file absent", func(t *testing.T) {
		h := newK8sHost(t)
		h.kubeadmTree(ca, node)

		if got := collectOne(t, h); got.ReporterEnrolled {
			t.Errorf("reporter_enrolled = true, want false")
		}
	})

	t.Run("state file looked up in the data directory, not the root", func(t *testing.T) {
		h := newK8sHost(t)
		h.kubeadmTree(ca, node)
		h.writeString("/k8s-reporter.json", "{}")

		if got := collectOne(t, h); got.ReporterEnrolled {
			t.Errorf("reporter_enrolled = true, want false when only the root holds the file")
		}
	})
}

// Intent: a version command that fails, prints nothing parsable, or names a binary that is a
// directory leaves kubelet_version empty and never stops the report from being sent.
func TestKubernetesCollector_VersionCommandFails(t *testing.T) {
	ca := newTestCert(t, "k3s-server-ca", 1)
	node := newTestCert(t, "system:node:k3s-agent-1", 2)

	cases := map[string]func(h *k8sHost){
		"command returns an error": func(h *k8sHost) {
			h.failingBinary("/usr/local/bin/k3s", errors.New("signal: killed"))
		},
		"command times out": func(h *k8sHost) {
			h.failingBinary("/usr/local/bin/k3s", context.DeadlineExceeded)
		},
		"output is not a version": func(h *k8sHost) {
			h.binary("/usr/local/bin/k3s", "k3s: error while loading shared libraries\n")
		},
		"output is invalid utf-8": func(h *k8sHost) {
			h.binary("/usr/local/bin/k3s", "k3s version v1\xff\x01bad (x)\n")
		},
	}

	for name, build := range cases {
		t.Run(name, func(t *testing.T) {
			h := newK8sHost(t)
			h.k3sAgentTree(ca, node)
			build(h)

			assertNodePayload(t, collectOne(t, h), kubernetesNodePayload{
				Distro:         "k3s",
				NodeName:       "k3s-agent-1",
				NodeNameSource: "certificate",
				HasKubelet:     true,
				CaFingerprint:  fingerprintOf(ca),
			})

			h.wantCalls("/usr/local/bin/k3s --version")
		})
	}
}

// Intent: the enqueued JSON is accepted by protojson for the KubernetesNodeRecord message, which
// rejects unknown keys, so every payload key matches a proto field; the record also marshals as
// binary, which proves no field holds invalid UTF-8.
func TestKubernetesCollector_PayloadRoundTripsThroughProto(t *testing.T) {
	h := newK8sHost(t)
	ca := newTestCert(t, "k3s-server-ca", 1)
	node := newTestCert(t, "system:node:k3s-server-1", 2)

	h.k3sAgentTree(ca, node)
	h.mkdir("/var/lib/rancher/k3s/server/tls")
	h.writeUnreadable("/etc/rancher/k3s/k3s.yaml")
	h.binary("/usr/local/bin/k3s", "k3s version v1.30.1+k3s1 (a1b2c3d4)\n")

	if err := os.WriteFile(filepath.Join(h.dataDir, "k8s-reporter.json"), []byte("{}"), 0o600); err != nil {
		t.Fatalf("write reporter state: %v", err)
	}

	items := collectItems(t, h.collector(), newTestStore(t))
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item, got %d", len(items))
	}

	var record pb.KubernetesNodeRecord
	if err := protojson.Unmarshal([]byte(items[0].Payload), &record); err != nil {
		t.Fatalf("protojson.Unmarshal rejected the payload %s: %v", items[0].Payload, err)
	}

	want := &pb.KubernetesNodeRecord{
		Distro:                 "k3s",
		KubeletVersion:         "v1.30.1+k3s1",
		NodeName:               "k3s-server-1",
		NodeNameSource:         "certificate",
		HasKubelet:             true,
		ControlPlane:           true,
		CaFingerprint:          fingerprintOf(ca),
		AdminCredentialPresent: true,
		ReporterEnrolled:       true,
	}

	if proto.Equal(&record, want) == false {
		t.Errorf("record = %v, want %v", &record, want)
	}

	if _, err := proto.Marshal(&record); err != nil {
		t.Errorf("proto.Marshal error: %v", err)
	}
}
