// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Integrations;

/// <summary>
/// Returns all integration endpoints for the current tenant.
/// Requires TenantAdmin role.
/// </summary>
public sealed class IntegrationListEndpoint : EndpointWithoutRequest<ApiResponse<List<IntegrationEndpointDto>>>
{
    private readonly IIntegrationRepository _integrationRepo;

    /// <summary>
    /// Creates a new instance of the <see cref="IntegrationListEndpoint"/> class.
    /// </summary>
    public IntegrationListEndpoint(IIntegrationRepository integrationRepo)
    {
        _integrationRepo = integrationRepo;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/integrations");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<List<IntegrationEndpointDto>>.Error("Unauthorized"), ct);

            return;
        }

        List<IntegrationEndpoint> integrations = await _integrationRepo.GetIntegrationsForTenantAsync(tenantId.Value, ct);

        List<IntegrationEndpointDto> dtos = integrations.Select(i => new IntegrationEndpointDto
        {
            Id = i.Id,
            Provider = i.Provider.ToString(),
            Name = i.Name,
            IsEnabled = i.IsEnabled,
            CreatedAt = i.CreatedAt.ToString("o"),
        }).ToList();

        await Send.OkAsync(ApiResponse<List<IntegrationEndpointDto>>.Ok(dtos), cancellation: ct);
    }
}
