// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Kubernetes;

/// <summary>
/// The values of <see cref="MachineKubernetesDto.Grouping"/>: how a machine relates to the clusters
/// of its tenant.
/// </summary>
public static class KubernetesGrouping
{
    /// <summary>The machine is a linked node, or its CA fingerprint is held by exactly one cluster.</summary>
    public const string Grouped = "grouped";

    /// <summary>More than one cluster holds the machine's CA fingerprint, so it cannot be placed.</summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>The machine's report carries no CA fingerprint, so it cannot be placed.</summary>
    public const string Unknown = "unknown";

    /// <summary>No cluster holds the machine's CA fingerprint yet.</summary>
    public const string Ungrouped = "ungrouped";
}
