// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles invitation business logic.
/// </summary>
public interface IInvitationHandler
{
    /// <summary>
    /// Creates a new tenant invitation.
    /// </summary>
    Task<ServiceResult<InvitationCreateResult>> CreateAsync(string email, string? role, int? tenantId, int userId, string baseUrl, CancellationToken ct);

    /// <summary>
    /// Accepts a tenant invitation.
    /// </summary>
    Task<ServiceResult<InvitationAcceptResult>> AcceptAsync(string token, string userEmail, int userId, string uniqueId, CancellationToken ct);

    /// <summary>
    /// Revokes a pending invitation.
    /// </summary>
    Task<ServiceResult<InvitationRevokeResult>> RevokeAsync(int invitationId, int? tenantId, CancellationToken ct);

    /// <summary>
    /// Resends an invitation by revoking the old one and creating a fresh one.
    /// </summary>
    Task<ServiceResult<InvitationResendResult>> ResendAsync(int invitationId, int? tenantId, int userId, string inviterEmail, string baseUrl, CancellationToken ct);
}
