// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

/// <summary>
/// Removes a member from the current tenant.
/// </summary>
public sealed class MemberRemoveEndpoint : EndpointWithoutRequest<ApiResponse<object>>
{
    private readonly IMemberHandler _handler;
    private readonly ILogger<MemberRemoveEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="MemberRemoveEndpoint"/> class.
    /// </summary>
    public MemberRemoveEndpoint(IMemberHandler handler, ILogger<MemberRemoveEndpoint> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/members/{userId}/remove");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int targetUserId = Route<int>("userId");
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        string? userIdStr = User.FindFirstValue(ClaimTypes.Actor);
        int currentUserId = int.TryParse(userIdStr, out int uid) ? uid : 0;

        ServiceResult<ApiResponse<object>> result = await _handler.RemoveAsync(targetUserId, tenantId, currentUserId, ct);

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

        _logger.LogInformation("User {TargetUserId} removed from tenant {TenantId} by user {CurrentUserId}", targetUserId, tenantId, currentUserId);

        await Send.OkAsync(result.Data!, cancellation: ct);
    }
}
