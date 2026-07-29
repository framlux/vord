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
    /// <returns>
    /// <see cref="EmailDeliveryOutcome.Sent"/> if the provider accepted the message,
    /// <see cref="EmailDeliveryOutcome.Skipped"/> if no provider is configured (a supported
    /// self-hosted deployment, not a failure), or <see cref="EmailDeliveryOutcome.Failed"/> if the
    /// provider rejected the message or was unreachable.
    /// </returns>
    Task<EmailDeliveryOutcome> SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, string acceptUrl, CancellationToken ct);

    /// <summary>
    /// Sends a pre-rendered alert notification email to a single recipient.
    /// </summary>
    /// <param name="toEmail">The recipient email address.</param>
    /// <param name="subject">The email subject line.</param>
    /// <param name="htmlBody">The rendered HTML body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="EmailDeliveryOutcome.Sent"/> if the provider accepted the message,
    /// <see cref="EmailDeliveryOutcome.Skipped"/> if no provider is configured (a supported
    /// self-hosted deployment, not a failure), or <see cref="EmailDeliveryOutcome.Failed"/> if the
    /// provider rejected the message or was unreachable.
    /// </returns>
    Task<EmailDeliveryOutcome> SendAlertEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct);
}
