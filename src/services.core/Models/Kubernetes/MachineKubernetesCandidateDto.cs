// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Kubernetes;

/// <summary>
/// A machine's state as a reporter candidate, shown on its own page.
/// </summary>
public sealed class MachineKubernetesCandidateDto
{
    /// <summary>The ID of the cluster the machine is a candidate of.</summary>
    public long ClusterId { get; set; }

    /// <summary>True while the machine holds the cluster's reporter Lease.</summary>
    public bool IsHolder { get; set; }

    /// <summary>The error the machine's own reporter last reported, or null when it reported none.</summary>
    public string? ReporterError { get; set; }

    /// <summary>When the machine last reported itself as a candidate.</summary>
    public DateTimeOffset LastReportAt { get; set; }
}
