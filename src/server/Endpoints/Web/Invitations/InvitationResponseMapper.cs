// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Handlers;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

/// <summary>
/// Shared mapping helpers for invitation delivery responses.
/// </summary>
internal static class InvitationResponseMapper
{
    /// <summary>
    /// Maps an <see cref="InvitationDeliveryResult"/> to the wire-facing <see cref="InvitationResponse"/>.
    /// </summary>
    internal static InvitationResponse ToResponse(InvitationDeliveryResult result)
    {
        return new InvitationResponse
        {
            Id = result.Id,
            Email = result.Email,
            Token = result.Token,
            AcceptUrl = result.AcceptUrl,
            ExpiresAt = result.ExpiresAt,
            Status = result.Status,
        };
    }
}
