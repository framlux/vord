// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// One condition of a node that is not in its healthy state, such as MemoryPressure being True.
/// </summary>
public sealed class KubernetesNodeConditionDto
{
    /// <summary>The condition type, such as MemoryPressure, DiskPressure, PIDPressure or NetworkUnavailable.</summary>
    public string Type { get; set; } = "";

    /// <summary>The condition status: True or Unknown, since a condition that is False is not reported.</summary>
    public string Status { get; set; } = "";

    /// <summary>When the condition last changed status, as the cluster stated it (ISO 8601).</summary>
    public string LastTransitionTime { get; set; } = "";
}
