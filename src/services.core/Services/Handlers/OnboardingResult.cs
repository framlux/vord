// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Result of organization creation.
/// </summary>
public sealed class OnboardingResult
{
    /// <summary>
    /// The created tenant ID.
    /// </summary>
    public int TenantId { get; init; }

    /// <summary>
    /// Error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
