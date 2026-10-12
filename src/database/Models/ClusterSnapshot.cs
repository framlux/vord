// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// One parsed cluster snapshot from the machine that holds the cluster's reporter Lease, ready to
/// apply to the inventory.
/// </summary>
/// <param name="TelemetryId">The <see cref="MachineTelemetry.Id"/> of the row the snapshot came from.</param>
/// <param name="TenantId">The tenant that owns the sending machine.</param>
/// <param name="MachineId">The machine that sent the snapshot.</param>
/// <param name="ServerReceivedAt">When the server accepted the snapshot, which orders snapshots of one Lease epoch.</param>
/// <param name="KubeSystemUid">The UID of the cluster's <c>kube-system</c> namespace, or null when the snapshot has none.</param>
/// <param name="CaFingerprint">The lowercase hex SHA-256 of the cluster CA certificate as the sender sees it, or null when it could not tell.</param>
/// <param name="ClusterVersion">The Kubernetes server version, or null when the snapshot has none.</param>
/// <param name="RoleOutdated">True while the installed reporter permissions are older than the agent expects.</param>
/// <param name="LeaseTransitions">The Lease's transition count when the snapshot was taken.</param>
/// <param name="LeaseUid">The <c>metadata.uid</c> of the Lease the snapshot was taken under, or null when it carries none.</param>
/// <param name="LeaseCreatedAt">When that Lease was created, which orders the Lease epochs of one cluster.</param>
/// <param name="ReporterError">The error the snapshot reports, or null on a snapshot of the cluster.</param>
/// <param name="Truncated">True when nodes were left out to keep the snapshot within its size cap.</param>
/// <param name="Nodes">The nodes the snapshot lists, none on an error snapshot.</param>
public sealed record ClusterSnapshot(
    long TelemetryId,
    int TenantId,
    long MachineId,
    DateTimeOffset ServerReceivedAt,
    string? KubeSystemUid,
    string? CaFingerprint,
    string? ClusterVersion,
    bool RoleOutdated,
    long LeaseTransitions,
    string? LeaseUid,
    DateTimeOffset LeaseCreatedAt,
    string? ReporterError,
    bool Truncated,
    IReadOnlyList<ClusterSnapshotNode> Nodes);
