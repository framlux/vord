// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package kubelocal detects, from files on this host alone, whether the machine is a Kubernetes node and what
// it can say about itself: the distro, the node name, the cluster CA fingerprint and where the admin
// credential is. It is shared by the telemetry collector and the cluster reporter commands, which must agree
// on the answer, so it depends on neither of them.
package kubelocal

import (
	"context"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"log/slog"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"
)

// commandTimeout bounds one version command run by the default command runner.
const commandTimeout = 30 * time.Second

// maxKubernetesFileBytes caps every file the detector reads. A larger file is treated as unreadable, so a
// misplaced or hostile path cannot make the agent buffer an arbitrary amount of data.
const maxKubernetesFileBytes = 1 << 20

// Values of the node_name_source field.
const (
	nodeNameSourceCertificate = "certificate"
	nodeNameSourceHostname    = "hostname"
)

// pemSource is a file, given as a path below the filesystem root, that may hold a PEM certificate. When
// kubeconfig is set the file is a kubeconfig, and the certificate is the one it embeds or references.
type pemSource struct {
	path       string
	kubeconfig bool
}

// kubernetesLayout is what detection learned about a host: which distro it matches, the facts that can be read
// by checking whether files exist, where to look for the cluster CA and the kubelet client certificate (in
// order), and how to find the distro version. Files are only opened after the host has passed the reporting gate.
// adminPath is where the admin credential lives, below the filesystem root, and is empty when it does not exist.
type kubernetesLayout struct {
	distro       string
	hasKubelet   bool
	controlPlane bool
	adminPath    string
	caSources    []pemSource
	certSources  []pemSource
	version      func(ctx context.Context) string
}

// reportable reports whether the host is worth reporting: it has a kubelet, an admin credential or a control
// plane. A bare data directory, such as the one "k3s kubectl" unpacks or an uninstalled distro leaves behind,
// matches a distro marker without being a node.
func (l kubernetesLayout) reportable() bool {
	return l.hasKubelet || (l.adminPath != "") || l.controlPlane
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

// Facts is what a host can say about itself as a Kubernetes node, learned from local files only.
type Facts struct {
	// Distro is the matched distribution: k3s, rke2, k0s, microk8s, kubeadm or unknown.
	Distro string
	// KubeletVersion is the version the distro reports, or "" when it could not be determined.
	KubeletVersion string
	// NodeName is the node's name: taken from the kubelet client certificate when there is one, otherwise from
	// the hostname. A host with no kubelet and no usable hostname has an empty name.
	NodeName string
	// NodeNameSource says where NodeName came from: "certificate" or "hostname".
	NodeNameSource string
	// CaFingerprint is the lowercase hex SHA-256 of the cluster CA certificate, or "" when none could be read.
	CaFingerprint string
	// HasKubelet reports whether the host runs a kubelet.
	HasKubelet bool
	// ControlPlane reports whether the host runs control plane components.
	ControlPlane bool
	// AdminCredentialPresent reports whether the distro's admin kubeconfig exists. It is only checked for
	// existence here and never opened.
	AdminCredentialPresent bool
	// AdminKubeconfigPath is the absolute host path of the admin credential, and "" when it is absent.
	AdminKubeconfigPath string
}

// Detector inspects one host's files. The zero value inspects the real host.
type Detector struct {
	// Root is the filesystem root every well-known path is resolved under. Empty means the real root.
	Root string
	// ExecCmd runs a version command. Nil runs the command with a timeout.
	ExecCmd func(ctx context.Context, name string, args ...string) ([]byte, error)
	// Hostname returns the host's name. Nil means os.Hostname.
	Hostname func() (string, error)
}

// Detect returns the host's facts and whether the host should report at all. A host with no distro marker, or
// with markers but no kubelet, no admin credential and no control plane, does not report: an empty data
// directory left by an uninstalled distro or by running "k3s kubectl" does not make a machine a node. A host
// that runs a kubelet but has no node name it can use does not report either; DetectWithError says why.
func (d Detector) Detect(ctx context.Context) (Facts, bool) {
	facts, report, err := d.DetectWithError(ctx)
	if err != nil {
		slog.Debug("Kubernetes host cannot be reported", "error", err)

		return Facts{}, false
	}

	return facts, report
}

// DetectWithError is Detect that also returns the reason a host which runs a kubelet cannot be reported: its
// node name is neither in a kubelet certificate nor derivable from a hostname that is a valid node name. A host
// that is simply not a node answers (Facts{}, false, nil), so a caller can tell an absent node from one it
// failed to identify. A reported host answers its facts, true and no error.
func (d Detector) DetectWithError(ctx context.Context) (Facts, bool, error) {
	layout, found := d.detect()
	if found == false {
		return Facts{}, false, nil
	}

	nodeName, nodeNameSource, err := d.resolveNodeName(layout.certSources, layout.hasKubelet)
	if err != nil {
		return Facts{}, false, fmt.Errorf("resolving Kubernetes node name: %w", err)
	}

	facts := Facts{
		Distro:                 layout.distro,
		NodeName:               nodeName,
		NodeNameSource:         nodeNameSource,
		CaFingerprint:          d.findCaFingerprint(layout.caSources),
		HasKubelet:             layout.hasKubelet,
		ControlPlane:           layout.controlPlane,
		AdminCredentialPresent: layout.adminPath != "",
	}

	if layout.adminPath != "" {
		facts.AdminKubeconfigPath = d.abs(layout.adminPath)
	}

	if layout.version != nil {
		facts.KubeletVersion = layout.version(ctx)
	}

	return facts, true, nil
}

// detect returns the layout of the first distro, in a fixed order, whose marker exists and whose host passes
// the reporting gate, so a host that carries the files of several distros is reported once. A distro that
// matches but fails the gate is skipped rather than ending the search, so a stray data directory cannot hide a
// real node of a later distro.
func (d Detector) detect() (kubernetesLayout, bool) {
	detectors := []func() (kubernetesLayout, bool){
		d.detectK3s,
		d.detectRke2,
		d.detectK0s,
		d.detectMicrok8s,
		d.detectKubeadm,
		d.detectUnknown,
	}

	for _, detectDistro := range detectors {
		layout, found := detectDistro()
		if found == false {
			continue
		}

		if layout.reportable() == false {
			slog.Debug("Kubernetes files found without a kubelet, admin credential or control plane, trying the next layout",
				"distro", layout.distro)

			continue
		}

		return layout, true
	}

	return kubernetesLayout{}, false
}

func (d Detector) detectK3s() (kubernetesLayout, bool) {
	return d.detectRancher(rancherDistro{
		name:            "k3s",
		dataDir:         "/var/lib/rancher/k3s",
		adminCredential: "/etc/rancher/k3s/k3s.yaml",
		binaries:        []string{"/usr/local/bin/k3s", "/opt/bin/k3s"},
		parseVersion:    parseK3sVersion,
	})
}

func (d Detector) detectRke2() (kubernetesLayout, bool) {
	return d.detectRancher(rancherDistro{
		name:              "rke2",
		dataDir:           "/var/lib/rancher/rke2",
		adminCredential:   "/etc/rancher/rke2/rke2.yaml",
		apiServerManifest: "agent/pod-manifests/kube-apiserver.yaml",
		binaries:          []string{"/usr/local/bin/rke2", "/opt/rke2/bin/rke2", "/usr/bin/rke2"},
		parseVersion:      parseRke2Version,
	})
}

func (d Detector) detectRancher(distro rancherDistro) (kubernetesLayout, bool) {
	if d.dirExists(distro.dataDir) == false {
		return kubernetesLayout{}, false
	}

	controlPlane := d.dirExists(filepath.Join(distro.dataDir, "server/tls"))
	if (distro.apiServerManifest != "") && d.fileExists(filepath.Join(distro.dataDir, distro.apiServerManifest)) {
		controlPlane = true
	}

	return kubernetesLayout{
		distro:       distro.name,
		hasKubelet:   d.fileExists(filepath.Join(distro.dataDir, "agent/kubelet.kubeconfig")),
		controlPlane: controlPlane,
		adminPath:    d.existingFile(distro.adminCredential),
		caSources: []pemSource{
			{path: filepath.Join(distro.dataDir, "agent/server-ca.crt")},
			{path: filepath.Join(distro.dataDir, "server/tls/server-ca.crt")},
		},
		certSources: []pemSource{
			{path: filepath.Join(distro.dataDir, "agent/client-kubelet.crt")},
		},
		version: d.commandVersion(distro.binaries, []string{"--version"}, distro.parseVersion),
	}, true
}

func (d Detector) detectK0s() (kubernetesLayout, bool) {
	const dataDir = "/var/lib/k0s"

	if d.dirExists(dataDir) == false {
		return kubernetesLayout{}, false
	}

	kubeletConf := filepath.Join(dataDir, "kubelet.conf")

	return kubernetesLayout{
		distro:       "k0s",
		hasKubelet:   d.fileExists(kubeletConf),
		controlPlane: d.fileExists(filepath.Join(dataDir, "pki/ca.key")),
		adminPath:    d.existingFile(filepath.Join(dataDir, "pki/admin.conf")),
		caSources: []pemSource{
			{path: kubeletConf, kubeconfig: true},
			{path: filepath.Join(dataDir, "pki/ca.crt")},
		},
		certSources: []pemSource{
			{path: kubeletConf, kubeconfig: true},
			{path: filepath.Join(dataDir, "kubelet/pki/kubelet-client-current.pem")},
		},
		version: d.commandVersion([]string{"/usr/local/bin/k0s"}, []string{"version"}, parseK0sVersion),
	}, true
}

func (d Detector) detectMicrok8s() (kubernetesLayout, bool) {
	const dataDir = "/var/snap/microk8s"

	if d.dirExists(dataDir) == false {
		return kubernetesLayout{}, false
	}

	// The kubelet's own credentials file is what a running MicroK8s node has; a directory left behind by a
	// removed snap does not, and without a kubelet the host is not a control plane either.
	hasKubelet := d.fileExists(filepath.Join(dataDir, "current/credentials/kubelet.config"))
	controlPlane := hasKubelet && (d.fileExists(filepath.Join(dataDir, "current/var/lock/clustered.lock")) == false)

	adminPath := ""
	if controlPlane {
		adminPath = d.existingFile(filepath.Join(dataDir, "current/credentials/client.config"))
	}

	return kubernetesLayout{
		distro:       "microk8s",
		hasKubelet:   hasKubelet,
		controlPlane: controlPlane,
		adminPath:    adminPath,
		caSources: []pemSource{
			{path: filepath.Join(dataDir, "current/credentials/kubelet.config"), kubeconfig: true},
			{path: filepath.Join(dataDir, "current/certs/ca.crt")},
		},
		certSources: []pemSource{
			{path: filepath.Join(dataDir, "current/certs/kubelet.crt")},
		},
		version: d.snapVersion("/snap/microk8s/current/meta/snap.yaml"),
	}, true
}

func (d Detector) detectKubeadm() (kubernetesLayout, bool) {
	const kubeletConf = "/etc/kubernetes/kubelet.conf"

	if d.fileExists(kubeletConf) == false {
		return kubernetesLayout{}, false
	}

	return kubernetesLayout{
		distro:       "kubeadm",
		hasKubelet:   true,
		controlPlane: d.fileExists("/etc/kubernetes/manifests/kube-apiserver.yaml"),
		adminPath:    d.existingFile("/etc/kubernetes/admin.conf"),
		caSources: []pemSource{
			{path: kubeletConf, kubeconfig: true},
			{path: "/etc/kubernetes/pki/ca.crt"},
		},
		certSources: []pemSource{
			{path: kubeletConf, kubeconfig: true},
			{path: "/var/lib/kubelet/pki/kubelet-client-current.pem"},
		},
		version: d.commandVersion([]string{"/usr/bin/kubelet", "/usr/local/bin/kubelet"}, []string{"--version"}, parseKubeletVersion),
	}, true
}

// detectUnknown matches a host that has a kubelet certificate or kubeconfig but none of the layouts above.
// The marker is a file, so an empty /var/lib/kubelet does not match.
func (d Detector) detectUnknown() (kubernetesLayout, bool) {
	const kubeletCertificate = "/var/lib/kubelet/pki/kubelet-client-current.pem"

	if (d.fileExists(kubeletCertificate) == false) && (d.fileExists("/var/lib/kubelet/kubeconfig") == false) {
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
func (d Detector) commandVersion(binaries []string, args []string, parse func(string) string) func(context.Context) string {
	return func(ctx context.Context) string {
		for _, binary := range binaries {
			if d.fileExists(binary) == false {
				continue
			}

			run := d.ExecCmd
			if run == nil {
				run = runCommand
			}

			path := d.abs(binary)

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
func (d Detector) snapVersion(path string) func(context.Context) string {
	return func(context.Context) string {
		content, err := readKubernetesFile(d.abs(path))
		if err != nil {
			slog.Debug("Kubernetes snap metadata is unreadable", "path", d.abs(path), "error", err)

			return ""
		}

		return parseSnapVersion(string(content))
	}
}

// resolveNodeName takes the node name from the first kubelet client certificate that carries a node identity.
// Without one it falls back to the lowercased hostname, which is what a kubelet uses to name itself. When the
// hostname is not a valid node name, a host with a kubelet fails, so that a blank or unsafe name is never
// reported for a node; a host without a kubelet has no node to name, so it is reported with an empty name.
func (d Detector) resolveNodeName(sources []pemSource, hasKubelet bool) (string, string, error) {
	for _, source := range sources {
		pemData := d.loadPEM(source, func(refs KubeconfigRefs) ([]byte, string) { return refs.CertData, refs.CertPath })
		if pemData == nil {
			continue
		}

		name, err := NodeNameFromCert(pemData)
		if err != nil {
			slog.Debug("Kubernetes kubelet certificate has no node identity", "path", d.abs(source.path), "error", err)

			continue
		}

		return name, nodeNameSourceCertificate, nil
	}

	lookup := d.Hostname
	if lookup == nil {
		lookup = os.Hostname
	}

	host, err := lookup()
	if err != nil {
		if hasKubelet == false {
			return "", nodeNameSourceHostname, nil
		}

		return "", "", fmt.Errorf("reading hostname: %w", err)
	}

	name := strings.ToLower(strings.TrimSpace(host))
	if validNodeName(name) == false {
		if hasKubelet == false {
			return "", nodeNameSourceHostname, nil
		}

		return "", "", errors.New("hostname is not a valid node name")
	}

	return name, nodeNameSourceHostname, nil
}

// findCaFingerprint returns the fingerprint of the first CA source that yields a certificate, or "" when none
// does.
func (d Detector) findCaFingerprint(sources []pemSource) string {
	for _, source := range sources {
		pemData := d.loadPEM(source, func(refs KubeconfigRefs) ([]byte, string) { return refs.CAData, refs.CAPath })
		if pemData == nil {
			continue
		}

		fingerprint, err := CAFingerprint(pemData)
		if err != nil {
			slog.Debug("Kubernetes cluster CA is not a certificate", "path", d.abs(source.path), "error", err)

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
func (d Detector) loadPEM(source pemSource, pick func(KubeconfigRefs) ([]byte, string)) []byte {
	path := d.abs(source.path)

	content, err := readKubernetesFile(path)
	if err != nil {
		slog.Debug("Kubernetes file is unreadable", "path", path, "error", err)

		return nil
	}

	if source.kubeconfig == false {
		return content
	}

	refs, err := ParseKubeconfig(content)
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
		target = d.abs(reference)
	}

	referenced, err := readKubernetesFile(target)
	if err != nil {
		slog.Debug("Kubernetes kubeconfig reference is unreadable", "kubeconfig", path, "reason", readFailure(err))

		return nil
	}

	return referenced
}

// abs returns where a path below the filesystem root really lives. With an empty root that is the path itself.
func (d Detector) abs(path string) string {
	return filepath.Join(d.Root, path)
}

func (d Detector) fileExists(path string) bool {
	return fileExistsAt(d.abs(path))
}

// existingFile returns path when it names a file that exists, and "" otherwise.
func (d Detector) existingFile(path string) string {
	if d.fileExists(path) {
		return path
	}

	return ""
}

func (d Detector) dirExists(path string) bool {
	info, err := os.Stat(d.abs(path))

	return (err == nil) && info.IsDir()
}

// runCommand runs an external command with a timeout derived from the parent context, so a hung version command
// cannot block detection indefinitely.
func runCommand(ctx context.Context, name string, args ...string) ([]byte, error) {
	cmdCtx, cancel := context.WithTimeout(ctx, commandTimeout)
	defer cancel()

	return exec.CommandContext(cmdCtx, name, args...).CombinedOutput()
}

// fileExistsAt reports whether path exists and is not a directory. Only the file's metadata is looked at.
func fileExistsAt(path string) bool {
	info, err := os.Stat(path)

	return (err == nil) && (info.IsDir() == false)
}

// readFailure describes why a file could not be read without repeating its path, which the operating system's
// error text includes. It is used where the path came from a file's content and is not worth logging.
func readFailure(err error) string {
	var pathErr *fs.PathError
	if errors.As(err, &pathErr) {
		return pathErr.Err.Error()
	}

	return err.Error()
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
