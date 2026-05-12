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
/// Formats alert payloads as PagerDuty Events API v2 requests with deduplication key support.
/// </summary>
public sealed class PagerDutyPayloadFormatter : IIntegrationPayloadFormatter
{
    private const string PagerDutyEventsUrl = "https://events.pagerduty.com/v2/enqueue";
    private const int MaxSummaryLength = 1024;

    /// <inheritdoc/>
    public IntegrationProvider Provider => IntegrationProvider.PagerDuty;

    /// <inheritdoc/>
    public HttpRequestMessage FormatRequest(AlertEvent alertEvent, AlertRule rule, IntegrationEndpoint integration)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(integration);

        using JsonDocument config = JsonDocument.Parse(integration.Configuration);
        string routingKey = config.RootElement.GetProperty("routingKey").GetString()
            ?? throw new InvalidOperationException("PagerDuty configuration missing routingKey");

        string severity = alertEvent.Severity switch
        {
            AlertSeverity.Critical => "critical",
            AlertSeverity.Warning => "warning",
            _ => "info",
        };

        string summary = alertEvent.Message;
        if (summary.Length > MaxSummaryLength)
        {
            summary = string.Concat(summary.AsSpan(0, MaxSummaryLength - 3), "...");
        }

        object payload = new
        {
            routing_key = routingKey,
            event_action = "trigger",
            dedup_key = $"vord-alert-{rule.Id}-{alertEvent.MachineId}",
            payload = new
            {
                summary,
                severity,
                source = $"machine-{alertEvent.MachineId}",
                component = "vord-fleet",
                custom_details = new
                {
                    rule_name = rule.Name,
                    machine_id = alertEvent.MachineId,
                    triggered_at = alertEvent.TriggeredAt.ToString("O"),
                },
            },
        };

        string json = JsonSerializer.Serialize(payload, JsonDefaults.SnakeCase);

        HttpRequestMessage request = new(HttpMethod.Post, PagerDutyEventsUrl);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        return request;
    }
}
