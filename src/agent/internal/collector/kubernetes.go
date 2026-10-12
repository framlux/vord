// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/id"
	"github.com/framlux/vord/internal/kubelocal"
)

// reporterStateFile is the file name, under the data directory, that marks this host as an enrolled cluster
// reporter.
const reporterStateFile = "k8s-reporter.json"

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
	ReporterError          string `json:"reporter_error,omitempty"`
}

// KubernetesCollector reports whether this host is a Kubernetes node and its identity from local files: the
// distro, the kubelet client certificate's node name, the cluster CA fingerprint and which credentials exist.
// Detection is done by kubelocal, which reads files only: the admin credential is checked for existence and
// never opened, and private keys are never parsed.
type KubernetesCollector struct {
	detector kubelocal.Detector
	dataDir  string // agent data directory, holds k8s-reporter.json
	// reporterError says why the cluster reporter on this host cannot use its Lease credentials. Nil when the
	// host runs no reporter runtime.
	reporterError func() string
}

// NewKubernetesCollector creates a collector that inspects the real filesystem and keeps its reporter state
// file in dataDir. reporterError, which may be nil, is asked on every collection for the reason the cluster
// reporter on this host cannot use its credentials, and the answer is sent while the host is enrolled.
func NewKubernetesCollector(dataDir string, reporterError func() string) *KubernetesCollector {
	return &KubernetesCollector{detector: kubelocal.Detector{Root: "/"}, dataDir: dataDir, reporterError: reporterError}
}

// Name returns the collector's unique identifier.
func (c *KubernetesCollector) Name() string { return "kubernetes" }

// DefaultInterval returns the collection interval. Detection runs on every tick, so a host that becomes a
// node, or stops being one, is noticed within one interval.
func (c *KubernetesCollector) DefaultInterval() time.Duration { return 15 * time.Minute }

// Collect enqueues one Kubernetes node report when the host is a node. A host with no distro marker, or with
// markers but no kubelet, no admin credential and no control plane, reports nothing: an empty data directory
// left by an uninstalled distro or by running "k3s kubectl" does not make a machine a node. A host that runs a
// kubelet but whose node name cannot be determined reports nothing and returns the reason.
func (c *KubernetesCollector) Collect(ctx context.Context, store *db.Store) error {
	facts, report, err := c.detector.DetectWithError(ctx)
	if err != nil {
		return err
	}

	if report == false {
		slog.Debug("no Kubernetes node files found, skipping Kubernetes telemetry")

		return store.SaveCollectorState(c.Name(), nil)
	}

	payload := kubernetesNodePayload{
		Distro:                 facts.Distro,
		KubeletVersion:         facts.KubeletVersion,
		NodeName:               facts.NodeName,
		NodeNameSource:         facts.NodeNameSource,
		HasKubelet:             facts.HasKubelet,
		ControlPlane:           facts.ControlPlane,
		CaFingerprint:          facts.CaFingerprint,
		AdminCredentialPresent: facts.AdminCredentialPresent,
		ReporterEnrolled:       c.reporterEnrolled(),
	}

	// An error describes an enrolled reporter's credentials, so a host without the state file reports none even
	// if the runtime still remembers its last failure.
	if payload.ReporterEnrolled && (c.reporterError != nil) {
		payload.ReporterError = c.reporterError()
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

// reporterEnrolled reports whether the agent data directory holds the cluster reporter state file.
func (c *KubernetesCollector) reporterEnrolled() bool {
	if c.dataDir == "" {
		return false
	}

	info, err := os.Stat(filepath.Join(c.dataDir, reporterStateFile))

	return (err == nil) && (info.IsDir() == false)
}
