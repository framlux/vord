// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;
using System.ComponentModel.DataAnnotations;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// A Kubernetes cluster that one or more of a tenant's machines belong to. Machines are grouped
/// into a cluster by the fingerprint of the cluster CA certificate they report. Several clusters
/// in a tenant may share a fingerprint, so the fingerprint is indexed for lookup but not unique.
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
}
