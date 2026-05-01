// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

/// <summary>
/// DTO for listing invitations.
/// </summary>
public sealed class InvitationListDto
{
    /// <summary>
    /// The invitation ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The invited email address.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The invitation status.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// When the invitation was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the invitation expires.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// The role assigned to the invitation.
    /// </summary>
    public string Role { get; set; } = string.Empty;
}

/// <summary>
/// Lists all invitations for the current tenant.
/// </summary>
public sealed class InvitationListEndpoint : EndpointWithoutRequest<ApiResponse<List<InvitationListDto>>>
{
    private readonly IInvitationRepository _invitationRepository;

    /// <summary>
    /// Creates a new instance of the <see cref="InvitationListEndpoint"/> class.
    /// </summary>
    public InvitationListEndpoint(IInvitationRepository invitationRepository)
    {
        _invitationRepository = invitationRepository;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/invitations");
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
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<List<InvitationListDto>>.Error("Unauthorized"), ct);

            return;
        }

        IEnumerable<TenantInvitation> invitations = await _invitationRepository.GetInvitationsForTenantAsync(tenantId.Value, ct);
        List<InvitationListDto> dtos = invitations.Select(i => new InvitationListDto
        {
            Id = i.Id,
            Email = i.Email,
            Status = i.ExpiresAt < DateTimeOffset.UtcNow && i.Status == Database.Enums.InvitationStatus.Pending
                ? "Expired"
                : i.Status.ToString(),
            CreatedAt = i.CreatedAt,
            ExpiresAt = i.ExpiresAt,
            Role = i.Role.ToString(),
        }).ToList();

        await Send.OkAsync(ApiResponse<List<InvitationListDto>>.Ok(dtos), cancellation: ct);
    }
}
