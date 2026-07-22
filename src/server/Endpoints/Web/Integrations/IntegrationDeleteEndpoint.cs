// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Integrations;

/// <summary>
/// Soft-deletes an integration endpoint for the current tenant.
/// Requires TenantAdmin role.
/// </summary>
public sealed class IntegrationDeleteEndpoint : EndpointWithoutRequest
{
    private readonly IIntegrationRepository _integrationRepo;
    private readonly IAuditLogRepository _auditLog;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="IntegrationDeleteEndpoint"/> class.
    /// </summary>
    public IntegrationDeleteEndpoint(
        IIntegrationRepository integrationRepo,
        IAuditLogRepository auditLog,
        ITenantContext tenantContext)
    {
        _integrationRepo = integrationRepo;
        _auditLog = auditLog;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Delete("/integrations/{id:int}");
        Policies(AuthorizationPolicies.TenantAdmin);
        Tags(EndpointTags.RequiresTenant);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int tenantId = _tenantContext.RequireTenantId();

        int? userId = _tenantContext.UserId;
        if (userId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unable to identify user", ct);

            return;
        }

        int integrationId = Route<int>("id");

        IntegrationEndpoint? integration = await _integrationRepo.GetIntegrationByIdAsync(integrationId, tenantId, ct);
        if (integration is null)
        {
            await HttpContext.SendApiErrorAsync(404, "Integration not found", ct);

            return;
        }

        await _integrationRepo.SoftDeleteIntegrationAsync(integrationId, tenantId, userId.Value, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId.Value, null,
            AuditAction.IntegrationDeleted, AuditResourceType.Integration,
            integrationId.ToString(), null, null), ct);

        HttpContext.Response.StatusCode = 204;
    }
}
