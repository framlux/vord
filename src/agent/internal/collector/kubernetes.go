// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/id"
)

// reporterStateFile is the file name, under the data directory, that marks this host as an enrolled cluster
// reporter.
const reporterStateFile = "k8s-reporter.json"

// maxKubernetesFileBytes caps every file the collector reads. A larger file is treated as unreadable, so a
// misplaced or hostile path cannot make the agent buffer an arbitrary amount of data.
const maxKubernetesFileBytes = 1 << 20

// Values of the node_name_source field.
const (
	nodeNameSourceCertificate = "certificate"
	nodeNameSourceHostname    = "hostname"
)

// kubernetesNodePayload is the JSON shape enqueued for Kubernetes node telemetry. The keys match the
// KubernetesNodeRecord proto field names so the sender can unmarshal it with protojson.
type kubernetesNodePayload struct {
	Distro                 string `json:"distro"`
	KubeletVersion         string `json:"kubelet_version,omitempty"`
	NodeName               string `json:"node_name"`
	NodeNameSource         string `json:"node_name_source"`
	HasKubelet             bool   `json:"has_kubelet"`
	ControlPlane           bool   `json:"control_plane"`
	CaFingerprint          string `json:"ca_fingerprint,omitempty"`
	AdminCredentialPresent bool   `json:"admin_credential_present"`
	ReporterEnrolled       bool   `json:"reporter_enrolled"`
}

// pemSource is a file, given as a path below the filesystem root, that may hold a PEM certificate. When
// kubeconfig is set the file is a kubeconfig, and the certificate is the one it embeds or references.
type pemSource struct {
	path       string
	kubeconfig bool
}

// kubernetesLayout is what detection learned about a host: which distro it matches, the facts that can be read
// by checking whether files exist, where to look for the cluster CA and the kubelet client certificate (in
// order), and how to find the distro version. Files are only opened after the host has passed the reporting gate.
type kubernetesLayout struct {
	distro          string
	hasKubelet      bool
	controlPlane    bool
	adminCredential bool
	caSources       []pemSource
	certSources     []pemSource
	version         func(ctx context.Context) string
}

// rancherDistro describes how k3s and RKE2, which share one directory layout, differ.
type rancherDistro struct {
	name              string
	dataDir           string
	adminCredential   string
	apiServerManifest string
	binaries          []string
	parseVersion      func(string) string
}

// KubernetesCollector detects whether this host is a Kubernetes node and reports its identity from local
// files: the distro, the kubelet client certificate's node name, the cluster CA fingerprint and which
// credentials exist. It reads files only. The admin credential is checked for existence and never opened, and
// private keys are never parsed.
type KubernetesCollector struct {
	root     string                                                                 // filesystem root; "/" in production
	dataDir  string                                                                 // agent data directory, holds k8s-reporter.json
	execCmd  func(ctx context.Context, name string, args ...string) ([]byte, error) // nil means runCmd
	hostname func() (string, error)                                                 // nil means os.Hostname
}

// NewKubernetesCollector creates a collector that inspects the real filesystem and keeps its reporter state
// file in dataDir.
func NewKubernetesCollector(dataDir string) *KubernetesCollector {
	return &KubernetesCollector{root: "/", dataDir: dataDir}
}

// Name returns the collector's unique identifier.
func (c *KubernetesCollector) Name() string { return "kubernetes" }

// DefaultInterval returns the collection interval. Detection runs on every tick, so a host that becomes a
// node, or stops being one, is noticed within one interval.
func (c *KubernetesCollector) DefaultInterval() time.Duration { return 15 * time.Minute }

// Collect enqueues one Kubernetes node report when the host is a node. A host with no distro marker, or with a
// marker but no kubelet, no admin credential and no control plane, reports nothing: an empty data directory
// left by an uninstalled distro or by running "k3s kubectl" does not make a machine a node.
func (c *KubernetesCollector) Collect(ctx context.Context, store *db.Store) error {
	layout, found := c.detect()
	if found == false {
		slog.Debug("no Kubernetes node files found, skipping Kubernetes telemetry")

		return store.SaveCollectorState(c.Name(), nil)
	}

	if (layout.hasKubelet == false) && (layout.adminCredential == false) && (layout.controlPlane == false) {
		slog.Debug("Kubernetes files found without a kubelet, admin credential or control plane, skipping Kubernetes telemetry",
			"distro", layout.distro)

		return store.SaveCollectorState(c.Name(), nil)
	}

	nodeName, nodeNameSource, err := c.resolveNodeName(layout.certSources)
	if err != nil {
		return fmt.Errorf("resolving Kubernetes node name: %w", err)
	}

	payload := kubernetesNodePayload{
		Distro:                 layout.distro,
		NodeName:               nodeName,
		NodeNameSource:         nodeNameSource,
		HasKubelet:             layout.hasKubelet,
		ControlPlane:           layout.controlPlane,
		CaFingerprint:          c.findCaFingerprint(layout.caSources),
		AdminCredentialPresent: layout.adminCredential,
		ReporterEnrolled:       c.reporterEnrolled(),
	}

	if layout.version != nil {
		payload.KubeletVersion = layout.version(ctx)
	}

	data, err := json.Marshal(payload)
	if err != nil {
		return fmt.Errorf("marshaling Kubernetes node: %w", err)
	}

	if err := store.EnqueueTelemetry(id.NewV7(), db.TelemetryKubernetesNode, string(data)); err != nil {
		return fmt.Errorf("enqueuing Kubernetes node telemetry: %w", err)
	}

	return store.SaveCollectorState(c.Name(), nil)
}

// detect returns the layout of the first distro whose marker exists. The order is fixed so a host that carries
// the files of several distros is reported once.
func (c *KubernetesCollector) detect() (kubernetesLayout, bool) {
	detectors := []func() (kubernetesLayout, bool){
		c.detectK3s,
		c.detectRke2,
		c.detectK0s,
		c.detectMicrok8s,
		c.detectKubeadm,
		c.detectUnknown,
	}

	for _, detectDistro := range detectors {
		layout, found := detectDistro()
		if found {
			return layout, true
		}
	}

	return kubernetesLayout{}, false
}

func (c *KubernetesCollector) detectK3s() (kubernetesLayout, bool) {
	return c.detectRancher(rancherDistro{
		name:            "k3s",
		dataDir:         "/var/lib/rancher/k3s",
		adminCredential: "/etc/rancher/k3s/k3s.yaml",
		binaries:        []string{"/usr/local/bin/k3s", "/opt/bin/k3s"},
		parseVersion:    parseK3sVersion,
	})
}

func (c *KubernetesCollector) detectRke2() (kubernetesLayout, bool) {
	return c.detectRancher(rancherDistro{
		name:              "rke2",
		dataDir:           "/var/lib/rancher/rke2",
		adminCredential:   "/etc/rancher/rke2/rke2.yaml",
		apiServerManifest: "agent/pod-manifests/kube-apiserver.yaml",
		binaries:          []string{"/usr/local/bin/rke2", "/opt/rke2/bin/rke2", "/usr/bin/rke2"},
		parseVersion:      parseRke2Version,
	})
}

func (c *KubernetesCollector) detectRancher(distro rancherDistro) (kubernetesLayout, bool) {
	if c.dirExists(distro.dataDir) == false {
		return kubernetesLayout{}, false
	}

	controlPlane := c.dirExists(filepath.Join(distro.dataDir, "server/tls"))
	if (distro.apiServerManifest != "") && c.fileExists(filepath.Join(distro.dataDir, distro.apiServerManifest)) {
		controlPlane = true
	}

	return kubernetesLayout{
		distro:          distro.name,
		hasKubelet:      c.fileExists(filepath.Join(distro.dataDir, "agent/kubelet.kubeconfig")),
		controlPlane:    controlPlane,
		adminCredential: c.fileExists(distro.adminCredential),
		caSources: []pemSource{
			{path: filepath.Join(distro.dataDir, "agent/server-ca.crt")},
			{path: filepath.Join(distro.dataDir, "server/tls/server-ca.crt")},
		},
		certSources: []pemSource{
			{path: filepath.Join(distro.dataDir, "agent/client-kubelet.crt")},
		},
		version: c.commandVersion(distro.binaries, []string{"--version"}, distro.parseVersion),
	}, true
}

func (c *KubernetesCollector) detectK0s() (kubernetesLayout, bool) {
	const dataDir = "/var/lib/k0s"

	if c.dirExists(dataDir) == false {
		return kubernetesLayout{}, false
	}

	kubeletConf := filepath.Join(dataDir, "kubelet.conf")

	return kubernetesLayout{
		distro:          "k0s",
		hasKubelet:      c.fileExists(kubeletConf),
		controlPlane:    c.fileExists(filepath.Join(dataDir, "pki/ca.key")),
		adminCredential: c.fileExists(filepath.Join(dataDir, "pki/admin.conf")),
		caSources: []pemSource{
			{path: kubeletConf, kubeconfig: true},
			{path: filepath.Join(dataDir, "pki/ca.crt")},
		},
		certSources: []pemSource{
			{path: kubeletConf, kubeconfig: true},
			{path: filepath.Join(dataDir, "kubelet/pki/kubelet-client-current.pem")},
		},
		version: c.commandVersion([]string{"/usr/local/bin/k0s"}, []string{"version"}, parseK0sVersion),
	}, true
}

func (c *KubernetesCollector) detectMicrok8s() (kubernetesLayout, bool) {
	const dataDir = "/var/snap/microk8s"

	if c.dirExists(dataDir) == false {
		return kubernetesLayout{}, false
	}

	controlPlane := c.fileExists(filepath.Join(dataDir, "current/var/lock/clustered.lock")) == false

	return kubernetesLayout{
		distro:          "microk8s",
		hasKubelet:      true,
		controlPlane:    controlPlane,
		adminCredential: controlPlane && c.fileExists(filepath.Join(dataDir, "current/credentials/client.config")),
		caSources: []pemSource{
			{path: filepath.Join(dataDir, "current/credentials/kubelet.config"), kubeconfig: true},
			{path: filepath.Join(dataDir, "current/certs/ca.crt")},
		},
		certSources: []pemSource{
			{path: filepath.Join(dataDir, "current/certs/kubelet.crt")},
		},
		version: c.snapVersion("/snap/microk8s/current/meta/snap.yaml"),
	}, true
}

func (c *KubernetesCollector) detectKubeadm() (kubernetesLayout, bool) {
	const kubeletConf = "/etc/kubernetes/kubelet.conf"

	if c.fileExists(kubeletConf) == false {
		return kubernetesLayout{}, false
	}

	return kubernetesLayout{
		distro:          "kubeadm",
		hasKubelet:      true,
		controlPlane:    c.fileExists("/etc/kubernetes/manifests/kube-apiserver.yaml"),
		adminCredential: c.fileExists("/etc/kubernetes/admin.conf"),
		caSources: []pemSource{
			{path: kubeletConf, kubeconfig: true},
			{path: "/etc/kubernetes/pki/ca.crt"},
		},
		certSources: []pemSource{
			{path: kubeletConf, kubeconfig: true},
			{path: "/var/lib/kubelet/pki/kubelet-client-current.pem"},
		},
		version: c.commandVersion([]string{"/usr/bin/kubelet", "/usr/local/bin/kubelet"}, []string{"--version"}, parseKubeletVersion),
	}, true
}

// detectUnknown matches a host that has a kubelet certificate or kubeconfig but none of the layouts above.
// The marker is a file, so an empty /var/lib/kubelet does not match.
func (c *KubernetesCollector) detectUnknown() (kubernetesLayout, bool) {
	const kubeletCertificate = "/var/lib/kubelet/pki/kubelet-client-current.pem"

	if (c.fileExists(kubeletCertificate) == false) && (c.fileExists("/var/lib/kubelet/kubeconfig") == false) {
		return kubernetesLayout{}, false
	}

	return kubernetesLayout{
		distro:      "unknown",
		hasKubelet:  true,
		caSources:   []pemSource{{path: "/etc/kubernetes/pki/ca.crt"}},
		certSources: []pemSource{{path: kubeletCertificate}},
	}, true
}

// commandVersion returns a lookup that runs the first binary of binaries that exists and parses its output. A
// command that fails or times out yields no version and is only logged at debug level, because a missing
// version must never stop the node from being reported.
func (c *KubernetesCollector) commandVersion(binaries []string, args []string, parse func(string) string) func(context.Context) string {
	return func(ctx context.Context) string {
		for _, binary := range binaries {
			if c.fileExists(binary) == false {
				continue
			}

			run := c.execCmd
			if run == nil {
				run = runCmd
			}

			path := c.abs(binary)

			output, err := run(ctx, path, args...)
			if err != nil {
				slog.Debug("Kubernetes version command failed", "path", path, "error", err)

				return ""
			}

			return parse(string(output))
		}

		return ""
	}
}

// snapVersion returns a lookup that reads the version from a snap's metadata file.
func (c *KubernetesCollector) snapVersion(path string) func(context.Context) string {
	return func(context.Context) string {
		content, err := readKubernetesFile(c.abs(path))
		if err != nil {
			slog.Debug("Kubernetes snap metadata is unreadable", "path", c.abs(path), "error", err)

			return ""
		}

		return parseSnapVersion(string(content))
	}
}

// resolveNodeName takes the node name from the first kubelet client certificate that carries a node identity.
// Without one it falls back to the lowercased hostname, which is what a kubelet uses to name itself, and fails
// when the hostname is not a valid node name so that a blank or unsafe name is never reported.
func (c *KubernetesCollector) resolveNodeName(sources []pemSource) (string, string, error) {
	for _, source := range sources {
		pemData := c.loadPEM(source, func(refs kubeconfigRefs) ([]byte, string) { return refs.certData, refs.certPath })
		if pemData == nil {
			continue
		}

		name, err := nodeNameFromCert(pemData)
		if err != nil {
			slog.Debug("Kubernetes kubelet certificate has no node identity", "path", c.abs(source.path), "error", err)

			continue
		}

		return name, nodeNameSourceCertificate, nil
	}

	lookup := c.hostname
	if lookup == nil {
		lookup = os.Hostname
	}

	host, err := lookup()
	if err != nil {
		return "", "", fmt.Errorf("reading hostname: %w", err)
	}

	name := strings.ToLower(strings.TrimSpace(host))
	if validNodeName(name) == false {
		return "", "", errors.New("hostname is not a valid node name")
	}

	return name, nodeNameSourceHostname, nil
}

// findCaFingerprint returns the fingerprint of the first CA source that yields a certificate, or "" when none
// does.
func (c *KubernetesCollector) findCaFingerprint(sources []pemSource) string {
	for _, source := range sources {
		pemData := c.loadPEM(source, func(refs kubeconfigRefs) ([]byte, string) { return refs.caData, refs.caPath })
		if pemData == nil {
			continue
		}

		fingerprint, err := caFingerprint(pemData)
		if err != nil {
			slog.Debug("Kubernetes cluster CA is not a certificate", "path", c.abs(source.path), "error", err)

			continue
		}

		return fingerprint
	}

	return ""
}

// loadPEM returns the PEM bytes a source provides, or nil when the source is missing, unreadable or does not
// parse. For a kubeconfig, pick selects the embedded data or the file reference for the wanted certificate:
// embedded data wins, and a reference is resolved against the kubeconfig's directory when relative and against
// the filesystem root when absolute.
func (c *KubernetesCollector) loadPEM(source pemSource, pick func(kubeconfigRefs) ([]byte, string)) []byte {
	path := c.abs(source.path)

	content, err := readKubernetesFile(path)
	if err != nil {
		slog.Debug("Kubernetes file is unreadable", "path", path, "error", err)

		return nil
	}

	if source.kubeconfig == false {
		return content
	}

	refs, err := parseKubeconfig(content)
	if err != nil {
		slog.Debug("Kubernetes kubeconfig could not be parsed", "path", path, "error", err)

		return nil
	}

	data, reference := pick(refs)
	if len(data) > 0 {
		return data
	}

	if reference == "" {
		return nil
	}

	target := filepath.Join(filepath.Dir(path), reference)
	if filepath.IsAbs(reference) {
		target = c.abs(reference)
	}

	referenced, err := readKubernetesFile(target)
	if err != nil {
		slog.Debug("Kubernetes kubeconfig reference is unreadable", "kubeconfig", path, "path", target, "error", err)

		return nil
	}

	return referenced
}

// reporterEnrolled reports whether the agent data directory holds the cluster reporter state file.
func (c *KubernetesCollector) reporterEnrolled() bool {
	if c.dataDir == "" {
		return false
	}

	return fileExistsAt(filepath.Join(c.dataDir, reporterStateFile))
}

// abs returns where a path below the filesystem root really lives.
func (c *KubernetesCollector) abs(path string) string {
	return filepath.Join(c.root, path)
}

func (c *KubernetesCollector) fileExists(path string) bool {
	return fileExistsAt(c.abs(path))
}

func (c *KubernetesCollector) dirExists(path string) bool {
	info, err := os.Stat(c.abs(path))

	return (err == nil) && info.IsDir()
}

// fileExistsAt reports whether path exists and is not a directory. Only the file's metadata is looked at.
func fileExistsAt(path string) bool {
	info, err := os.Stat(path)

	return (err == nil) && (info.IsDir() == false)
}

// readKubernetesFile reads a regular file of at most maxKubernetesFileBytes. Anything else, including a
// directory, a device or a larger file, is an error.
func readKubernetesFile(path string) ([]byte, error) {
	info, err := os.Stat(path)
	if err != nil {
		return nil, err
	}

	if info.Mode().IsRegular() == false {
		return nil, errors.New("not a regular file")
	}

	if info.Size() > maxKubernetesFileBytes {
		return nil, errors.New("file exceeds the size limit")
	}

	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()

	data, err := io.ReadAll(io.LimitReader(file, maxKubernetesFileBytes+1))
	if err != nil {
		return nil, err
	}

	if len(data) > maxKubernetesFileBytes {
		return nil, errors.New("file exceeds the size limit")
	}

	return data, nil
}
