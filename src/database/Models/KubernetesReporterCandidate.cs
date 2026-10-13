// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;
using System.ComponentModel.DataAnnotations;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// A machine that can report for a <see cref="KubernetesCluster"/>: it runs the cluster's reporter
/// and takes part in electing the one that holds the Lease. A machine is a candidate of at most one
/// cluster. Deleting the cluster deletes its candidates.
/// </summary>
[Table(TableNames.KubernetesReporterCandidates)]
public sealed class KubernetesReporterCandidate
{
    /// <summary>The owning tenant, repeated from the cluster so tenant-scoped queries need no join.</summary>
    [Column("TenantId"), NotNull]
    public int TenantId { get; set; }

    /// <summary>The cluster the machine can report for.</summary>
    [PrimaryKey(1)]
    [Column("ClusterId"), NotNull]
    public long ClusterId { get; set; }

    /// <summary>The candidate machine. It is a candidate of one cluster only.</summary>
    [PrimaryKey(2)]
    [Column("MachineId"), NotNull]
    public long MachineId { get; set; }

    /// <summary>
    /// The error this machine's own reporter last reported (<c>credentials_rejected</c>,
    /// <c>tls_ca_changed</c> or <c>state_copied</c>), or null when it reported none.
    /// </summary>
    [Column("ReporterError"), Nullable, MaxLength(32)]
    public string? ReporterError { get; set; }

    /// <summary>When the machine last reported itself as a candidate.</summary>
    [Column("LastReportAt"), NotNull]
    public DateTimeOffset LastReportAt { get; set; }
}
