// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles organization onboarding business logic.
/// </summary>
public interface IOnboardingHandler
{
    /// <summary>
    /// Creates a new organization (tenant) with subscription and admin role.
    /// </summary>
    Task<ServiceResult<OnboardingResult>> CreateOrganizationAsync(string organizationName, string tier, int userId, string uniqueId, CancellationToken ct);
}

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
