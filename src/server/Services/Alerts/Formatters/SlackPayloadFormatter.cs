// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text;
using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Alerts.Formatters;

/// <summary>
/// Formats alert payloads as Slack Block Kit messages for incoming webhook delivery.
/// </summary>
public sealed class SlackPayloadFormatter : IIntegrationPayloadFormatter
{
    /// <inheritdoc/>
    public IntegrationProvider Provider => IntegrationProvider.Slack;

    /// <inheritdoc/>
    public HttpRequestMessage FormatRequest(AlertEvent alertEvent, AlertRule rule, IntegrationEndpoint integration)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(integration);

        using JsonDocument config = JsonDocument.Parse(integration.Configuration);
        string webhookUrl = config.RootElement.GetProperty("webhookUrl").GetString()
            ?? throw new InvalidOperationException("Slack configuration missing webhookUrl");

        string severityEmoji = alertEvent.Severity switch
        {
            AlertSeverity.Critical => ":rotating_light:",
            AlertSeverity.Warning => ":warning:",
            _ => ":information_source:",
        };

        object payload = new
        {
            blocks = new object[]
            {
                new
                {
                    type = "header",
                    text = new { type = "plain_text", text = $"{severityEmoji} {rule.Name}" },
                },
                new
                {
                    type = "section",
                    fields = new object[]
                    {
                        new { type = "mrkdwn", text = $"*Severity:*\n{alertEvent.Severity}" },
                        new { type = "mrkdwn", text = $"*Machine:*\n{alertEvent.MachineId}" },
                    },
                },
                new
                {
                    type = "section",
                    text = new { type = "mrkdwn", text = alertEvent.Message },
                },
                new
                {
                    type = "context",
                    elements = new object[]
                    {
                        new { type = "mrkdwn", text = $"Triggered at {alertEvent.TriggeredAt:O}" },
                    },
                },
            },
        };

        string json = JsonSerializer.Serialize(payload, JsonDefaults.CamelCase);

        HttpRequestMessage request = new(HttpMethod.Post, webhookUrl);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        return request;
    }
}
