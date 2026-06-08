// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Test.Infrastructure;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

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
        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();

        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

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

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

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

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

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

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

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

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

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

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(3);
    }

    [Test]
    public async Task DeliverAsync_5xxResponse_ThrowsSoHangfireRetries()
    {
        // Intent: 5xx responses are transient (server crash, gateway timeout). The job MUST throw
        // so Hangfire's [AutomaticRetry] kicks in. Pre-Hangfire the service swallowed all errors,
        // which caused silent alert loss on transient receiver outages.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;
        AlertEvent alertEvent = CreateEvent(tenantId);
        await dbFactory.Context.InsertAsync(alertEvent);

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "5xx Integration",
            Configuration = """{"url":"https://hooks.example.com/5xx","secret":"plaintext-secret"}""",
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

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

        await Assert.ThrowsAsync<IntegrationDeliveryException>(() =>
            service.DeliverAsync(alertEvent, CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None));

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DeliverAsync_NetworkException_ThrowsSoHangfireRetries()
    {
        // Intent: a transport-level exception (connection refused, DNS, TLS) is the canonical
        // transient failure that Hangfire's retry was designed for. Like 5xx, propagate it.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;
        AlertEvent alertEvent = CreateEvent(tenantId);
        await dbFactory.Context.InsertAsync(alertEvent);

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

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

        await Assert.ThrowsAsync<IntegrationDeliveryException>(() =>
            service.DeliverAsync(alertEvent, CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None));

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DeliverAsync_4xxResponse_DoesNotThrow()
    {
        // Intent: a 4xx response (auth failure, bad request, invalid URL) is a permanent failure.
        // Retrying gains nothing and would waste Hangfire's retry budget. Log, don't throw.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;
        AlertEvent alertEvent = CreateEvent(tenantId);
        await dbFactory.Context.InsertAsync(alertEvent);

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "4xx Integration",
            Configuration = """{"url":"https://hooks.example.com/4xx","secret":"plaintext-secret"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

        // 4xx must not throw.
        await service.DeliverAsync(alertEvent, CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DeliverAsync_PermanentFailure_LeavesClaimPendingNoRetry()
    {
        // Intent: a 4xx response is permanent — the Pending claim must REMAIN so future Hangfire
        // retries treat the integration as already attempted and skip the HTTP call. Without
        // this, retries would re-POST to the same broken receiver until the retry budget runs
        // out, generating log spam and wasted load.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;
        AlertEvent alertEvent = CreateEvent(tenantId);
        await dbFactory.Context.InsertAsync(alertEvent);

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "Permanent Fail Integration",
            Configuration = """{"url":"https://hooks.example.com/auth","secret":"plaintext-secret"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        int integrationId = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(alertEvent, CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        IntegrationDeliveryAttempt? row = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == alertEvent.Id) && (a.IntegrationEndpointId == integrationId))
            .FirstOrDefaultAsync();
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Status).IsEqualTo(IntegrationDeliveryAttemptStatus.Pending);
        await Assert.That(row.SucceededAt).IsNull();

        // Simulate the Hangfire retry: invoke DeliverAsync again. The HTTP handler counter must
        // not advance because the Pending claim suppresses the re-attempt.
        await service.DeliverAsync(alertEvent, CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DeliverAsync_ClaimThenSucceed_RecordsSucceededExactlyOnce()
    {
        // Intent: end-to-end happy path — pre-send claim + 2xx must produce exactly one
        // Succeeded row, no duplicates. A naive design that wrote the row only after the POST
        // could insert two rows under retry; the claim-then-send pattern with the unique index
        // guarantees one row.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;
        AlertEvent alertEvent = CreateEvent(tenantId);
        await dbFactory.Context.InsertAsync(alertEvent);

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "OK Integration",
            Configuration = """{"url":"https://hooks.example.com/ok","secret":"plaintext-secret"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        int integrationId = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(alertEvent, CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        int count = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == alertEvent.Id) && (a.IntegrationEndpointId == integrationId))
            .CountAsync();
        await Assert.That(count).IsEqualTo(1);

        IntegrationDeliveryAttempt row = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == alertEvent.Id) && (a.IntegrationEndpointId == integrationId))
            .FirstAsync();
        await Assert.That(row.Status).IsEqualTo(IntegrationDeliveryAttemptStatus.Succeeded);
    }

    [Test]
    public async Task DeliverAsync_TransportError_ReleasesClaimAndThrows()
    {
        // Intent: a transport-level failure (connection refused) must release the Pending claim
        // so a Hangfire retry can re-attempt — AND the job must throw so Hangfire retries are
        // actually triggered. Without release, retries would skip the integration as
        // "already claimed" even though no notification was delivered.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;
        AlertEvent alertEvent = CreateEvent(tenantId);
        await dbFactory.Context.InsertAsync(alertEvent);

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "Transport Fail Integration",
            Configuration = """{"url":"https://hooks.example.com/netfail","secret":"plaintext-secret"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        int integrationId = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        handler.WithException(new HttpRequestException("Connection refused"));
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

        await Assert.ThrowsAsync<IntegrationDeliveryException>(() =>
            service.DeliverAsync(alertEvent, CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None));

        // Claim was released — no row remains so the Hangfire retry can re-claim.
        int count = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == alertEvent.Id) && (a.IntegrationEndpointId == integrationId))
            .CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task DeliverAsync_ClaimAlreadyHeld_SkipsHttpCallEntirely()
    {
        // Intent: when a Pending claim already exists from a prior worker (mid-flight POST or a
        // permanent failure), the current invocation must not even attempt the HTTP POST. This
        // is the safety property of the two-state design — the pre-check sees the Pending row
        // and skips before the formatter is invoked or any network call is made.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;
        AlertEvent alertEvent = CreateEvent(tenantId);
        await dbFactory.Context.InsertAsync(alertEvent);

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "Pre-Claimed Integration",
            Configuration = """{"url":"https://hooks.example.com/held","secret":"plaintext-secret"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        int integrationId = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        // Pre-populate a Pending claim — simulates a prior worker that completed the POST but
        // crashed before marking success, OR a prior 4xx that left a Pending row in place.
        await dbFactory.Context.InsertAsync(new IntegrationDeliveryAttempt
        {
            AlertEventId = alertEvent.Id,
            IntegrationEndpointId = integrationId,
            Status = IntegrationDeliveryAttemptStatus.Pending,
            AttemptedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
            SucceededAt = null,
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(alertEvent, CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeliverAsync_SuccessfulDelivery_RecordsAttempt()
    {
        // Intent: every 2xx delivery transitions the pre-send Pending claim to Succeeded so the
        // next Hangfire retry of this job skips this integration. Without this, retries of a job
        // that had a 5xx on a *different* integration would re-POST to receivers that already
        // got their notification — duplicate PagerDuty pages, etc.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;
        AlertEvent alertEvent = CreateEvent(tenantId);
        await dbFactory.Context.InsertAsync(alertEvent);

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "OK Integration",
            Configuration = """{"url":"https://hooks.example.com/ok","secret":"plaintext-secret"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        int integrationId = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(alertEvent, CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        IntegrationDeliveryAttempt? row = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == alertEvent.Id) && (a.IntegrationEndpointId == integrationId))
            .FirstOrDefaultAsync();
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Status).IsEqualTo(IntegrationDeliveryAttemptStatus.Succeeded);
        await Assert.That(row.SucceededAt).IsNotNull();
    }

    [Test]
    public async Task DeliverAsync_AlreadyDelivered_SkipsHttpCall()
    {
        // Intent: when a row already exists in IntegrationDeliveryAttempts for this
        // (event, integration) pair, the delivery service must NOT POST again. Idempotency
        // across Hangfire retries depends on this.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;
        AlertEvent alertEvent = CreateEvent(tenantId);
        await dbFactory.Context.InsertAsync(alertEvent);

        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "Pre-Delivered Integration",
            Configuration = """{"url":"https://hooks.example.com/dup","secret":"plaintext-secret"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        int integrationId = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        // Pre-populate a Succeeded attempt — simulates a prior successful retry.
        await dbFactory.Context.InsertAsync(new Framlux.FleetManagement.Database.Models.IntegrationDeliveryAttempt
        {
            AlertEventId = alertEvent.Id,
            IntegrationEndpointId = integrationId,
            Status = IntegrationDeliveryAttemptStatus.Succeeded,
            AttemptedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            SucceededAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

        await service.DeliverAsync(alertEvent, CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        // No HTTP call — the integration was skipped by the idempotency check.
        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeliverAsync_MultipleIntegrations_OneFails_StillRecordsSuccessForOthersAndThrows()
    {
        // Intent: when one integration 5xx's and another succeeds, the job must (a) record the
        // success so a retry doesn't re-deliver to the OK one and (b) throw so Hangfire retries
        // for the failing one. Without (a) we get duplicate deliveries; without (b) the failure
        // is silently swallowed.
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;
        AlertEvent alertEvent = CreateEvent(tenantId);
        await dbFactory.Context.InsertAsync(alertEvent);

        IntegrationEndpoint okIntegration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "OK",
            Configuration = """{"url":"https://hooks.example.com/ok","secret":"s"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        int okId = await dbFactory.Context.InsertWithInt32IdentityAsync(okIntegration);

        IntegrationEndpoint failIntegration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = "FAIL",
            Configuration = """{"url":"https://hooks.example.com/fail","secret":"s"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        int failId = await dbFactory.Context.InsertWithInt32IdentityAsync(failIntegration);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MockHttpMessageHandler handler = new();
        handler.WithResponse("https://hooks.example.com/ok", new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        handler.WithResponse("https://hooks.example.com/fail", new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway));
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

        await Assert.ThrowsAsync<IntegrationDeliveryException>(() =>
            service.DeliverAsync(alertEvent, CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None));

        // Both integrations were attempted (so we don't lose alerts during a partial outage).
        await Assert.That(handler.Requests.Count).IsEqualTo(2);

        // Success recorded only for the OK integration. The failing integration's claim was
        // released so a Hangfire retry can re-attempt — the row must be absent.
        IntegrationDeliveryAttempt? okRow = await dbFactory.Context.IntegrationDeliveryAttempts
            .Where(a => (a.AlertEventId == alertEvent.Id) && (a.IntegrationEndpointId == okId))
            .FirstOrDefaultAsync();
        bool failRecorded = await dbFactory.Context.IntegrationDeliveryAttempts
            .AnyAsync(a => (a.AlertEventId == alertEvent.Id) && (a.IntegrationEndpointId == failId));
        await Assert.That(okRow).IsNotNull();
        await Assert.That(okRow!.Status).IsEqualTo(IntegrationDeliveryAttemptStatus.Succeeded);
        await Assert.That(failRecorded).IsFalse();
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

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        // Only register Custom formatter, not Slack
        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        ILogger<AlertDeliveryService> logger = Substitute.For<ILogger<AlertDeliveryService>>();
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, logger);

        await service.DeliverAsync(CreateEvent(tenantId), CreateRule(notifyWebhook: true, tenantId: tenantId), CancellationToken.None);

        // No HTTP call because the formatter was not found
        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    // --- EnqueueAsync Tests ---

    [Test]
    public async Task EnqueueAsync_EnqueuesIntegrationDeliveryJobViaHangfire()
    {
        // Intent: enqueue must dispatch via Hangfire's IBackgroundJobClient, not Redis. The
        // substitute records the Create(Job, IState) call that Enqueue<T> delegates to.
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();

        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

        await service.EnqueueAsync(100, 1, 1, CancellationToken.None);

        backgroundJobClient.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(IntegrationDeliveryJob) && j.Method.Name == nameof(IntegrationDeliveryJob.DeliverAsync)),
            Arg.Any<EnqueuedState>());
    }

    [Test]
    public async Task EnqueueAsync_PassesEventIdRuleIdTenantIdThrough()
    {
        // Intent: the args captured in the Hangfire Job expression must carry exactly the ids the
        // caller passed in. A mismatch would silently deliver alerts against the wrong rule/tenant.
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        IHttpClientFactory httpFactory = Substitute.For<IHttpClientFactory>();
        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();

        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, new NullLogger<AlertDeliveryService>());

        await service.EnqueueAsync(eventId: 42, ruleId: 7, tenantId: 3, CancellationToken.None);

        backgroundJobClient.Received(1).Create(
            Arg.Is<Job>(j =>
                j.Type == typeof(IntegrationDeliveryJob)
                && (long)j.Args[0]! == 42L
                && (int)j.Args[1]! == 7
                && (int)j.Args[2]! == 3),
            Arg.Any<EnqueuedState>());
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
        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        ILogger<AlertDeliveryService> logger = Substitute.For<ILogger<AlertDeliveryService>>();

        IIntegrationPayloadFormatter[] formatters = [CreateCustomFormatter()];
        AlertDeliveryService service = new(scopeFactory, httpFactory, backgroundJobClient, formatters, logger);

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
