// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Alerts;
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
    private static IIntegrationPayloadFormatter CreateCustomFormatter()
    {
        IIntegrationPayloadFormatter customFormatter = Substitute.For<IIntegrationPayloadFormatter>();
        customFormatter.Provider.Returns(IntegrationProvider.Custom);
        customFormatter.FormatRequest(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<IntegrationEndpoint>())
            .Returns(callInfo =>
            {
                IntegrationEndpoint endpoint = callInfo.Arg<IntegrationEndpoint>();
                System.Text.Json.JsonDocument config = System.Text.Json.JsonDocument.Parse(endpoint.Configuration);
                string url = config.RootElement.GetProperty("url").GetString()!;

                HttpRequestMessage req = new(HttpMethod.Post, url);
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                return req;
            });

        return customFormatter;
    }

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
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, formatters, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(), CreateRule(notifyEmail: false, notifyWebhook: false), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeliverAsync_NotifyEmailOnly_NoHttpCalls()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();

        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, formatters, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(), CreateRule(notifyEmail: true, notifyWebhook: false), CancellationToken.None);

        // No HTTP calls for email-only (email delivery not yet implemented)
        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeliverAsync_NotifyWebhookWithEnabledIntegrations_MakesHttpCalls()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "Test Integration",
            Configuration = """{"url":"https://hooks.example.com/test","secret":"plaintext-secret"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, formatters, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        await Assert.That(handler.Requests[0].RequestUri!.ToString()).IsEqualTo("https://hooks.example.com/test");
        await Assert.That(handler.Requests[0].Method).IsEqualTo(HttpMethod.Post);
    }

    [Test]
    public async Task DeliverAsync_DisabledIntegration_Skipped()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "Disabled Integration",
            Configuration = """{"url":"https://hooks.example.com/disabled","secret":"plaintext-secret"}""",
            IsEnabled = false,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, formatters, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeliverAsync_DeletedIntegration_Skipped()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "Deleted Integration",
            Configuration = """{"url":"https://hooks.example.com/deleted","secret":"plaintext-secret"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            DeletedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, formatters, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeliverAsync_NoIntegrationsExist_CompletesWithoutError()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, formatters, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(), CreateRule(notifyWebhook: true), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeliverAsync_MultipleIntegrations_EachGetsHttpCall()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        for (int i = 0; i < 3; i++)
        {
            IntegrationEndpoint integration = new()
            {
                TenantId = tenantId,
                Provider = IntegrationProvider.Custom,
                Name = $"Integration {i}",
                Configuration = $$$"""{"url":"https://hooks.example.com/multi{{{i}}}","secret":"secret-{{{i}}}"}""",
                IsEnabled = true,
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await dbFactory.Context.InsertWithInt32IdentityAsync(integration);
        }

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, formatters, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(3);
    }

    [Test]
    public async Task DeliverAsync_HttpError_LogsWarningAndContinues()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "Error Integration",
            Configuration = """{"url":"https://hooks.example.com/error","secret":"plaintext-secret"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, formatters, new NullLogger<AlertDeliveryService>());

        // Should not throw
        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DeliverAsync_NetworkException_LogsErrorAndContinues()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "Network Error Integration",
            Configuration = """{"url":"https://hooks.example.com/netfail","secret":"plaintext-secret"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        handler.WithException(new HttpRequestException("Connection refused"));
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, formatters, new NullLogger<AlertDeliveryService>());

        // Should not throw
        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        // Request was attempted even though it threw
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DeliverAsync_MissingFormatterForProvider_LogsWarningAndSkips()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;

        // Seed a Slack integration but only register a Custom formatter
        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Slack,
            Name = "Slack Integration",
            Configuration = """{"webhookUrl":"https://hooks.slack.com/test"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        // Only register Custom formatter, not Slack
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        ILogger<AlertDeliveryService> logger = Substitute.For<ILogger<AlertDeliveryService>>();
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, formatters, logger);

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        // No HTTP call because the formatter was not found
        await Assert.That(handler.Requests.Count).IsEqualTo(0);
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

        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, formatters, new NullLogger<AlertDeliveryService>());

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

        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, formatters, new NullLogger<AlertDeliveryService>());

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

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "Both Flags Integration",
            Configuration = """{"url":"https://hooks.example.com/both","secret":"plaintext-secret"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        ILogger<AlertDeliveryService> logger = Substitute.For<ILogger<AlertDeliveryService>>();

        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, redis, formatters, logger);

        // Both notifyEmail and notifyWebhook enabled
        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyEmail: true, notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        // Integration should receive an HTTP POST
        await Assert.That(handler.Requests.Count).IsEqualTo(1);

        // Email path should log a message (placeholder implementation)
        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("email")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
