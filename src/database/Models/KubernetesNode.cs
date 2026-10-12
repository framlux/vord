// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;
using System.ComponentModel.DataAnnotations;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// A node of a <see cref="KubernetesCluster"/>. A node is unique by name within its cluster and is
/// linked to the enrolled machine that reported it, when there is one. A machine is at most one
/// node.
/// </summary>
[Table(TableNames.KubernetesNodes)]
public sealed class KubernetesNode
{
    /// <summary>Identity primary key.</summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public long Id { get; set; }

    /// <summary>The owning tenant, repeated from the cluster so tenant-scoped queries need no join.</summary>
    [Column("TenantId"), NotNull]
    public int TenantId { get; set; }

    /// <summary>The cluster the node belongs to. Deleting the cluster deletes its nodes.</summary>
    [Column("ClusterId"), NotNull]
    public long ClusterId { get; set; }

    /// <summary>The Kubernetes node name, unique within the cluster.</summary>
    [Column("NodeName"), NotNull, MaxLength(253)]
    public required string NodeName { get; set; }

    /// <summary>
    /// The enrolled machine this node is, or null while no enrolled machine has reported it. At most
    /// one node links to any machine.
    /// </summary>
    [Column("MachineId"), Nullable]
    public long? MachineId { get; set; }

    /// <summary>
    /// The CA fingerprint this node's machine most recently reported, which can differ from the
    /// cluster's fingerprint while a rotation or reinstall is in progress.
    /// </summary>
    [Column("ReportedCaFingerprint"), Nullable, MaxLength(64)]
    public string? ReportedCaFingerprint { get; set; }

    /// <summary>
    /// True while the fingerprint this node's machine reports differs from its cluster's, which flags
    /// the node for review without regrouping it.
    /// </summary>
    [Column("FingerprintMismatch"), NotNull]
    public bool FingerprintMismatch { get; set; }

    /// <summary>True when the node's own machine reported that it runs a control plane.</summary>
    [Column("LocalControlPlane"), NotNull]
    public bool LocalControlPlane { get; set; }

    /// <summary>The kubelet version the node's machine reported, if any.</summary>
    [Column("KubeletVersion"), Nullable, MaxLength(64)]
    public string? KubeletVersion { get; set; }

    /// <summary>When the node's own machine last reported it, or null if only another source has.</summary>
    [Column("LastLocalReportAt"), Nullable]
    public DateTimeOffset? LastLocalReportAt { get; set; }
}
