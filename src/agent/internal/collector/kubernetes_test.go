// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"math/big"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/kubelocal"
	"github.com/framlux/vord/internal/kubereporter"
	pb "github.com/framlux/vord/internal/proto/agent"
)

// kubeFixture is a fake machine for the collector: a directory tree standing in for the filesystem root and a
// separate agent data directory. Detection itself is tested in kubelocal; these tests cover what the collector
// does with its answer.
type kubeFixture struct {
	t           *testing.T
	root        string
	dataDir     string
	hostname    string
	hostnameErr error
	versionOut  string
	versionErr  error

	// reporterError is what the fake reporter runtime says about its Lease credentials; nil means no runtime.
	reporterError func() string
}

func newKubeFixture(t *testing.T) *kubeFixture {
	t.Helper()

	return &kubeFixture{t: t, root: t.TempDir(), dataDir: t.TempDir(), hostname: "fallback-host"}
}

func (f *kubeFixture) write(path string, data []byte) {
	f.t.Helper()

	full := filepath.Join(f.root, path)
	if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
		f.t.Fatalf("create directory for %s: %v", path, err)
	}

	if err := os.WriteFile(full, data, 0o644); err != nil {
		f.t.Fatalf("write %s: %v", path, err)
	}
}

func (f *kubeFixture) mkdir(path string) {
	f.t.Helper()

	if err := os.MkdirAll(filepath.Join(f.root, path), 0o755); err != nil {
		f.t.Fatalf("create directory %s: %v", path, err)
	}
}

func (f *kubeFixture) markReporterEnrolled() {
	f.t.Helper()

	if err := os.WriteFile(filepath.Join(f.dataDir, "k8s-reporter.json"), []byte("{}"), 0o600); err != nil {
		f.t.Fatalf("write reporter state: %v", err)
	}
}

// k3sNode lays out a k3s agent: a kubelet, the agent CA and a kubelet client certificate for nodeName.
func (f *kubeFixture) k3sNode(ca []byte, nodeName string) {
	f.t.Helper()

	nodeCert, _ := newKubeCert(f.t, "system:node:"+nodeName, 2)

	f.write("/var/lib/rancher/k3s/agent/kubelet.kubeconfig", []byte("kubelet kubeconfig"))
	f.write("/var/lib/rancher/k3s/agent/server-ca.crt", ca)
	f.write("/var/lib/rancher/k3s/agent/client-kubelet.crt", nodeCert)
}

// unknownNode lays out a node the detector can only classify as the unknown distro, named by the hostname.
func (f *kubeFixture) unknownNode() {
	f.t.Helper()

	f.write("/var/lib/kubelet/kubeconfig", []byte("kubelet kubeconfig"))
}

func (f *kubeFixture) collector() *KubernetesCollector {
	return &KubernetesCollector{
		reporterError: f.reporterError,
		detector: kubelocal.Detector{
			Root: f.root,
			ExecCmd: func(context.Context, string, ...string) ([]byte, error) {
				return []byte(f.versionOut), f.versionErr
			},
			Hostname: func() (string, error) {
				return f.hostname, f.hostnameErr
			},
		},
		dataDir: f.dataDir,
	}
}

// newKubeCert builds a self-signed certificate with the given common name and returns its PEM and the
// lowercase hex SHA-256 of its DER bytes. Validity uses fixed dates so the output never depends on the clock.
func newKubeCert(t *testing.T, commonName string, serial int64) ([]byte, string) {
	t.Helper()

	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}

	template := &x509.Certificate{
		SerialNumber: big.NewInt(serial),
		Subject:      pkix.Name{CommonName: commonName},
		NotBefore:    time.Date(2026, time.January, 1, 0, 0, 0, 0, time.UTC),
		NotAfter:     time.Date(2036, time.January, 1, 0, 0, 0, 0, time.UTC),
		KeyUsage:     x509.KeyUsageDigitalSignature,
	}

	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatalf("create certificate: %v", err)
	}

	sum := sha256.Sum256(der)

	return pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}), hex.EncodeToString(sum[:])
}

// collectItems runs one collection, checks that the collector recorded that it ran, and returns what it
// enqueued.
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

	return dequeueAll(t, store)
}

func dequeueAll(t *testing.T, store *db.Store) []db.TelemetryQueueItem {
	t.Helper()

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry error: %v", err)
	}

	return items
}

// collectOne runs one collection on a fresh queue and requires exactly one node report.
func collectOne(t *testing.T, f *kubeFixture) kubernetesNodePayload {
	t.Helper()

	items := collectItems(t, f.collector(), newTestStore(t))
	if len(items) != 1 {
		t.Fatalf("expected 1 node report, got %d", len(items))
	}

	if items[0].ItemType != db.TelemetryKubernetesNode {
		t.Errorf("item type = %d, want %d", items[0].ItemType, db.TelemetryKubernetesNode)
	}

	var payload kubernetesNodePayload
	if err := json.Unmarshal([]byte(items[0].Payload), &payload); err != nil {
		t.Fatalf("unmarshal payload: %v", err)
	}

	return payload
}

// Intent: a name and a 15 minute interval identify the collector to the scheduler, and the
// production constructor reads the real filesystem root and the agent data directory.
func TestKubernetesCollector_NameIntervalAndConstructor(t *testing.T) {
	c := NewKubernetesCollector("/var/lib/vord-agent", nil)

	if c.Name() != "kubernetes" {
		t.Errorf("Name = %q, want %q", c.Name(), "kubernetes")
	}

	if c.DefaultInterval() != 15*time.Minute {
		t.Errorf("DefaultInterval = %v, want 15m", c.DefaultInterval())
	}

	if c.detector.Root != "/" {
		t.Errorf("detector root = %q, want %q", c.detector.Root, "/")
	}

	if c.dataDir != "/var/lib/vord-agent" {
		t.Errorf("dataDir = %q, want %q", c.dataDir, "/var/lib/vord-agent")
	}

	withSource := NewKubernetesCollector("/var/lib/vord-agent", func() string { return "tls_ca_changed" })
	if withSource.reporterError == nil || withSource.reporterError() != "tls_ca_changed" {
		t.Errorf("the constructor did not keep the reporter error source")
	}
}

// Intent: a machine that is not a node sends nothing but the collector still records that it ran. That holds
// for no Kubernetes at all, for a k3s data directory that exists only because "k3s kubectl" unpacked its
// binaries, and for an empty /var/lib/kubelet left by an uninstalled kubelet.
func TestKubernetesCollector_NotANode_SendsNothingAndRecordsTheRun(t *testing.T) {
	cases := map[string]func(f *kubeFixture){
		"no kubernetes":     func(f *kubeFixture) {},
		"k3s data dir only": func(f *kubeFixture) { f.mkdir("/var/lib/rancher/k3s/data") },
		"empty kubelet dir": func(f *kubeFixture) { f.mkdir("/var/lib/kubelet/pki") },
	}

	for name, build := range cases {
		t.Run(name, func(t *testing.T) {
			f := newKubeFixture(t)
			build(f)

			if items := collectItems(t, f.collector(), newTestStore(t)); len(items) != 0 {
				t.Errorf("expected 0 telemetry items, got %d", len(items))
			}
		})
	}
}

// Intent: detection runs on every tick, so a machine that becomes a node after the agent started is
// reported by the same collector on its next run.
func TestKubernetesCollector_AppearsAfterStart(t *testing.T) {
	f := newKubeFixture(t)
	store := newTestStore(t)
	c := f.collector()

	if items := collectItems(t, c, store); len(items) != 0 {
		t.Fatalf("expected 0 telemetry items before Kubernetes is installed, got %d", len(items))
	}

	ca, _ := newKubeCert(t, "k3s-server-ca", 1)
	f.k3sNode(ca, "agent-1")

	items := collectItems(t, c, store)
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item after Kubernetes is installed, got %d", len(items))
	}

	var payload kubernetesNodePayload
	if err := json.Unmarshal([]byte(items[0].Payload), &payload); err != nil {
		t.Fatalf("unmarshal payload: %v", err)
	}

	if (payload.Distro != "k3s") || (payload.NodeName != "agent-1") {
		t.Errorf("payload = %+v, want the k3s node agent-1", payload)
	}
}

// Intent: when the host runs a kubelet, has no certificate to name it, and its hostname cannot be a node
// name, the collector returns the error and sends nothing rather than reporting a blank or unsafe name.
func TestKubernetesCollector_UnusableHostnameWithKubeletFailsWithoutSending(t *testing.T) {
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
			f := newKubeFixture(t)
			f.hostname = tc.hostname
			f.hostnameErr = tc.err
			f.write("/var/lib/rancher/k3s/agent/kubelet.kubeconfig", []byte("kubelet kubeconfig"))

			store := newTestStore(t)

			err := f.collector().Collect(context.Background(), store)
			if err == nil {
				t.Fatalf("Collect returned nil, want an error for hostname %q", tc.hostname)
			}

			if strings.Contains(err.Error(), "resolving Kubernetes node name") == false {
				t.Errorf("error = %q, want it to say the node name could not be resolved", err)
			}

			if items := dequeueAll(t, store); len(items) != 0 {
				t.Errorf("expected 0 telemetry items, got %d", len(items))
			}
		})
	}
}

// Intent: a control plane host without a kubelet has no node name to report, so an unusable hostname does not
// hide it: the report is still sent with an empty node name and the name source "hostname".
func TestKubernetesCollector_UnusableHostnameWithoutKubeletStillReports(t *testing.T) {
	ca, fingerprint := newKubeCert(t, "k3s-server-ca", 1)

	f := newKubeFixture(t)
	f.hostname = "Build_Host"
	f.write("/var/lib/rancher/k3s/server/tls/server-ca.crt", ca)

	got := collectOne(t, f)

	want := kubernetesNodePayload{
		Distro:         "k3s",
		NodeName:       "",
		NodeNameSource: "hostname",
		ControlPlane:   true,
		CaFingerprint:  fingerprint,
	}

	if got != want {
		t.Errorf("payload = %+v\nwant      %+v", got, want)
	}
}

// Intent: a k8s-reporter.json in the agent data directory marks the host as an enrolled cluster
// reporter, and its absence leaves the flag false.
func TestKubernetesCollector_ReporterEnrolled(t *testing.T) {
	t.Run("state file present", func(t *testing.T) {
		f := newKubeFixture(t)
		f.unknownNode()
		f.markReporterEnrolled()

		if got := collectOne(t, f); got.ReporterEnrolled == false {
			t.Errorf("reporter_enrolled = false, want true")
		}
	})

	t.Run("state file absent", func(t *testing.T) {
		f := newKubeFixture(t)
		f.unknownNode()

		if got := collectOne(t, f); got.ReporterEnrolled {
			t.Errorf("reporter_enrolled = true, want false")
		}
	})

	t.Run("state file looked up in the data directory, not the root", func(t *testing.T) {
		f := newKubeFixture(t)
		f.unknownNode()
		f.write("/k8s-reporter.json", []byte("{}"))

		if got := collectOne(t, f); got.ReporterEnrolled {
			t.Errorf("reporter_enrolled = true, want false when only the root holds the file")
		}
	})

	t.Run("state file is a directory", func(t *testing.T) {
		f := newKubeFixture(t)
		f.unknownNode()

		if err := os.Mkdir(filepath.Join(f.dataDir, "k8s-reporter.json"), 0o755); err != nil {
			t.Fatalf("create directory: %v", err)
		}

		if got := collectOne(t, f); got.ReporterEnrolled {
			t.Errorf("reporter_enrolled = true, want false for a directory")
		}
	})

	t.Run("no data directory configured", func(t *testing.T) {
		f := newKubeFixture(t)
		f.unknownNode()
		f.markReporterEnrolled()

		c := f.collector()
		c.dataDir = ""

		items := collectItems(t, c, newTestStore(t))
		if len(items) != 1 {
			t.Fatalf("expected 1 telemetry item, got %d", len(items))
		}

		if strings.Contains(items[0].Payload, `"reporter_enrolled":false`) == false {
			t.Errorf("payload = %s, want reporter_enrolled false without a data directory", items[0].Payload)
		}
	})
}

// Intent: a version command that fails leaves kubelet_version out of the payload and never stops the report
// from being sent.
func TestKubernetesCollector_VersionCommandFailsStillReports(t *testing.T) {
	ca, fingerprint := newKubeCert(t, "k3s-server-ca", 1)

	f := newKubeFixture(t)
	f.k3sNode(ca, "k3s-agent-1")
	f.write("/usr/local/bin/k3s", []byte("#!/bin/sh\n"))
	f.versionErr = errors.New("signal: killed")

	got := collectOne(t, f)

	want := kubernetesNodePayload{
		Distro:         "k3s",
		NodeName:       "k3s-agent-1",
		NodeNameSource: "certificate",
		HasKubelet:     true,
		CaFingerprint:  fingerprint,
	}

	if got != want {
		t.Errorf("payload = %+v\nwant      %+v", got, want)
	}
}

// Intent: the enqueued JSON is exactly the bytes the server was built against: key order, omitted empty
// optional fields and the flags that are always present are all part of the payload contract, and the host
// path of the admin credential that detection now returns never appears in it.
func TestKubernetesCollector_PayloadBytesAreStable(t *testing.T) {
	t.Run("control plane with every field", func(t *testing.T) {
		ca, fingerprint := newKubeCert(t, "k3s-server-ca", 1)

		f := newKubeFixture(t)
		f.k3sNode(ca, "k3s-server-1")
		f.mkdir("/var/lib/rancher/k3s/server/tls")
		f.write("/etc/rancher/k3s/k3s.yaml", []byte("admin kubeconfig"))
		f.write("/usr/local/bin/k3s", []byte("#!/bin/sh\n"))
		f.versionOut = "k3s version v1.30.1+k3s1 (a1b2c3d4)\n"
		f.markReporterEnrolled()

		items := collectItems(t, f.collector(), newTestStore(t))
		if len(items) != 1 {
			t.Fatalf("expected 1 telemetry item, got %d", len(items))
		}

		want := `{"distro":"k3s","kubelet_version":"v1.30.1+k3s1","node_name":"k3s-server-1",` +
			`"node_name_source":"certificate","has_kubelet":true,"control_plane":true,` +
			`"ca_fingerprint":"` + fingerprint + `","admin_credential_present":true,"reporter_enrolled":true}`

		if items[0].Payload != want {
			t.Errorf("payload = %s\nwant      %s", items[0].Payload, want)
		}
	})

	t.Run("worker with only the always-present fields", func(t *testing.T) {
		f := newKubeFixture(t)
		f.unknownNode()

		items := collectItems(t, f.collector(), newTestStore(t))
		if len(items) != 1 {
			t.Fatalf("expected 1 telemetry item, got %d", len(items))
		}

		want := `{"distro":"unknown","node_name":"fallback-host","node_name_source":"hostname",` +
			`"has_kubelet":true,"control_plane":false,"admin_credential_present":false,"reporter_enrolled":false}`

		if items[0].Payload != want {
			t.Errorf("payload = %s\nwant      %s", items[0].Payload, want)
		}
	})
}

// Intent: the enqueued JSON is accepted by protojson for the KubernetesNodeRecord message, which
// rejects unknown keys, so every payload key matches a proto field; the record also marshals as
// binary, which proves no field holds invalid UTF-8.
func TestKubernetesCollector_PayloadRoundTripsThroughProto(t *testing.T) {
	ca, fingerprint := newKubeCert(t, "k3s-server-ca", 1)

	f := newKubeFixture(t)
	f.k3sNode(ca, "k3s-server-1")
	f.mkdir("/var/lib/rancher/k3s/server/tls")
	f.write("/etc/rancher/k3s/k3s.yaml", []byte("admin kubeconfig"))
	f.write("/usr/local/bin/k3s", []byte("#!/bin/sh\n"))
	f.versionOut = "k3s version v1.30.1+k3s1 (a1b2c3d4)\n"
	f.markReporterEnrolled()

	items := collectItems(t, f.collector(), newTestStore(t))
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
		CaFingerprint:          fingerprint,
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

// withReporterError lays out an enrolled control plane host whose reporter runtime reports the given error.
func (f *kubeFixture) withReporterError(reason string) {
	f.t.Helper()

	ca, _ := newKubeCert(f.t, "k3s-server-ca", 1)
	f.k3sNode(ca, "k3s-server-1")
	f.write("/etc/rancher/k3s/k3s.yaml", []byte("admin kubeconfig"))
	f.reporterError = func() string { return reason }
}

// Intent: a reporter that cannot use its credentials tells the server through the node report, as the exact
// reason code, and the key is one the proto knows so the sender accepts it.
func TestKubernetesCollector_ReporterError_IsReportedWhileEnrolled(t *testing.T) {
	for _, reason := range []string{"credentials_rejected", "tls_ca_changed", "state_copied"} {
		t.Run(reason, func(t *testing.T) {
			f := newKubeFixture(t)
			f.withReporterError(reason)
			f.markReporterEnrolled()

			items := collectItems(t, f.collector(), newTestStore(t))
			if len(items) != 1 {
				t.Fatalf("expected 1 node report, got %d", len(items))
			}

			if want := `"reporter_enrolled":true,"reporter_error":"` + reason + `"}`; strings.HasSuffix(items[0].Payload, want) == false {
				t.Errorf("payload = %s, want it to end with %s", items[0].Payload, want)
			}

			var record pb.KubernetesNodeRecord
			if err := protojson.Unmarshal([]byte(items[0].Payload), &record); err != nil {
				t.Fatalf("protojson.Unmarshal rejected the payload %s: %v", items[0].Payload, err)
			}

			if record.ReporterError != reason {
				t.Errorf("record.ReporterError = %q, want %q", record.ReporterError, reason)
			}
		})
	}
}

// Intent: a healthy reporter and a host with no reporter runtime both leave the field out, so the report is
// byte-identical to the one an agent without the field sends.
func TestKubernetesCollector_ReporterError_OmittedWhenThereIsNone(t *testing.T) {
	cases := map[string]func(*kubeFixture){
		"empty error": func(f *kubeFixture) { f.withReporterError("") },
		"no runtime": func(f *kubeFixture) {
			f.withReporterError("")
			f.reporterError = nil
		},
	}

	for name, arrange := range cases {
		t.Run(name, func(t *testing.T) {
			f := newKubeFixture(t)
			arrange(f)
			f.markReporterEnrolled()

			items := collectItems(t, f.collector(), newTestStore(t))
			if len(items) != 1 {
				t.Fatalf("expected 1 node report, got %d", len(items))
			}

			if strings.Contains(items[0].Payload, "reporter_error") {
				t.Errorf("payload = %s, want no reporter_error key", items[0].Payload)
			}
		})
	}
}

// Intent: an error belongs to an enrolled reporter, so a host whose state file is gone never reports one, even
// if the runtime still remembers its last failure.
func TestKubernetesCollector_ReporterError_NotReportedWhenNotEnrolled(t *testing.T) {
	f := newKubeFixture(t)
	f.withReporterError("credentials_rejected")

	items := collectItems(t, f.collector(), newTestStore(t))
	if len(items) != 1 {
		t.Fatalf("expected 1 node report, got %d", len(items))
	}

	if strings.Contains(items[0].Payload, "reporter_error") {
		t.Errorf("payload = %s, want no reporter_error key without a state file", items[0].Payload)
	}
}

// copiedReporterState saves a reporter state file on the fixture's host that records the given machine id, and
// returns a real reporter runtime reading it, with this host's own machine id laid out under the fixture's root.
func (f *kubeFixture) copiedReporterState(recordedMachineID string) *kubereporter.Runtime {
	f.t.Helper()

	ca, _ := newKubeCert(f.t, "k3s-server-ca", 1)
	f.k3sNode(ca, "k3s-server-1")
	f.write("/etc/rancher/k3s/k3s.yaml", []byte("admin kubeconfig"))
	f.write("/etc/machine-id", []byte("0f1e2d3c4b5a69788796a5b4c3d2e1f0\n"))

	state := kubereporter.State{
		InstanceID:    "0123456789abcdef",
		Server:        "https://10.0.0.1:6443",
		CACert:        "pem",
		Token:         "token",
		HostMachineID: recordedMachineID,
	}

	if err := kubereporter.SaveState(f.dataDir, state); err != nil {
		f.t.Fatalf("SaveState: %v", err)
	}

	return kubereporter.NewRuntime(kubereporter.RuntimeOptions{DataDir: f.dataDir, Root: f.root})
}

// Intent: a disk image copied from another host carries that host's reporter state. The copy stays a candidate
// in the node report, so the page can offer to unenrol or enrol it again, and says why it cannot report: the
// answer comes from the real runtime, which has no elector for such a state file.
func TestKubernetesCollector_CopiedReporterState_IsReportedEnrolledWithStateCopied(t *testing.T) {
	f := newKubeFixture(t)
	f.reporterError = f.copiedReporterState("ffeeddccbbaa99887766554433221100").ReporterError

	items := collectItems(t, f.collector(), newTestStore(t))
	if len(items) != 1 {
		t.Fatalf("expected 1 node report, got %d", len(items))
	}

	if want := `"reporter_enrolled":true,"reporter_error":"state_copied"}`; strings.HasSuffix(items[0].Payload, want) == false {
		t.Errorf("payload = %s, want it to end with %s", items[0].Payload, want)
	}

	var record pb.KubernetesNodeRecord
	if err := protojson.Unmarshal([]byte(items[0].Payload), &record); err != nil {
		t.Fatalf("protojson.Unmarshal rejected the payload %s: %v", items[0].Payload, err)
	}

	if (record.ReporterEnrolled == false) || (record.ReporterError != "state_copied") {
		t.Errorf("record = %v, want enrolled with state_copied", &record)
	}
}

// Intent: the same state file on the host that wrote it reports no error.
func TestKubernetesCollector_ReporterStateOfThisHost_ReportsNoError(t *testing.T) {
	f := newKubeFixture(t)
	f.reporterError = f.copiedReporterState("0f1e2d3c4b5a69788796a5b4c3d2e1f0").ReporterError

	items := collectItems(t, f.collector(), newTestStore(t))
	if len(items) != 1 {
		t.Fatalf("expected 1 node report, got %d", len(items))
	}

	if strings.Contains(items[0].Payload, "reporter_error") {
		t.Errorf("payload = %s, want no reporter_error key", items[0].Payload)
	}

	if strings.Contains(items[0].Payload, `"reporter_enrolled":true`) == false {
		t.Errorf("payload = %s, want an enrolled host", items[0].Payload)
	}
}
