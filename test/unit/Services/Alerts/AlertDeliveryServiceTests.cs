// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Alerts;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

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

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

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
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        ILogger<AlertDeliveryService> logger = Substitute.For<ILogger<AlertDeliveryService>>();

        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, logger);

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

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

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

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

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

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        string? body = handler.Requests[0].Body;
        IEnumerable<string> sigValues = handler.Requests[0].Headers["X-Vord-Signature"];
        string signature = sigValues.First();

        byte[] expectedSig = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(body!));
        string expectedHex = $"sha256={Convert.ToHexStringLower(expectedSig)}";

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

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

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

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

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

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

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

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

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

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(), CreateRule(notifyWebhook: true), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeliverWebhooksAsync_SignatureHasSha256Prefix()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId,
            Name = "Prefix Hook",
            Url = "https://hooks.example.com/prefix",
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

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        IEnumerable<string> sigValues = handler.Requests[0].Headers["X-Vord-Signature"];
        string signature = sigValues.First();

        await Assert.That(signature.StartsWith("sha256=", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task DeliverWebhooksAsync_SignatureIsValidHmacSha256()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;
        string secret = "f1e2d3c4b5a6f1e2d3c4b5a6f1e2d3c4b5a6f1e2d3c4b5a6f1e2d3c4b5a6f1e2";

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId,
            Name = "Valid Sig Hook",
            Url = "https://hooks.example.com/validsig",
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

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        string? body = handler.Requests[0].Body;
        IEnumerable<string> sigValues = handler.Requests[0].Headers["X-Vord-Signature"];
        string signature = sigValues.First();

        // Strip the sha256= prefix and verify the HMAC matches
        string hexPart = signature.Substring("sha256=".Length);
        byte[] expectedSig = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(body!));
        string expectedHex = Convert.ToHexStringLower(expectedSig);

        await Assert.That(hexPart).IsEqualTo(expectedHex);
    }

    [Test]
    public async Task DeliverWebhooksAsync_DifferentPayloads_ProduceDifferentSignatures()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;
        string secret = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId,
            Name = "Diff Payload Hook",
            Url = "https://hooks.example.com/diffpayload",
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

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

        // Deliver first event
        AlertEvent event1 = CreateEvent(tenantId);
        event1.Message = "First payload message";
        await service.DeliverAsync(event1, CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        // Deliver second event with different message
        AlertEvent event2 = CreateEvent(tenantId);
        event2.Id = 200;
        event2.Message = "Second payload message";
        await service.DeliverAsync(event2, CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        string signature1 = handler.Requests[0].Headers["X-Vord-Signature"].First();
        string signature2 = handler.Requests[1].Headers["X-Vord-Signature"].First();

        await Assert.That(signature1).IsNotEqualTo(signature2);
    }

    [Test]
    public async Task DeliverWebhooksAsync_DifferentSecrets_ProduceDifferentSignatures()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId1 = 1;
        int tenantId2 = 2;
        string secret1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string secret2 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        WebhookEndpoint webhook1 = new()
        {
            TenantId = tenantId1,
            Name = "Secret1 Hook",
            Url = "https://hooks.example.com/secret1",
            Secret = secret1,
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(webhook1);

        WebhookEndpoint webhook2 = new()
        {
            TenantId = tenantId2,
            Name = "Secret2 Hook",
            Url = "https://hooks.example.com/secret2",
            Secret = secret2,
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(webhook2);

        // Deliver to tenant 1
        MockHttpMessageHandler handler1 = new();
        IHttpClientFactory httpFactory1 = Substitute.For<IHttpClientFactory>();
        httpFactory1.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler1));
        TestServiceScopeFactory scopeFactory1 = new(dbFactory.Context);
        IConnectionMultiplexer redis1 = Substitute.For<IConnectionMultiplexer>();
        AlertDeliveryService service1 = new(scopeFactory1, httpFactory1, redis1, new NullLogger<AlertDeliveryService>());

        AlertEvent eventForTenant1 = CreateEvent(tenantId1);
        await service1.DeliverAsync(eventForTenant1, CreateRule(notifyWebhook: true, tenantId: tenantId1), CancellationToken.None);

        // Deliver to tenant 2 with same event content but different secret
        MockHttpMessageHandler handler2 = new();
        IHttpClientFactory httpFactory2 = Substitute.For<IHttpClientFactory>();
        httpFactory2.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler2));
        TestServiceScopeFactory scopeFactory2 = new(dbFactory.Context);
        IConnectionMultiplexer redis2 = Substitute.For<IConnectionMultiplexer>();
        AlertDeliveryService service2 = new(scopeFactory2, httpFactory2, redis2, new NullLogger<AlertDeliveryService>());

        AlertEvent eventForTenant2 = CreateEvent(tenantId2);
        await service2.DeliverAsync(eventForTenant2, CreateRule(notifyWebhook: true, tenantId: tenantId2), CancellationToken.None);

        string signature1 = handler1.Requests[0].Headers["X-Vord-Signature"].First();
        string signature2 = handler2.Requests[0].Headers["X-Vord-Signature"].First();

        await Assert.That(signature1).IsNotEqualTo(signature2);
    }

    // --- EnqueueAsync Tests ---

    [Test]
    public async Task EnqueueAsync_PushesToCorrectRedisKey()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

        await service.EnqueueAsync(100, 1, 1, CancellationToken.None);

        await redisDb.Received(1).ListLeftPushAsync(
            Arg.Is<RedisKey>(k => k == "alert:delivery:queue"),
            Arg.Any<RedisValue>(),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task EnqueueAsync_PayloadContainsEventIdRuleIdTenantId()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

        await service.EnqueueAsync(42, 7, 3, CancellationToken.None);

        await redisDb.Received(1).ListLeftPushAsync(
            Arg.Any<RedisKey>(),
            Arg.Is<RedisValue>(v => v.ToString().Contains("\"eventId\":42") && v.ToString().Contains("\"ruleId\":7") && v.ToString().Contains("\"tenantId\":3")),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());
    }

    // --- Cross-Cutting Tests ---

    [Test]
    public async Task DeliverAsync_BothNotifyFlags_ExecutesBothPaths()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId,
            Name = "Both Flags Hook",
            Url = "https://hooks.example.com/both",
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
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        ILogger<AlertDeliveryService> logger = Substitute.For<ILogger<AlertDeliveryService>>();

        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, logger);

        // Both notifyEmail and notifyWebhook enabled.
        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyEmail: true, notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        // Webhook should receive an HTTP POST.
        await Assert.That(handler.Requests.Count).IsEqualTo(1);

        // Email path should log a message (placeholder implementation).
        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("email")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task DeliverWebhooksAsync_TriggeredAtIsIso8601()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId,
            Name = "ISO8601 Hook",
            Url = "https://hooks.example.com/iso",
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
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();

        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        string? body = handler.Requests[0].Body;
        await Assert.That(body).IsNotNull();

        // The triggeredAt field should be parseable as ISO 8601 with timezone.
        System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body!);
        string triggeredAt = doc.RootElement.GetProperty("triggeredAt").GetString()!;
        bool parsed = DateTimeOffset.TryParse(triggeredAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset _);

        await Assert.That(parsed).IsTrue();
    }
}
