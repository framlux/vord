// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Alerts.Formatters;

namespace Framlux.FleetManagement.Test.Services.Alerts.Formatters;

/// <summary>
/// Tests for <see cref="PagerDutyPayloadFormatter"/>.
/// </summary>
public sealed class PagerDutyPayloadFormatterTests
{
    private readonly PagerDutyPayloadFormatter _formatter = new();

    private static AlertEvent CreateEvent(AlertSeverity severity = AlertSeverity.Critical, string? message = null)
    {
        return new AlertEvent
        {
            Id = 100,
            AlertRuleId = 5,
            TenantId = 1,
            MachineId = 42,
            Severity = severity,
            Message = message ?? "Server unresponsive",
            Details = """{"metric":"HealthCheck","currentValue":0}""",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.Parse("2026-05-10T16:00:00+00:00"),
        };
    }

    private static AlertRule CreateRule()
    {
        return new AlertRule
        {
            Id = 5,
            TenantId = 1,
            Name = "Server Down",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 0,
            Severity = AlertSeverity.Critical,
            IsEnabled = true,
            NotifyWebhook = true,
            IsCustom = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static IntegrationEndpoint CreateIntegration()
    {
        return new IntegrationEndpoint
        {
            Id = 1,
            TenantId = 1,
            Provider = IntegrationProvider.PagerDuty,
            Name = "PagerDuty Integration",
            Configuration = """{"routingKey":"R011234567890abcdef1234567890abcd"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Test]
    public async Task Provider_ReturnsPagerDuty()
    {
        await Assert.That(_formatter.Provider).IsEqualTo(IntegrationProvider.PagerDuty);
    }

    [Test]
    public async Task FormatRequest_TargetsPagerDutyEventsUrl()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());

        await Assert.That(request.RequestUri!.ToString()).IsEqualTo("https://events.pagerduty.com/v2/enqueue");
    }

    [Test]
    public async Task FormatRequest_RoutingKeyFromConfiguration()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        string routingKey = doc.RootElement.GetProperty("routing_key").GetString()!;

        await Assert.That(routingKey).IsEqualTo("R011234567890abcdef1234567890abcd");
    }

    [Test]
    public async Task FormatRequest_DedupKeyFormat()
    {
        AlertEvent alertEvent = CreateEvent();
        AlertRule rule = CreateRule();
        HttpRequestMessage request = _formatter.FormatRequest(alertEvent, rule, CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        string dedupKey = doc.RootElement.GetProperty("dedup_key").GetString()!;

        string expected = $"vord-alert-{rule.Id}-{alertEvent.MachineId}";

        await Assert.That(dedupKey).IsEqualTo(expected);
    }

    [Test]
    public async Task FormatRequest_CriticalSeverity_MapsToCritical()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(AlertSeverity.Critical), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        string severity = doc.RootElement.GetProperty("payload").GetProperty("severity").GetString()!;

        await Assert.That(severity).IsEqualTo("critical");
    }

    [Test]
    public async Task FormatRequest_WarningSeverity_MapsToWarning()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(AlertSeverity.Warning), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        string severity = doc.RootElement.GetProperty("payload").GetProperty("severity").GetString()!;

        await Assert.That(severity).IsEqualTo("warning");
    }

    [Test]
    public async Task FormatRequest_InfoSeverity_MapsToInfo()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(AlertSeverity.Info), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        string severity = doc.RootElement.GetProperty("payload").GetProperty("severity").GetString()!;

        await Assert.That(severity).IsEqualTo("info");
    }

    [Test]
    public async Task FormatRequest_LongMessage_TruncatedAt1024WithEllipsis()
    {
        string longMessage = new('A', 2000);
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(message: longMessage), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        string summary = doc.RootElement.GetProperty("payload").GetProperty("summary").GetString()!;

        await Assert.That(summary.Length).IsEqualTo(1024);
        await Assert.That(summary.EndsWith("...", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task FormatRequest_ShortMessage_NotTruncated()
    {
        string shortMessage = "Short alert message";
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(message: shortMessage), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        string summary = doc.RootElement.GetProperty("payload").GetProperty("summary").GetString()!;

        await Assert.That(summary).IsEqualTo(shortMessage);
    }

    [Test]
    public async Task FormatRequest_NullAlertEvent_ThrowsArgumentNullException()
    {
        await Assert.That(() => _formatter.FormatRequest(null!, CreateRule(), CreateIntegration())).ThrowsExactly<ArgumentNullException>();
    }

    [Test]
    public async Task FormatRequest_NullRule_ThrowsArgumentNullException()
    {
        await Assert.That(() => _formatter.FormatRequest(CreateEvent(), null!, CreateIntegration())).ThrowsExactly<ArgumentNullException>();
    }

    [Test]
    public async Task FormatRequest_NullIntegration_ThrowsArgumentNullException()
    {
        await Assert.That(() => _formatter.FormatRequest(CreateEvent(), CreateRule(), null!)).ThrowsExactly<ArgumentNullException>();
    }

    [Test]
    public async Task FormatRequest_MissingRoutingKey_ThrowsKeyNotFoundException()
    {
        IntegrationEndpoint integration = CreateIntegration();
        integration.Configuration = "{}";

        await Assert.That(() => _formatter.FormatRequest(CreateEvent(), CreateRule(), integration)).ThrowsExactly<KeyNotFoundException>();
    }

    [Test]
    public async Task FormatRequest_MessageExactly1024Chars_NotTruncated()
    {
        string exactMessage = new('B', 1024);
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(message: exactMessage), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        string summary = doc.RootElement.GetProperty("payload").GetProperty("summary").GetString()!;

        await Assert.That(summary.Length).IsEqualTo(1024);
        await Assert.That(summary).IsEqualTo(exactMessage);
    }

    [Test]
    public async Task FormatRequest_MessageAt1025Chars_IsTruncated()
    {
        string longMessage = new('C', 1025);
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(message: longMessage), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        string summary = doc.RootElement.GetProperty("payload").GetProperty("summary").GetString()!;

        await Assert.That(summary.Length).IsEqualTo(1024);
        await Assert.That(summary.EndsWith("...", StringComparison.Ordinal)).IsTrue();
    }
}
