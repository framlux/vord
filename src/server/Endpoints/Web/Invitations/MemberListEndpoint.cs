// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

/// <summary>
/// DTO for a tenant member.
/// </summary>
public sealed class MemberDto
{
    /// <summary>
    /// The user ID.
    /// </summary>
    public int UserId { get; set; }

    /// <summary>
    /// The user's email address.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The user's role in the tenant.
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// When the user joined the tenant.
    /// </summary>
    public DateTimeOffset JoinedAt { get; set; }
}

/// <summary>
/// Lists all active members of the current tenant.
/// </summary>
public sealed class MemberListEndpoint : EndpointWithoutRequest<ApiResponse<List<MemberDto>>>
{
    private readonly IDatabaseCache _databaseCache;

    /// <summary>
    /// Creates a new instance of the <see cref="MemberListEndpoint"/> class.
    /// </summary>
    public MemberListEndpoint(IDatabaseCache databaseCache)
    {
        _databaseCache = databaseCache;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/members");
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
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<List<MemberDto>>.Error("Unauthorized"), ct);

            return;
        }

        IEnumerable<UserTenantRole> members = await _databaseCache.GetMembersForTenantAsync(tenantId.Value, ct);
        List<MemberDto> dtos = members.Select(m => new MemberDto
        {
            UserId = m.UserId,
            Email = m.User?.Username ?? "Unknown",
            Role = m.Role.ToString(),
            JoinedAt = m.AssignedAt,
        }).ToList();

        await Send.OkAsync(ApiResponse<List<MemberDto>>.Ok(dtos), cancellation: ct);
    }
}
