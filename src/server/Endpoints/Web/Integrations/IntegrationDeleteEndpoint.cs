// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Integrations;

/// <summary>
/// Soft-deletes an integration endpoint for the current tenant.
/// Requires TenantAdmin role.
/// </summary>
public sealed class IntegrationDeleteEndpoint : EndpointWithoutRequest
{
    private readonly IIntegrationRepository _integrationRepo;
    private readonly IAuditLogRepository _auditLog;

    /// <summary>
    /// Creates a new instance of the <see cref="IntegrationDeleteEndpoint"/> class.
    /// </summary>
    public IntegrationDeleteEndpoint(
        IIntegrationRepository integrationRepo,
        IAuditLogRepository auditLog)
    {
        _integrationRepo = integrationRepo;
        _auditLog = auditLog;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Delete("/integrations/{id:int}");
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
                ApiResponse<object>.Error("Unauthorized"), ct);

            return;
        }

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Unable to identify user"), ct);

            return;
        }

        int integrationId = Route<int>("id");

        IntegrationEndpoint? integration = await _integrationRepo.GetIntegrationByIdAsync(integrationId, tenantId.Value, ct);
        if (integration is null)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Integration not found"), ct);

            return;
        }

        await _integrationRepo.SoftDeleteIntegrationAsync(integrationId, tenantId.Value, userId.Value, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId.Value, userId.Value, null,
            AuditAction.IntegrationDeleted, AuditResourceType.Integration,
            integrationId.ToString(), null, null), ct);

        HttpContext.Response.StatusCode = 204;
    }
}
