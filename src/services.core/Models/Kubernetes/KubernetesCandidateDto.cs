// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Kubernetes;

/// <summary>
/// A machine enrolled as a reporter candidate of a cluster.
/// </summary>
public sealed class KubernetesCandidateDto
{
    /// <summary>The machine's ID.</summary>
    public long MachineId { get; set; }

    /// <summary>The machine's name.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>
    /// True when this machine sent the cluster's most recent accepted snapshot and that snapshot is not stale.
    /// The server cannot see the Lease, so this is not proof the machine holds it now.
    /// </summary>
    public bool IsActiveReporter { get; set; }

    /// <summary>The error the machine's own reporter last reported, or null when it reported none.</summary>
    public string? ReporterError { get; set; }

    /// <summary>When the machine last reported itself as a candidate.</summary>
    public DateTimeOffset LastReportAt { get; set; }

    /// <summary>Whether the server has heard from the machine lately, by the same rule as the machine list.</summary>
    public bool Online { get; set; }
}
