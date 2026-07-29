// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Hangfire;

namespace Framlux.FleetManagement.Services.Core.Notifications;

/// <summary>
/// Hangfire job that sends an invitation email with automatic retry on failure.
/// Moves email delivery off the request path so a transient Resend outage or rate-limit
/// does not surface as a user-visible error. If <see cref="IEmailService.SendInvitationEmailAsync"/>
/// returns <see cref="EmailDeliveryOutcome.Failed"/> the job throws so Hangfire's
/// <see cref="AutomaticRetryAttribute"/> retries. <see cref="EmailDeliveryOutcome.Skipped"/> (no
/// email provider configured — a supported deployment) completes quietly instead.
/// </summary>
public sealed class SendInvitationEmailJob
{
    private readonly IEmailService _emailService;
    private readonly ILogger<SendInvitationEmailJob> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="SendInvitationEmailJob"/> class.
    /// </summary>
    public SendInvitationEmailJob(IEmailService emailService, ILogger<SendInvitationEmailJob> logger)
    {
        ArgumentNullException.ThrowIfNull(emailService);
        ArgumentNullException.ThrowIfNull(logger);

        _emailService = emailService;
        _logger = logger;
    }

    /// <summary>
    /// Sends an invitation email to the specified address. Throws <see cref="InvalidOperationException"/>
    /// when the underlying email service returns <see cref="EmailDeliveryOutcome.Failed"/> so that
    /// Hangfire retries the job. <see cref="EmailDeliveryOutcome.Skipped"/> — no email provider
    /// configured — completes quietly rather than throwing.
    /// </summary>
    /// <param name="toEmail">The recipient email address.</param>
    /// <param name="tenantName">The name of the tenant the user is being invited to.</param>
    /// <param name="inviterName">The name of the user who sent the invitation.</param>
    /// <param name="acceptUrl">The URL to accept the invitation.</param>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new int[] { 10, 30, 60 })]
    public async Task SendAsync(string toEmail, string tenantName, string inviterName, string acceptUrl, CancellationToken ct)
    {
        EmailDeliveryOutcome outcome = await _emailService.SendInvitationEmailAsync(toEmail, tenantName, inviterName, acceptUrl, ct);

        if (outcome == EmailDeliveryOutcome.Failed)
        {
            _logger.LogWarning("Invitation email to {Email} for tenant {TenantName} failed; Hangfire will retry", toEmail, tenantName);

            throw new InvalidOperationException($"Failed to send invitation email to {toEmail} for tenant '{tenantName}'. Hangfire will retry.");
        }
    }
}
