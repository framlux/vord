// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Notifications;

/// <summary>
/// Email service implementation using Resend API.
/// </summary>
public sealed class ResendEmailService : IEmailService
{
    private readonly HttpClient _httpClient;
    private readonly ResendOptions _resendOptions;
    private readonly ILogger<ResendEmailService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="ResendEmailService"/> class.
    /// </summary>
    public ResendEmailService(HttpClient httpClient, IOptions<ResendOptions> resendOptions, ILogger<ResendEmailService> logger)
    {
        _httpClient = httpClient;
        _resendOptions = resendOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, string acceptUrl, CancellationToken ct)
    {
        string apiKey = _resendOptions.ApiKey;
        string fromEmail = string.IsNullOrEmpty(_resendOptions.FromEmail) == false
            ? _resendOptions.FromEmail
            : "Framlux Vord <invitations@vordfleet.dev>";

        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("Resend API key not configured — skipping invitation email to {Email}", toEmail);

            return false;
        }

        string htmlBody = $"""
            <div style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 560px; margin: 0 auto; padding: 40px 20px;">
                <h2 style="color: #1a1a1a; margin-bottom: 8px;">You've been invited to join {HtmlEncode(tenantName)}</h2>
                <p style="color: #666; font-size: 15px; line-height: 1.5;">
                    {HtmlEncode(inviterName)} has invited you to join <strong>{HtmlEncode(tenantName)}</strong> on Framlux Vord.
                </p>
                <div style="margin: 32px 0;">
                    <a href="{HtmlEncode(acceptUrl)}" style="display: inline-block; background-color: #6366f1; color: #ffffff; text-decoration: none; padding: 12px 32px; border-radius: 8px; font-weight: 600; font-size: 15px;">
                        Accept Invitation
                    </a>
                </div>
                <p style="color: #999; font-size: 13px; line-height: 1.5;">
                    This invitation expires in 7 days. If you did not expect this email, you can safely ignore it.
                </p>
                <hr style="border: none; border-top: 1px solid #eee; margin: 32px 0;" />
                <p style="color: #bbb; font-size: 12px;">Framlux Vord &mdash; Fleet Monitoring</p>
            </div>
            """;

        object payload = new
        {
            from = fromEmail,
            to = new[] { toEmail },
            subject = $"You've been invited to join {tenantName} on Framlux Vord",
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

                return true;
            }

            string responseBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Resend API returned {StatusCode} for email to {Email}: {Body}", response.StatusCode, toEmail, responseBody);

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send invitation email to {Email}", toEmail);

            return false;
        }
    }

    private static string HtmlEncode(string value)
    {
        return System.Net.WebUtility.HtmlEncode(value);
    }
}
