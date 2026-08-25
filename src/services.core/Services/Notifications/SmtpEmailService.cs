// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Framlux.FleetManagement.Services.Core.Notifications;

/// <summary>
/// Email service that delivers through an operator-supplied SMTP relay. This is the transport for
/// self-hosted deployments, where requiring an account with a hosted email provider would be a
/// barrier to running the product at all.
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    private readonly EmailOptions _emailOptions;
    private readonly ILogger<SmtpEmailService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="SmtpEmailService"/> class.
    /// </summary>
    /// <param name="emailOptions">The bound email configuration.</param>
    /// <param name="logger">The logger.</param>
    public SmtpEmailService(IOptions<EmailOptions> emailOptions, ILogger<SmtpEmailService> logger)
    {
        ArgumentNullException.ThrowIfNull(emailOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _emailOptions = emailOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<EmailDeliveryOutcome> SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, string acceptUrl, CancellationToken ct)
    {
        return SendAsync(
            toEmail,
            EmailTemplates.InvitationSubject(tenantName),
            EmailTemplates.RenderInvitation(tenantName, inviterName, acceptUrl),
            ct);
    }

    /// <inheritdoc/>
    public Task<EmailDeliveryOutcome> SendAlertEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        return SendAsync(toEmail, subject, htmlBody, ct);
    }

    private async Task<EmailDeliveryOutcome> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        SmtpEmailOptions smtp = _emailOptions.Smtp;

        try
        {
            // Address parsing is inside the guarded region on purpose. Email:FromEmail is only
            // checked for presence at startup, never for parseability, so a mistyped sender would
            // otherwise throw out of every send instead of reporting a delivery failure the caller
            // can act on.
            MimeMessage message = new();
            message.From.Add(MailboxAddress.Parse(_emailOptions.FromEmail));
            message.To.Add(MailboxAddress.Parse(toEmail));
            message.Subject = subject;
            message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

            using SmtpClient client = new();

            SecureSocketOptions socketOptions = smtp.UseStartTls
                ? SecureSocketOptions.StartTls
                : SecureSocketOptions.None;

            await client.ConnectAsync(smtp.Host, smtp.Port, socketOptions, ct);

            if (string.IsNullOrWhiteSpace(smtp.Username) == false)
            {
                await client.AuthenticateAsync(smtp.Username, smtp.Password, ct);
            }

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            _logger.LogInformation("Email sent to {Email} via SMTP host {Host}", toEmail, smtp.Host);

            return EmailDeliveryOutcome.Sent;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email} via SMTP host {Host}", toEmail, smtp.Host);

            return EmailDeliveryOutcome.Failed;
        }
    }
}
