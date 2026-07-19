// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

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
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="MemberRoleChangeEndpoint"/> class.
    /// </summary>
    public MemberRoleChangeEndpoint(IMemberHandler handler, ILogger<MemberRoleChangeEndpoint> logger, ITenantContext tenantContext)
    {
        _handler = handler;
        _logger = logger;
        _tenantContext = tenantContext;
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
        int? tenantId = _tenantContext.TenantId;

        int? currentUserId = _tenantContext.UserId;
        if (currentUserId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unable to identify user", ct);

            return;
        }

        ServiceResult<ApiResponse<object>> result = await _handler.ChangeRoleAsync(req.UserId, tenantId, currentUserId.Value, req.Role, ct);

        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Member not found", ct);

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
