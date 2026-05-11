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
/// Formats alert payloads as Discord embed messages for webhook delivery.
/// </summary>
public sealed class DiscordPayloadFormatter : IIntegrationPayloadFormatter
{
    private const int ColorBlue = 3447003;
    private const int ColorYellow = 16776960;
    private const int ColorRed = 15158332;

    /// <inheritdoc/>
    public IntegrationProvider Provider => IntegrationProvider.Discord;

    /// <inheritdoc/>
    public HttpRequestMessage FormatRequest(AlertEvent alertEvent, AlertRule rule, IntegrationEndpoint integration)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(integration);

        using JsonDocument config = JsonDocument.Parse(integration.Configuration);
        string webhookUrl = config.RootElement.GetProperty("webhookUrl").GetString()
            ?? throw new InvalidOperationException("Discord configuration missing webhookUrl");

        int color = alertEvent.Severity switch
        {
            AlertSeverity.Critical => ColorRed,
            AlertSeverity.Warning => ColorYellow,
            _ => ColorBlue,
        };

        object payload = new
        {
            embeds = new object[]
            {
                new
                {
                    title = rule.Name,
                    description = alertEvent.Message,
                    color,
                    fields = new object[]
                    {
                        new { name = "Severity", value = alertEvent.Severity.ToString(), inline = true },
                        new { name = "Machine", value = alertEvent.MachineId.ToString(), inline = true },
                    },
                    timestamp = alertEvent.TriggeredAt.ToString("O"),
                },
            },
        };

        string json = JsonSerializer.Serialize(payload, JsonDefaults.CamelCase);

        HttpRequestMessage request = new(HttpMethod.Post, webhookUrl);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        return request;
    }
}
