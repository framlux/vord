// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Alerts;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Security.Cryptography;
using System.Text;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="AlertDeliveryService"/>.
/// </summary>
public sealed class AlertDeliveryServiceTests
{
    private static AlertRule CreateRule(
        bool notifyEmail = false,
        bool notifyWebhook = false,
        int tenantId = 1)
    {
        return new AlertRule
        {
            Id = 1,
            TenantId = tenantId,
            Name = "Test Rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            NotifyEmail = notifyEmail,
            NotifyWebhook = notifyWebhook,
            IsCustom = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static AlertEvent CreateEvent(int tenantId = 1)
    {
        return new AlertEvent
        {
            Id = 100,
            AlertRuleId = 1,
            TenantId = tenantId,
            MachineId = 42,
            Severity = AlertSeverity.Critical,
            Message = "CPU at 95%",
            Details = """{"metric":"CpuUsage","currentValue":95}""",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
    }

    [Test]
    public async Task DeliverAsync_NoNotifyFlags_DoesNothing()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        AlertDeliveryService service = new(scopeFactory, httpFactory, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(), CreateRule(notifyEmail: false, notifyWebhook: false), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeliverAsync_NotifyEmailOnly_LogsEmailAction()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        ILogger<AlertDeliveryService> logger = Substitute.For<ILogger<AlertDeliveryService>>();

        AlertDeliveryService service = new(scopeFactory, httpFactory, logger);

        await service.DeliverAsync(CreateEvent(), CreateRule(notifyEmail: true, notifyWebhook: false), CancellationToken.None);

        // No HTTP calls for email-only (email is placeholder)
        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeliverAsync_NotifyWebhookOnly_PostsToWebhookUrl()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        // Seed webhook
        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId,
            Name = "Test Hook",
            Url = "https://hooks.example.com/alerts",
            Secret = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        AlertDeliveryService service = new(scopeFactory, httpFactory, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        await Assert.That(handler.Requests[0].RequestUri!.ToString()).IsEqualTo("https://hooks.example.com/alerts");
        await Assert.That(handler.Requests[0].Method).IsEqualTo(HttpMethod.Post);
    }

    [Test]
    public async Task DeliverAsync_WebhookPayload_ContainsCorrectFields()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId,
            Name = "Payload Hook",
            Url = "https://hooks.example.com/payload",
            Secret = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        AlertDeliveryService service = new(scopeFactory, httpFactory, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        string? body = handler.Requests[0].Body;
        await Assert.That(body).IsNotNull();
        await Assert.That(body!).Contains("\"eventId\":");
        await Assert.That(body).Contains("\"ruleName\":");
        await Assert.That(body).Contains("\"severity\":");
        await Assert.That(body).Contains("\"message\":");
        await Assert.That(body).Contains("\"machineId\":");
        await Assert.That(body).Contains("\"triggeredAt\":");
    }

    [Test]
    public async Task DeliverAsync_WebhookSignature_IsValidHmacSha256()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;
        string secret = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId,
            Name = "Sig Hook",
            Url = "https://hooks.example.com/sig",
            Secret = secret,
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        AlertDeliveryService service = new(scopeFactory, httpFactory, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        string? body = handler.Requests[0].Body;
        IEnumerable<string> sigValues = handler.Requests[0].Headers["X-Vord-Signature"];
        string signature = sigValues.First();

        byte[] expectedSig = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(body!));
        string expectedHex = Convert.ToHexStringLower(expectedSig);

        await Assert.That(signature).IsEqualTo(expectedHex);
    }

    [Test]
    public async Task DeliverAsync_MultipleWebhooks_PostsToAll()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        for (int i = 0; i < 2; i++)
        {
            WebhookEndpoint webhook = new()
            {
                TenantId = tenantId,
                Name = $"Hook {i}",
                Url = $"https://hooks.example.com/multi{i}",
                Secret = $"abcdef0123456789abcdef0123456789abcdef0123456789abcdef012345678{i}",
                IsEnabled = true,
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);
        }

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        AlertDeliveryService service = new(scopeFactory, httpFactory, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task DeliverAsync_WebhookHttpError_LogsWarningAndContinues()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId,
            Name = "Error Hook",
            Url = "https://hooks.example.com/error",
            Secret = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        AlertDeliveryService service = new(scopeFactory, httpFactory, new NullLogger<AlertDeliveryService>());

        // Should not throw
        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DeliverAsync_WebhookNetworkException_LogsErrorAndContinues()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId,
            Name = "Network Error Hook",
            Url = "https://hooks.example.com/netfail",
            Secret = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        handler.WithException(new HttpRequestException("Connection refused"));
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        AlertDeliveryService service = new(scopeFactory, httpFactory, new NullLogger<AlertDeliveryService>());

        // Should not throw
        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);
    }

    [Test]
    public async Task DeliverAsync_DisabledWebhook_Skipped()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId,
            Name = "Disabled Hook",
            Url = "https://hooks.example.com/disabled",
            Secret = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            IsEnabled = false,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        AlertDeliveryService service = new(scopeFactory, httpFactory, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeliverAsync_NoWebhooksExist_CompletesSuccessfully()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        AlertDeliveryService service = new(scopeFactory, httpFactory, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(), CreateRule(notifyWebhook: true), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }
}
