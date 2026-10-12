// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;
using System.ComponentModel.DataAnnotations;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// A Kubernetes cluster that one or more of a tenant's machines belong to. Machines are grouped
/// into a cluster by the fingerprint of the cluster CA certificate they report. Several clusters
/// in a tenant may share a fingerprint, so the fingerprint is indexed for lookup but not unique;
/// the <c>kube-system</c> namespace UID that a cluster snapshot reports is what tells them apart.
/// </summary>
[Table(TableNames.KubernetesClusters)]
public sealed class KubernetesCluster
{
    /// <summary>Identity primary key.</summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public long Id { get; set; }

    /// <summary>The tenant that owns the cluster. Clusters are never shared across tenants.</summary>
    [Column("TenantId"), NotNull]
    public int TenantId { get; set; }

    /// <summary>
    /// The lowercase hex SHA-256 of the DER bytes of the cluster CA certificate that established the
    /// cluster (64 characters).
    /// </summary>
    [Column("CaFingerprint"), NotNull, MaxLength(64)]
    public required string CaFingerprint { get; set; }

    /// <summary>
    /// The name an operator gave the cluster, or null while the cluster uses its derived default name.
    /// </summary>
    [Column("DisplayName"), Nullable, MaxLength(100)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// The Kubernetes distribution: <c>k3s</c>, <c>rke2</c>, <c>kubeadm</c>, <c>k0s</c>,
    /// <c>microk8s</c> or <c>unknown</c>.
    /// </summary>
    [Column("Distro"), NotNull, MaxLength(16)]
    public required string Distro { get; set; }

    /// <summary>When the cluster was first created from a node report.</summary>
    [Column("FirstSeenAt"), NotNull]
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>When a node report last confirmed the cluster.</summary>
    [Column("LastSeenAt"), NotNull]
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// The UID of the cluster's <c>kube-system</c> namespace, which identifies the cluster for good,
    /// or null while no cluster snapshot has reported it. A tenant holds each UID at most once;
    /// clusters without a UID never conflict.
    /// </summary>
    [Column("KubeSystemUid"), Nullable, MaxLength(64)]
    public string? KubeSystemUid { get; set; }

    /// <summary>The Kubernetes server version the latest cluster snapshot reported, if any.</summary>
    [Column("ClusterVersion"), Nullable, MaxLength(64)]
    public string? ClusterVersion { get; set; }

    /// <summary>
    /// The error the cluster reporters last reported (<c>credentials_rejected</c>,
    /// <c>tls_ca_changed</c>, <c>api_unreachable</c> or <c>namespace_terminating</c>), or null when
    /// there is none.
    /// </summary>
    [Column("ReporterError"), Nullable, MaxLength(32)]
    public string? ReporterError { get; set; }

    /// <summary>True while the installed reporter permissions are older than the agent expects.</summary>
    [Column("RoleOutdated"), NotNull]
    public bool RoleOutdated { get; set; }

    /// <summary>True when the latest snapshot listed only some of the cluster's nodes to fit its size cap.</summary>
    [Column("Truncated"), NotNull]
    public bool Truncated { get; set; }

    /// <summary>The Lease's transition count in the latest accepted snapshot.</summary>
    [Column("LeaseTransitions"), NotNull]
    public long LeaseTransitions { get; set; }

    /// <summary>The machine that sent the latest accepted snapshot, or null when there has been none.</summary>
    [Column("ActiveReporterMachineId"), Nullable]
    public long? ActiveReporterMachineId { get; set; }

    /// <summary>When the server received the latest accepted snapshot, or null when there has been none.</summary>
    [Column("LastSnapshotReceivedAt"), Nullable]
    public DateTimeOffset? LastSnapshotReceivedAt { get; set; }

    /// <summary>The telemetry row id of the latest accepted snapshot, or null when there has been none.</summary>
    [Column("LastSnapshotTelemetryId"), Nullable]
    public long? LastSnapshotTelemetryId { get; set; }

    /// <summary>
    /// The <c>metadata.uid</c> of the Lease the latest accepted snapshot came from. A Lease that is
    /// deleted and created again starts a new epoch with a new UID.
    /// </summary>
    [Column("LeaseUid"), Nullable, MaxLength(64)]
    public string? LeaseUid { get; set; }

    /// <summary>
    /// The creation time of that Lease. Snapshots are ordered by it first, so a re-enabled cluster
    /// whose transition count restarts at zero is not mistaken for an older snapshot.
    /// </summary>
    [Column("LeaseCreatedAt"), Nullable]
    public DateTimeOffset? LeaseCreatedAt { get; set; }
}
