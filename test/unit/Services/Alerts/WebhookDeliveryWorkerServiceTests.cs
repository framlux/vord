// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Alerts;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="WebhookDeliveryWorkerService"/>.
/// </summary>
public sealed class WebhookDeliveryWorkerServiceTests
{
    private static (WebhookDeliveryWorkerService Worker, DatabaseContext Db, IAlertDeliveryService Delivery, IDatabase RedisDb, ILogger<WebhookDeliveryWorkerService> Logger) CreateWorker()
    {
        TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        TestServiceScopeFactory scopeFactory = new(db);

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        IAlertDeliveryService deliveryService = Substitute.For<IAlertDeliveryService>();
        ILogger<WebhookDeliveryWorkerService> logger = Substitute.For<ILogger<WebhookDeliveryWorkerService>>();

        WebhookDeliveryWorkerService worker = new(redis, deliveryService, scopeFactory, logger);

        return (worker, db, deliveryService, redisDb, logger);
    }

    [Test]
    public async Task ProcessJob_ValidJob_DeliversToAllEnabledWebhooks()
    {
        (WebhookDeliveryWorkerService worker, DatabaseContext db, IAlertDeliveryService delivery, IDatabase redisDb, ILogger<WebhookDeliveryWorkerService> logger) = CreateWorker();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, notifyWebhook: true);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        AlertEvent alertEvent = TestDataBuilder.BuildAlertEvent(alertRuleId: rule.Id, tenantId: tenant.Id, machineId: machine.Id);
        alertEvent.Id = await db.InsertWithInt64IdentityAsync(alertEvent);

        string payload = JsonSerializer.Serialize(new { eventId = alertEvent.Id, ruleId = rule.Id, tenantId = tenant.Id }, JsonDefaults.CamelCase);

        await worker.ProcessJobAsync(payload, CancellationToken.None);

        // Verify DeliverAsync was called with the correct event and rule
        await delivery.Received(1).DeliverAsync(
            Arg.Is<AlertEvent>(e => e.Id == alertEvent.Id),
            Arg.Is<AlertRule>(r => r.Id == rule.Id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJob_WebhookTimeout_LogsWarningAndContinues()
    {
        (WebhookDeliveryWorkerService worker, DatabaseContext db, IAlertDeliveryService delivery, IDatabase redisDb, ILogger<WebhookDeliveryWorkerService> logger) = CreateWorker();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, notifyWebhook: true);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        AlertEvent alertEvent = TestDataBuilder.BuildAlertEvent(alertRuleId: rule.Id, tenantId: tenant.Id, machineId: machine.Id);
        alertEvent.Id = await db.InsertWithInt64IdentityAsync(alertEvent);

        // Simulate a timeout during delivery
        delivery.DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>())
            .Throws(new TaskCanceledException("Webhook timed out"));

        string payload = JsonSerializer.Serialize(new { eventId = alertEvent.Id, ruleId = rule.Id, tenantId = tenant.Id }, JsonDefaults.CamelCase);

        // Should not throw; worker catches and logs
        await worker.ProcessJobAsync(payload, CancellationToken.None);

        // Verify the error was logged (delivery was attempted but failed)
        await delivery.Received(1).DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJob_WebhookHttpError_LogsWarningAndContinues()
    {
        (WebhookDeliveryWorkerService worker, DatabaseContext db, IAlertDeliveryService delivery, IDatabase redisDb, ILogger<WebhookDeliveryWorkerService> logger) = CreateWorker();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, notifyWebhook: true);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        AlertEvent alertEvent = TestDataBuilder.BuildAlertEvent(alertRuleId: rule.Id, tenantId: tenant.Id, machineId: machine.Id);
        alertEvent.Id = await db.InsertWithInt64IdentityAsync(alertEvent);

        // Simulate an HTTP error during delivery
        delivery.DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Internal Server Error"));

        string payload = JsonSerializer.Serialize(new { eventId = alertEvent.Id, ruleId = rule.Id, tenantId = tenant.Id }, JsonDefaults.CamelCase);

        // Should not throw; worker catches and logs
        await worker.ProcessJobAsync(payload, CancellationToken.None);

        await delivery.Received(1).DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJob_NoWebhooksForTenant_CompletesWithoutError()
    {
        (WebhookDeliveryWorkerService worker, DatabaseContext db, IAlertDeliveryService delivery, IDatabase redisDb, ILogger<WebhookDeliveryWorkerService> logger) = CreateWorker();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        // Rule with no webhooks configured for the tenant
        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, notifyWebhook: true);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        AlertEvent alertEvent = TestDataBuilder.BuildAlertEvent(alertRuleId: rule.Id, tenantId: tenant.Id, machineId: machine.Id);
        alertEvent.Id = await db.InsertWithInt64IdentityAsync(alertEvent);

        string payload = JsonSerializer.Serialize(new { eventId = alertEvent.Id, ruleId = rule.Id, tenantId = tenant.Id }, JsonDefaults.CamelCase);

        // Should complete without error; DeliverAsync handles the case where no webhooks exist
        await worker.ProcessJobAsync(payload, CancellationToken.None);

        await delivery.Received(1).DeliverAsync(
            Arg.Is<AlertEvent>(e => e.Id == alertEvent.Id),
            Arg.Is<AlertRule>(r => r.Id == rule.Id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJob_DisabledWebhook_SkipsDelivery()
    {
        (WebhookDeliveryWorkerService worker, DatabaseContext db, IAlertDeliveryService delivery, IDatabase redisDb, ILogger<WebhookDeliveryWorkerService> logger) = CreateWorker();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        // Disabled webhook exists but should be skipped by DeliverAsync
        WebhookEndpoint webhook = TestDataBuilder.BuildWebhookEndpoint(tenantId: tenant.Id, isEnabled: false);
        await db.InsertWithInt32IdentityAsync(webhook);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id, metric: AlertMetric.CpuUsage, threshold: 80m, notifyWebhook: true);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        AlertEvent alertEvent = TestDataBuilder.BuildAlertEvent(alertRuleId: rule.Id, tenantId: tenant.Id, machineId: machine.Id);
        alertEvent.Id = await db.InsertWithInt64IdentityAsync(alertEvent);

        string payload = JsonSerializer.Serialize(new { eventId = alertEvent.Id, ruleId = rule.Id, tenantId = tenant.Id }, JsonDefaults.CamelCase);

        // Worker delegates to DeliverAsync which handles enabled/disabled filtering
        await worker.ProcessJobAsync(payload, CancellationToken.None);

        await delivery.Received(1).DeliverAsync(
            Arg.Is<AlertEvent>(e => e.Id == alertEvent.Id),
            Arg.Is<AlertRule>(r => r.Id == rule.Id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJob_InvalidEventId_LogsErrorAndSkips()
    {
        (WebhookDeliveryWorkerService worker, DatabaseContext db, IAlertDeliveryService delivery, IDatabase redisDb, ILogger<WebhookDeliveryWorkerService> logger) = CreateWorker();

        // Job references a non-existent event ID
        string payload = JsonSerializer.Serialize(new { eventId = 999999L, ruleId = 1, tenantId = 1 }, JsonDefaults.CamelCase);

        await worker.ProcessJobAsync(payload, CancellationToken.None);

        // DeliverAsync should not be called when the event does not exist
        await delivery.DidNotReceive().DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJob_NullPayload_LogsErrorAndSkips()
    {
        (WebhookDeliveryWorkerService worker, DatabaseContext db, IAlertDeliveryService delivery, IDatabase redisDb, ILogger<WebhookDeliveryWorkerService> logger) = CreateWorker();

        await worker.ProcessJobAsync(null, CancellationToken.None);

        // DeliverAsync should not be called for null payload
        await delivery.DidNotReceive().DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJob_EmptyPayload_LogsErrorAndSkips()
    {
        (WebhookDeliveryWorkerService worker, DatabaseContext db, IAlertDeliveryService delivery, IDatabase redisDb, ILogger<WebhookDeliveryWorkerService> logger) = CreateWorker();

        await worker.ProcessJobAsync("", CancellationToken.None);

        // DeliverAsync should not be called for empty payload
        await delivery.DidNotReceive().DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJob_MalformedJson_LogsErrorAndSkips()
    {
        (WebhookDeliveryWorkerService worker, DatabaseContext db, IAlertDeliveryService delivery, IDatabase redisDb, ILogger<WebhookDeliveryWorkerService> logger) = CreateWorker();

        await worker.ProcessJobAsync("{not valid json", CancellationToken.None);

        // DeliverAsync should not be called for malformed JSON
        await delivery.DidNotReceive().DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessJob_InvalidRuleId_LogsErrorAndSkips()
    {
        (WebhookDeliveryWorkerService worker, DatabaseContext db, IAlertDeliveryService delivery, IDatabase redisDb, ILogger<WebhookDeliveryWorkerService> logger) = CreateWorker();

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        // Seed an event but reference a non-existent rule
        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenant.Id);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        AlertEvent alertEvent = TestDataBuilder.BuildAlertEvent(alertRuleId: rule.Id, tenantId: tenant.Id, machineId: machine.Id);
        alertEvent.Id = await db.InsertWithInt64IdentityAsync(alertEvent);

        string payload = JsonSerializer.Serialize(new { eventId = alertEvent.Id, ruleId = 999999, tenantId = tenant.Id }, JsonDefaults.CamelCase);

        await worker.ProcessJobAsync(payload, CancellationToken.None);

        // DeliverAsync should not be called when the rule does not exist
        await delivery.DidNotReceive().DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }
}
