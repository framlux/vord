// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text;
using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Services.Core.Alerts.Formatters;

/// <summary>
/// Formats alert payloads as Microsoft Teams Adaptive Card messages for webhook connector delivery.
/// </summary>
public sealed class TeamsPayloadFormatter : IIntegrationPayloadFormatter
{
    /// <inheritdoc/>
    public IntegrationProvider Provider => IntegrationProvider.MicrosoftTeams;

    /// <inheritdoc/>
    public HttpRequestMessage FormatRequest(AlertEvent alertEvent, AlertRule rule, IntegrationEndpoint integration)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(integration);

        using JsonDocument config = JsonDocument.Parse(integration.Configuration);
        string webhookUrl = config.RootElement.GetProperty("webhookUrl").GetString()
            ?? throw new InvalidOperationException("Teams configuration missing webhookUrl");

        string severityColor = alertEvent.Severity switch
        {
            AlertSeverity.Critical => "attention",
            AlertSeverity.Warning => "warning",
            _ => "default",
        };

        object payload = new
        {
            type = "message",
            attachments = new object[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new Dictionary<string, object>
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.4",
                        ["body"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["type"] = "TextBlock",
                                ["text"] = rule.Name,
                                ["weight"] = "Bolder",
                                ["size"] = "Medium",
                                ["color"] = severityColor,
                            },
                            new Dictionary<string, object>
                            {
                                ["type"] = "FactSet",
                                ["facts"] = new object[]
                                {
                                    new { title = "Severity", value = alertEvent.Severity.ToString() },
                                    new { title = "Machine", value = alertEvent.MachineId.ToString() },
                                    new { title = "Triggered", value = alertEvent.TriggeredAt.ToString("O") },
                                },
                            },
                            new Dictionary<string, object>
                            {
                                ["type"] = "TextBlock",
                                ["text"] = alertEvent.Message,
                                ["wrap"] = true,
                            },
                        },
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
