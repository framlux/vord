// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

/// <summary>
/// Revokes a pending invitation.
/// </summary>
public sealed class InvitationRevokeEndpoint : EndpointWithoutRequest<ApiResponse<object>>
{
    private readonly InvitationHandler _handler;
    private readonly ILogger<InvitationRevokeEndpoint> _logger;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="InvitationRevokeEndpoint"/> class.
    /// </summary>
    public InvitationRevokeEndpoint(InvitationHandler handler, ILogger<InvitationRevokeEndpoint> logger, ITenantContext tenantContext)
    {
        _handler = handler;
        _logger = logger;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/invitations/{id}/revoke");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int invitationId = Route<int>("id");
        int? tenantId = _tenantContext.TenantId;

        ServiceResult<InvitationRevokeResult> result = await _handler.RevokeAsync(invitationId, tenantId, ct);

        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Invitation not found", ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            await HttpContext.SendApiErrorAsync(result.StatusCode, result.Data?.ErrorMessage ?? "Unknown error", ct);

            return;
        }

        _logger.LogInformation("Invitation {InvitationId} revoked in tenant {TenantId}", invitationId, tenantId);

        await Send.OkAsync(ApiResponse<object>.Ok(new { }, "Invitation revoked"), cancellation: ct);
    }
}
