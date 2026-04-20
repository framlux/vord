// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

/// <summary>
/// Request model for changing a member's role.
/// </summary>
public sealed class MemberRoleChangeRequest
{
    /// <summary>
    /// The ID of the user whose role is being changed.
    /// </summary>
    public int UserId { get; set; }

    /// <summary>
    /// The new role to assign to the user.
    /// </summary>
    public string Role { get; set; } = string.Empty;
}

/// <summary>
/// Changes the role of a member within the current tenant.
/// </summary>
public sealed class MemberRoleChangeEndpoint : Endpoint<MemberRoleChangeRequest, ApiResponse<object>>
{
    private readonly IMemberHandler _handler;
    private readonly ILogger<MemberRoleChangeEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="MemberRoleChangeEndpoint"/> class.
    /// </summary>
    public MemberRoleChangeEndpoint(IMemberHandler handler, ILogger<MemberRoleChangeEndpoint> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Put("/members/{UserId}/role");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(MemberRoleChangeRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        int? currentUserId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (currentUserId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Unable to identify user"), ct);

            return;
        }

        ServiceResult<ApiResponse<object>> result = await _handler.ChangeRoleAsync(req.UserId, tenantId, currentUserId.Value, req.Role, ct);

        if (result.IsNotFound)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(result.Data!, ct);

            return;
        }

        _logger.LogInformation("Role of user {TargetUserId} changed in tenant {TenantId} by user {CurrentUserId} to {NewRole}", req.UserId, tenantId, currentUserId.Value, req.Role);

        await Send.OkAsync(result.Data!, cancellation: ct);
    }
}
