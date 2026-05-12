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
/// Tests for <see cref="DiscordPayloadFormatter"/>.
/// </summary>
public sealed class DiscordPayloadFormatterTests
{
    private readonly DiscordPayloadFormatter _formatter = new();

    private static AlertEvent CreateEvent(AlertSeverity severity = AlertSeverity.Warning)
    {
        return new AlertEvent
        {
            Id = 100,
            AlertRuleId = 1,
            TenantId = 1,
            MachineId = 42,
            Severity = severity,
            Message = "Disk usage exceeded threshold",
            Details = """{"metric":"DiskUsage","currentValue":90}""",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.Parse("2026-05-10T14:00:00+00:00"),
        };
    }

    private static AlertRule CreateRule()
    {
        return new AlertRule
        {
            Id = 1,
            TenantId = 1,
            Name = "High Disk Usage",
            Metric = AlertMetric.DiskUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 85,
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
            Provider = IntegrationProvider.Discord,
            Name = "Discord Integration",
            Configuration = """{"webhookUrl":"https://discord.com/api/webhooks/123456/abcdef"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Test]
    public async Task Provider_ReturnsDiscord()
    {
        await Assert.That(_formatter.Provider).IsEqualTo(IntegrationProvider.Discord);
    }

    [Test]
    public async Task FormatRequest_SetsCorrectUrlFromConfiguration()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());

        await Assert.That(request.RequestUri!.ToString()).IsEqualTo("https://discord.com/api/webhooks/123456/abcdef");
    }

    [Test]
    public async Task FormatRequest_CriticalSeverity_ColorIsRed()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(AlertSeverity.Critical), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement embeds = doc.RootElement.GetProperty("embeds");
        int color = embeds[0].GetProperty("color").GetInt32();

        await Assert.That(color).IsEqualTo(15158332);
    }

    [Test]
    public async Task FormatRequest_WarningSeverity_ColorIsYellow()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(AlertSeverity.Warning), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement embeds = doc.RootElement.GetProperty("embeds");
        int color = embeds[0].GetProperty("color").GetInt32();

        await Assert.That(color).IsEqualTo(16776960);
    }

    [Test]
    public async Task FormatRequest_InfoSeverity_ColorIsBlue()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(AlertSeverity.Info), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement embeds = doc.RootElement.GetProperty("embeds");
        int color = embeds[0].GetProperty("color").GetInt32();

        await Assert.That(color).IsEqualTo(3447003);
    }

    [Test]
    public async Task FormatRequest_PayloadHasEmbedsArrayWithTitleDescriptionFields()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement embeds = doc.RootElement.GetProperty("embeds");

        await Assert.That(embeds.GetArrayLength()).IsEqualTo(1);

        JsonElement embed = embeds[0];
        string title = embed.GetProperty("title").GetString()!;
        string description = embed.GetProperty("description").GetString()!;
        JsonElement fields = embed.GetProperty("fields");

        await Assert.That(title).IsEqualTo("High Disk Usage");
        await Assert.That(description).IsEqualTo("Disk usage exceeded threshold");
        await Assert.That(fields.GetArrayLength()).IsGreaterThan(0);
    }

    [Test]
    public async Task FormatRequest_TimestampIsIso8601()
    {
        AlertEvent alertEvent = CreateEvent();
        HttpRequestMessage request = _formatter.FormatRequest(alertEvent, CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement embed = doc.RootElement.GetProperty("embeds")[0];
        string timestamp = embed.GetProperty("timestamp").GetString()!;

        bool parsed = DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset _);

        await Assert.That(parsed).IsTrue();
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
