// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Alerts;
using Framlux.FleetManagement.Server.Services.Alerts.Formatters;

namespace Framlux.FleetManagement.Test.Services.Alerts.Formatters;

/// <summary>
/// Tests for <see cref="SlackPayloadFormatter"/>.
/// </summary>
public sealed class SlackPayloadFormatterTests
{
    private readonly SlackPayloadFormatter _formatter = new();

    private static AlertEvent CreateEvent(AlertSeverity severity = AlertSeverity.Warning)
    {
        return new AlertEvent
        {
            Id = 100,
            AlertRuleId = 1,
            TenantId = 1,
            MachineId = 42,
            Severity = severity,
            Message = "CPU usage exceeded threshold",
            Details = """{"metric":"CpuUsage","currentValue":95}""",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.Parse("2026-05-10T12:30:00+00:00"),
        };
    }

    private static AlertRule CreateRule()
    {
        return new AlertRule
        {
            Id = 1,
            TenantId = 1,
            Name = "High CPU Alert",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
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
            Provider = IntegrationProvider.Slack,
            Name = "Slack Integration",
            Configuration = """{"webhookUrl":"https://hooks.slack.com/services/T123/B456/abc"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Test]
    public async Task Provider_ReturnsSlack()
    {
        await Assert.That(_formatter.Provider).IsEqualTo(IntegrationProvider.Slack);
    }

    [Test]
    public async Task FormatRequest_SetsCorrectUrlFromConfiguration()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());

        await Assert.That(request.RequestUri!.ToString()).IsEqualTo("https://hooks.slack.com/services/T123/B456/abc");
    }

    [Test]
    public async Task FormatRequest_ProducesJsonWithBlocksArray()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);

        bool hasBlocks = doc.RootElement.TryGetProperty("blocks", out JsonElement blocks);

        await Assert.That(hasBlocks).IsTrue();
        await Assert.That(blocks.GetArrayLength()).IsGreaterThan(0);
    }

    [Test]
    public async Task FormatRequest_CriticalSeverity_MapsToRotatingLightEmoji()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(AlertSeverity.Critical), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();

        await Assert.That(body.Contains(":rotating_light:")).IsTrue();
    }

    [Test]
    public async Task FormatRequest_WarningSeverity_MapsToWarningEmoji()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(AlertSeverity.Warning), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();

        await Assert.That(body.Contains(":warning:")).IsTrue();
    }

    [Test]
    public async Task FormatRequest_InfoSeverity_MapsToInformationSourceEmoji()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(AlertSeverity.Info), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();

        await Assert.That(body.Contains(":information_source:")).IsTrue();
    }

    [Test]
    public async Task FormatRequest_AlertMessageIsIncludedInPayload()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();

        await Assert.That(body.Contains("CPU usage exceeded threshold")).IsTrue();
    }

    [Test]
    public async Task FormatRequest_TriggeredAtTimestampIsIso8601()
    {
        AlertEvent alertEvent = CreateEvent();
        HttpRequestMessage request = _formatter.FormatRequest(alertEvent, CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();

        // The TriggeredAt is formatted with O (round-trip) which is ISO 8601
        string expectedTimestamp = alertEvent.TriggeredAt.ToString("O");

        await Assert.That(body.Contains(expectedTimestamp)).IsTrue();
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
    public async Task FormatRequest_MissingWebhookUrl_ThrowsKeyNotFoundException()
    {
        IntegrationEndpoint integration = CreateIntegration();
        integration.Configuration = "{}";

        await Assert.That(() => _formatter.FormatRequest(CreateEvent(), CreateRule(), integration)).ThrowsExactly<KeyNotFoundException>();
    }
}
