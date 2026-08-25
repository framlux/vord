// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Notifications;

/// <summary>
/// Email service used when a self-hosted deployment has configured no SMTP host. Every send
/// reports Skipped, which callers treat as terminal success — retrying could never help.
/// </summary>
public sealed class NoOpEmailService : IEmailService
{
    private readonly ILogger<NoOpEmailService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="NoOpEmailService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public NoOpEmailService(ILogger<NoOpEmailService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<EmailDeliveryOutcome> SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, string acceptUrl, CancellationToken ct)
    {
        _logger.LogInformation("No email transport is configured, so the invitation to {Email} was not sent.", toEmail);

        return Task.FromResult(EmailDeliveryOutcome.Skipped);
    }

    /// <inheritdoc/>
    public Task<EmailDeliveryOutcome> SendAlertEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        _logger.LogInformation("No email transport is configured, so the alert email to {Email} was not sent.", toEmail);

        return Task.FromResult(EmailDeliveryOutcome.Skipped);
    }
}
