// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

/// <summary>
/// DTO for invitation details (public info for the accept page).
/// </summary>
public sealed class InvitationDetailDto
{
    /// <summary>
    /// The tenant name.
    /// </summary>
    public string TenantName { get; set; } = string.Empty;

    /// <summary>
    /// The email of the person who sent the invitation.
    /// </summary>
    public string InviterEmail { get; set; } = string.Empty;

    /// <summary>
    /// The email the invitation was sent to.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// When the invitation expires.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// The invitation status.
    /// </summary>
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Gets invitation details by token for the accept page.
/// </summary>
public sealed class InvitationDetailEndpoint : EndpointWithoutRequest<ApiResponse<InvitationDetailDto>>
{
    private readonly IInvitationRepository _invitationRepository;

    /// <summary>
    /// Creates a new instance of the <see cref="InvitationDetailEndpoint"/> class.
    /// </summary>
    public InvitationDetailEndpoint(IInvitationRepository invitationRepository)
    {
        _invitationRepository = invitationRepository;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/invitations/by-token/{token}");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        string token = Route<string>("token") ?? string.Empty;

        TenantInvitation? invitation = await _invitationRepository.GetInvitationByTokenAsync(token, ct);
        if (invitation is null)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        string status = invitation.Status.ToString();
        if (invitation.Status == InvitationStatus.Pending && invitation.ExpiresAt < DateTimeOffset.UtcNow)
        {
            status = "Expired";
        }

        InvitationDetailDto dto = new()
        {
            TenantName = invitation.Tenant?.Name ?? "Unknown",
            InviterEmail = invitation.InvitedByUser?.Username ?? "Unknown",
            Email = invitation.Email,
            ExpiresAt = invitation.ExpiresAt,
            Status = status,
        };

        await Send.OkAsync(ApiResponse<InvitationDetailDto>.Ok(dto), cancellation: ct);
    }
}
