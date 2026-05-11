// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Integrations;

/// <summary>
/// Represents a configuration field for a provider.
/// </summary>
public sealed class IntegrationConfigFieldDto
{
    /// <summary>The field key used in configuration dictionary.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The human-readable label for the field.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The input type (text, url, password).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Placeholder text for the input field.</summary>
    public string Placeholder { get; set; } = string.Empty;

    /// <summary>Help text describing the field.</summary>
    public string HelpText { get; set; } = string.Empty;

    /// <summary>Optional URL to documentation for the field.</summary>
    public string? HelpUrl { get; set; }
}

/// <summary>
/// Represents metadata about an integration provider.
/// </summary>
public sealed class IntegrationProviderDto
{
    /// <summary>The provider identifier.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The display name for the provider.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>A short description of the provider.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The configuration fields required for this provider.</summary>
    public List<IntegrationConfigFieldDto> ConfigFields { get; set; } = [];
}

/// <summary>
/// Returns static metadata about available integration providers.
/// Requires ViewOnly role (accessible to all tenant members).
/// </summary>
public sealed class IntegrationProvidersEndpoint : EndpointWithoutRequest<ApiResponse<List<IntegrationProviderDto>>>
{
    private static readonly List<IntegrationProviderDto> Providers =
    [
        new IntegrationProviderDto
        {
            Provider = "Slack",
            DisplayName = "Slack",
            Description = "Send alert notifications to a Slack channel via incoming webhook.",
            ConfigFields =
            [
                new IntegrationConfigFieldDto
                {
                    Key = "webhookUrl",
                    Label = "Webhook URL",
                    Type = "url",
                    Placeholder = "https://hooks.slack.com/services/T00000000/B00000000/XXXXXXXXXXXXXXXXXXXXXXXX",
                    HelpText = "The incoming webhook URL from your Slack app configuration.",
                    HelpUrl = "https://api.slack.com/messaging/webhooks",
                },
            ],
        },
        new IntegrationProviderDto
        {
            Provider = "MicrosoftTeams",
            DisplayName = "Microsoft Teams",
            Description = "Send alert notifications to a Microsoft Teams channel via webhook connector.",
            ConfigFields =
            [
                new IntegrationConfigFieldDto
                {
                    Key = "webhookUrl",
                    Label = "Webhook URL",
                    Type = "url",
                    Placeholder = "https://outlook.webhook.office.com/webhookb2/...",
                    HelpText = "The incoming webhook URL from your Teams channel connector.",
                    HelpUrl = "https://learn.microsoft.com/en-us/microsoftteams/platform/webhooks-and-connectors/how-to/add-incoming-webhook",
                },
            ],
        },
        new IntegrationProviderDto
        {
            Provider = "Discord",
            DisplayName = "Discord",
            Description = "Send alert notifications to a Discord channel via webhook.",
            ConfigFields =
            [
                new IntegrationConfigFieldDto
                {
                    Key = "webhookUrl",
                    Label = "Webhook URL",
                    Type = "url",
                    Placeholder = "https://discord.com/api/webhooks/000000000000000000/XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
                    HelpText = "The webhook URL from your Discord channel integrations settings.",
                    HelpUrl = "https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks",
                },
            ],
        },
        new IntegrationProviderDto
        {
            Provider = "PagerDuty",
            DisplayName = "PagerDuty",
            Description = "Trigger incidents in PagerDuty using the Events API v2.",
            ConfigFields =
            [
                new IntegrationConfigFieldDto
                {
                    Key = "routingKey",
                    Label = "Routing Key",
                    Type = "password",
                    Placeholder = "32-character hexadecimal routing key",
                    HelpText = "The integration key (routing key) from your PagerDuty service integration.",
                    HelpUrl = "https://support.pagerduty.com/main/docs/services-and-integrations",
                },
            ],
        },
        new IntegrationProviderDto
        {
            Provider = "Custom",
            DisplayName = "Custom Webhook",
            Description = "Send HMAC-signed alert payloads to any HTTPS endpoint.",
            ConfigFields =
            [
                new IntegrationConfigFieldDto
                {
                    Key = "url",
                    Label = "Endpoint URL",
                    Type = "url",
                    Placeholder = "https://your-server.example.com/webhook",
                    HelpText = "The HTTPS URL that will receive signed alert payloads.",
                },
            ],
        },
    ];

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/integrations/providers");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<List<IntegrationProviderDto>>.Error("Unauthorized"), ct);

            return;
        }

        await Send.OkAsync(ApiResponse<List<IntegrationProviderDto>>.Ok(Providers), cancellation: ct);
    }
}
