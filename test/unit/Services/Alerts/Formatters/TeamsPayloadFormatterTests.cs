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
/// Tests for <see cref="TeamsPayloadFormatter"/>.
/// </summary>
public sealed class TeamsPayloadFormatterTests
{
    private readonly TeamsPayloadFormatter _formatter = new();

    private static AlertEvent CreateEvent(AlertSeverity severity = AlertSeverity.Warning)
    {
        return new AlertEvent
        {
            Id = 100,
            AlertRuleId = 1,
            TenantId = 1,
            MachineId = 42,
            Severity = severity,
            Message = "Memory usage at 92%",
            Details = """{"metric":"MemoryUsage","currentValue":92}""",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.Parse("2026-05-10T18:00:00+00:00"),
        };
    }

    private static AlertRule CreateRule()
    {
        return new AlertRule
        {
            Id = 1,
            TenantId = 1,
            Name = "High Memory Alert",
            Metric = AlertMetric.MemoryUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 90,
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
            Provider = IntegrationProvider.MicrosoftTeams,
            Name = "Teams Integration",
            Configuration = """{"webhookUrl":"https://outlook.office.com/webhook/abc123"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Test]
    public async Task Provider_ReturnsMicrosoftTeams()
    {
        await Assert.That(_formatter.Provider).IsEqualTo(IntegrationProvider.MicrosoftTeams);
    }

    [Test]
    public async Task FormatRequest_SetsCorrectUrlFromConfiguration()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());

        await Assert.That(request.RequestUri!.ToString()).IsEqualTo("https://outlook.office.com/webhook/abc123");
    }

    [Test]
    public async Task FormatRequest_PayloadHasAdaptiveCardStructure()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // Top-level message type
        await Assert.That(root.GetProperty("type").GetString()).IsEqualTo("message");

        // Has attachments array
        JsonElement attachments = root.GetProperty("attachments");
        await Assert.That(attachments.GetArrayLength()).IsGreaterThan(0);

        // First attachment has AdaptiveCard content type
        JsonElement firstAttachment = attachments[0];
        string contentType = firstAttachment.GetProperty("contentType").GetString()!;

        await Assert.That(contentType).IsEqualTo("application/vnd.microsoft.card.adaptive");
    }

    [Test]
    public async Task FormatRequest_ContentTypeIncludesAdaptiveCardMimeType()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();

        await Assert.That(body.Contains("application/vnd.microsoft.card.adaptive")).IsTrue();
    }

    [Test]
    public async Task FormatRequest_CriticalSeverity_MapsToAttentionColor()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(AlertSeverity.Critical), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();

        await Assert.That(body.Contains("attention")).IsTrue();
    }

    [Test]
    public async Task FormatRequest_WarningSeverity_MapsToWarningColor()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(AlertSeverity.Warning), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();

        await Assert.That(body.Contains("warning")).IsTrue();
    }

    [Test]
    public async Task FormatRequest_InfoSeverity_MapsToDefaultColor()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(AlertSeverity.Info), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement content = doc.RootElement.GetProperty("attachments")[0].GetProperty("content");
        JsonElement bodyElements = content.GetProperty("body");

        // Find the TextBlock with the color property
        bool foundDefault = false;
        foreach (JsonElement element in bodyElements.EnumerateArray())
        {
            if (element.TryGetProperty("color", out JsonElement colorElement))
            {
                if (colorElement.GetString() == "default")
                {
                    foundDefault = true;

                    break;
                }
            }
        }

        await Assert.That(foundDefault).IsTrue();
    }

    [Test]
    public async Task FormatRequest_AlertMessageIncludedInBody()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();

        await Assert.That(body.Contains("Memory usage at 92%")).IsTrue();
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

    [Test]
    public async Task FormatRequest_PayloadContainsDollarSchemaKey()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());
        string? body = await request.Content!.ReadAsStringAsync();

        JsonDocument doc = JsonDocument.Parse(body!);
        JsonElement content = doc.RootElement.GetProperty("attachments")[0].GetProperty("content");

        // Verify $schema key exists with the correct Adaptive Card schema URL
        await Assert.That(content.TryGetProperty("$schema", out JsonElement schemaValue)).IsTrue();
        await Assert.That(schemaValue.GetString()).IsEqualTo("http://adaptivecards.io/schemas/adaptive-card.json");

        // Verify plain "schema" key (without $) does NOT exist at this level
        await Assert.That(content.TryGetProperty("schema", out _)).IsFalse();
    }
}
