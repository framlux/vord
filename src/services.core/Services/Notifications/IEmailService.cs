// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Notifications;

/// <summary>
/// Service for sending emails.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Sends an invitation email to the specified address.
    /// </summary>
    /// <param name="toEmail">The recipient email address.</param>
    /// <param name="tenantName">The name of the tenant the user is being invited to.</param>
    /// <param name="inviterName">The name of the user who sent the invitation.</param>
    /// <param name="acceptUrl">The URL to accept the invitation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Returns true if the email was sent successfully; otherwise, false.</returns>
    Task<bool> SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, string acceptUrl, CancellationToken ct);
}
