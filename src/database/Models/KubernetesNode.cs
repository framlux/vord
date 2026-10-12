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

    /// <summary>
    /// The <c>/etc/machine-id</c> the cluster reports for the node (its <c>systemUUID</c> or
    /// <c>machineID</c>), used to tell which enrolled machine the node is. Null until a cluster
    /// snapshot lists the node.
    /// </summary>
    [Column("NodeMachineId"), Nullable, MaxLength(64)]
    public string? NodeMachineId { get; set; }

    /// <summary>
    /// The node's roles as a JSON array of strings, for example <c>["control-plane","etcd"]</c>, or
    /// null until a cluster snapshot lists the node.
    /// </summary>
    [Column("Roles"), Nullable]
    public string? Roles { get; set; }

    /// <summary>The status of the node's <c>Ready</c> condition: <c>True</c>, <c>False</c> or <c>Unknown</c>.</summary>
    [Column("ReadyStatus"), Nullable, MaxLength(16)]
    public string? ReadyStatus { get; set; }

    /// <summary>When the node's <c>Ready</c> condition last changed.</summary>
    [Column("ReadySince"), Nullable]
    public DateTimeOffset? ReadySince { get; set; }

    /// <summary>
    /// The node's conditions as a JSON array of objects with <c>type</c>, <c>status</c> and
    /// <c>lastTransitionTime</c>, or null until a cluster snapshot lists the node.
    /// </summary>
    [Column("Conditions"), Nullable]
    public string? Conditions { get; set; }

    /// <summary>True while the node is cordoned and takes no new pods.</summary>
    [Column("Unschedulable"), NotNull]
    public bool Unschedulable { get; set; }

    /// <summary>When the node was cordoned, or null when it is schedulable or the time is unknown.</summary>
    [Column("UnschedulableSince"), Nullable]
    public DateTimeOffset? UnschedulableSince { get; set; }

    /// <summary>The container runtime and version the node runs, for example <c>containerd://1.7.23</c>.</summary>
    [Column("ContainerRuntimeVersion"), Nullable, MaxLength(64)]
    public string? ContainerRuntimeVersion { get; set; }

    /// <summary>The number of pods scheduled on the node, or null when the snapshot did not count them.</summary>
    [Column("PodCount"), Nullable]
    public int? PodCount { get; set; }

    /// <summary>The number of pods the node can run.</summary>
    [Column("PodCapacity"), Nullable]
    public int? PodCapacity { get; set; }

    /// <summary>When a cluster snapshot last listed the node, or null if only local reports have.</summary>
    [Column("LastReporterSeenAt"), Nullable]
    public DateTimeOffset? LastReporterSeenAt { get; set; }
}
