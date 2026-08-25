// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Notifications;

/// <summary>
/// Email service implementation using Resend API.
/// </summary>
public sealed class ResendEmailService : IEmailService
{
    private readonly HttpClient _httpClient;
    private readonly EmailOptions _emailOptions;
    private readonly ILogger<ResendEmailService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="ResendEmailService"/> class.
    /// </summary>
    public ResendEmailService(HttpClient httpClient, IOptions<EmailOptions> emailOptions, ILogger<ResendEmailService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(emailOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _emailOptions = emailOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<EmailDeliveryOutcome> SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, string acceptUrl, CancellationToken ct)
    {
        string apiKey = _emailOptions.Resend.ApiKey;
        string fromEmail = _emailOptions.FromEmail;

        string htmlBody = EmailTemplates.RenderInvitation(tenantName, inviterName, acceptUrl);

        object payload = new
        {
            from = fromEmail,
            to = new[] { toEmail },
            subject = EmailTemplates.InvitationSubject(tenantName),
            html = htmlBody,
        };

        try
        {
            string json = JsonSerializer.Serialize(payload, JsonDefaults.CamelCase);
            using HttpRequestMessage request = new(HttpMethod.Post, "https://api.resend.com/emails");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Invitation email sent to {Email} for tenant {TenantName}", toEmail, tenantName);

                return EmailDeliveryOutcome.Sent;
            }

            string responseBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Resend API returned {StatusCode} for email to {Email}: {Body}", response.StatusCode, toEmail, responseBody);

            return EmailDeliveryOutcome.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send invitation email to {Email}", toEmail);

            return EmailDeliveryOutcome.Failed;
        }
    }

    /// <inheritdoc/>
    public async Task<EmailDeliveryOutcome> SendAlertEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        string apiKey = _emailOptions.Resend.ApiKey;
        string fromEmail = _emailOptions.FromEmail;

        object payload = new { from = fromEmail, to = new[] { toEmail }, subject, html = htmlBody };

        try
        {
            string json = JsonSerializer.Serialize(payload, JsonDefaults.CamelCase);
            using HttpRequestMessage request = new(HttpMethod.Post, "https://api.resend.com/emails");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Alert email sent to {Email}", toEmail);

                return EmailDeliveryOutcome.Sent;
            }

            string responseBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Resend API returned {StatusCode} for alert email to {Email}: {Body}", response.StatusCode, toEmail, responseBody);

            return EmailDeliveryOutcome.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send alert email to {Email}", toEmail);

            return EmailDeliveryOutcome.Failed;
        }
    }
}
