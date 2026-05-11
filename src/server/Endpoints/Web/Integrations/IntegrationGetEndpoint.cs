// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Integrations;

/// <summary>
/// Returns a single integration endpoint by ID for the current tenant.
/// Requires TenantAdmin role.
/// </summary>
public sealed class IntegrationGetEndpoint : EndpointWithoutRequest<ApiResponse<IntegrationEndpointDto>>
{
    private readonly IIntegrationRepository _integrationRepo;

    /// <summary>
    /// Creates a new instance of the <see cref="IntegrationGetEndpoint"/> class.
    /// </summary>
    public IntegrationGetEndpoint(IIntegrationRepository integrationRepo)
    {
        _integrationRepo = integrationRepo;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/integrations/{id:int}");
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
                ApiResponse<IntegrationEndpointDto>.Error("Unauthorized"), ct);

            return;
        }

        int integrationId = Route<int>("id");

        IntegrationEndpoint? integration = await _integrationRepo.GetIntegrationByIdAsync(integrationId, tenantId.Value, ct);
        if (integration is null)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<IntegrationEndpointDto>.Error("Integration not found"), ct);

            return;
        }

        IntegrationEndpointDto dto = new()
        {
            Id = integration.Id,
            Provider = integration.Provider.ToString(),
            Name = integration.Name,
            IsEnabled = integration.IsEnabled,
            CreatedAt = integration.CreatedAt.ToString("o"),
        };

        await Send.OkAsync(ApiResponse<IntegrationEndpointDto>.Ok(dto), cancellation: ct);
    }
}
