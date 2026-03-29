// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Onboarding;

/// <summary>
/// Request body for creating an organization.
/// </summary>
public sealed class CreateOrganizationRequest
{
    /// <summary>
    /// The name of the organization.
    /// </summary>
    public string OrganizationName { get; set; } = string.Empty;
}

/// <summary>
/// Response for organization creation.
/// </summary>
public sealed class CreateOrganizationResponse
{
    /// <summary>
    /// The created tenant ID.
    /// </summary>
    public int TenantId { get; set; }
}

/// <summary>
/// Creates a new organization (tenant) during self-service onboarding.
/// Always creates a Free-tier subscription with configurable limits.
/// </summary>
public sealed class CreateOrganizationEndpoint : Endpoint<CreateOrganizationRequest, ApiResponse<CreateOrganizationResponse>>
{
    private readonly IOnboardingHandler _handler;
    private readonly ILogger<CreateOrganizationEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="CreateOrganizationEndpoint"/> class.
    /// </summary>
    public CreateOrganizationEndpoint(
        IOnboardingHandler handler,
        ILogger<CreateOrganizationEndpoint> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/onboarding/create-org");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CreateOrganizationRequest req, CancellationToken ct)
    {
        string? userIdStr = User.FindFirstValue(ClaimTypes.Actor);
        int userId = 0;
        if (int.TryParse(userIdStr, out userId) == false)
        {
            userId = 0;
        }

        string? uniqueId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        ServiceResult<OnboardingResult> result = await _handler.CreateOrganizationAsync(req.OrganizationName, "free", userId, uniqueId ?? string.Empty, ct);
        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<CreateOrganizationResponse>.Error(result.Data?.ErrorMessage ?? "Unknown error"), ct);

            return;
        }

        int tenantId = result.Data!.TenantId;
        _logger.LogInformation("User {UserId} created organization (ID: {TenantId})", userId, tenantId);

        CreateOrganizationResponse response = new() { TenantId = tenantId };

        await Send.OkAsync(ApiResponse<CreateOrganizationResponse>.Ok(response, "Organization created successfully"), cancellation: ct);
    }
}
