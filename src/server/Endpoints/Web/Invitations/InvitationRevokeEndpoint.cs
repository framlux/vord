// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

/// <summary>
/// Revokes a pending invitation.
/// </summary>
public sealed class InvitationRevokeEndpoint : EndpointWithoutRequest<ApiResponse<object>>
{
    private readonly IInvitationHandler _handler;
    private readonly ILogger<InvitationRevokeEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="InvitationRevokeEndpoint"/> class.
    /// </summary>
    public InvitationRevokeEndpoint(IInvitationHandler handler, ILogger<InvitationRevokeEndpoint> logger)
    {
        _handler = handler;
        _logger = logger;
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
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        ServiceResult<InvitationRevokeResult> result = await _handler.RevokeAsync(invitationId, tenantId, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Invitation not found"), ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<object>.Error(result.Data?.ErrorMessage ?? "Unknown error"), ct);

            return;
        }

        _logger.LogInformation("Invitation {InvitationId} revoked in tenant {TenantId}", invitationId, tenantId);

        await Send.OkAsync(ApiResponse<object>.Ok(new { }, "Invitation revoked"), cancellation: ct);
    }
}
