// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.History;

/// <summary>
/// Error response when a history request exceeds the tenant's retention tier.
/// </summary>
public sealed class HistoryRetentionErrorDto
{
    /// <summary>Error message.</summary>
    public required string Message { get; init; }

    /// <summary>Whether an upgrade is required to access this time range.</summary>
    public required bool UpgradeRequired { get; init; }

    /// <summary>The tenant's current retention in days.</summary>
    public required int CurrentRetentionDays { get; init; }
}
