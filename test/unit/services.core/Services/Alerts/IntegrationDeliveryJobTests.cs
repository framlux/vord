// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Alerts;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Framlux.FleetManagement.Test.Services;

public sealed class IntegrationDeliveryJobTests
{
    private static AlertEvent BuildEvent(long id, int ruleId = 1, int tenantId = 1, long machineId = 1)
    {
        return new AlertEvent
        {
            Id = id,
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = machineId,
            Severity = AlertSeverity.Warning,
            Message = "test",
            Details = "{}",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
    }

    private static AlertRule BuildRule(int id, int tenantId = 1)
    {
        return new AlertRule
        {
            Id = id,
            TenantId = tenantId,
            Name = "Test Rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Test]
    public async Task DeliverAsync_EventAndRuleFound_CallsDeliveryService()
    {
        IAlertEventRepository eventRepo = Substitute.For<IAlertEventRepository>();
        IAlertRuleRepository ruleRepo = Substitute.For<IAlertRuleRepository>();
        IAlertDeliveryService delivery = Substitute.For<IAlertDeliveryService>();
        ILogger<IntegrationDeliveryJob> logger = Substitute.For<ILogger<IntegrationDeliveryJob>>();

        AlertEvent evt = BuildEvent(id: 42, ruleId: 7, tenantId: 3);
        AlertRule rule = BuildRule(id: 7, tenantId: 3);

        eventRepo.GetAlertEventByIdAsync(42, Arg.Any<CancellationToken>()).Returns(evt);
        ruleRepo.GetAlertRuleByIdAsync(7, Arg.Any<CancellationToken>()).Returns(rule);

        IntegrationDeliveryJob job = new(eventRepo, ruleRepo, delivery, logger);

        await job.DeliverAsync(eventId: 42, ruleId: 7, tenantId: 3, CancellationToken.None);

        await delivery.Received(1).DeliverAsync(evt, rule, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeliverAsync_EventNotFound_SkipsDeliveryWithoutThrowing()
    {
        // Intent: a missing event should not cause Hangfire to retry — the event will never appear,
        // so retries would burn the [AutomaticRetry] budget for nothing. The job logs and returns.
        IAlertEventRepository eventRepo = Substitute.For<IAlertEventRepository>();
        IAlertRuleRepository ruleRepo = Substitute.For<IAlertRuleRepository>();
        IAlertDeliveryService delivery = Substitute.For<IAlertDeliveryService>();
        ILogger<IntegrationDeliveryJob> logger = Substitute.For<ILogger<IntegrationDeliveryJob>>();

        eventRepo.GetAlertEventByIdAsync(99, Arg.Any<CancellationToken>()).Returns((AlertEvent?)null);

        IntegrationDeliveryJob job = new(eventRepo, ruleRepo, delivery, logger);

        await job.DeliverAsync(eventId: 99, ruleId: 1, tenantId: 1, CancellationToken.None);

        await delivery.DidNotReceive().DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
        await ruleRepo.DidNotReceive().GetAlertRuleByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeliverAsync_RuleNotFound_SkipsDeliveryWithoutThrowing()
    {
        // Intent: rule may have been deleted between enqueue and delivery. Same reasoning as
        // event-not-found — Hangfire must not retry on a permanent miss.
        IAlertEventRepository eventRepo = Substitute.For<IAlertEventRepository>();
        IAlertRuleRepository ruleRepo = Substitute.For<IAlertRuleRepository>();
        IAlertDeliveryService delivery = Substitute.For<IAlertDeliveryService>();
        ILogger<IntegrationDeliveryJob> logger = Substitute.For<ILogger<IntegrationDeliveryJob>>();

        AlertEvent evt = BuildEvent(id: 1, ruleId: 99);
        eventRepo.GetAlertEventByIdAsync(1, Arg.Any<CancellationToken>()).Returns(evt);
        ruleRepo.GetAlertRuleByIdAsync(99, Arg.Any<CancellationToken>()).Returns((AlertRule?)null);

        IntegrationDeliveryJob job = new(eventRepo, ruleRepo, delivery, logger);

        await job.DeliverAsync(eventId: 1, ruleId: 99, tenantId: 1, CancellationToken.None);

        await delivery.DidNotReceive().DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeliverAsync_DeliveryServiceThrows_PropagatesForHangfireRetry()
    {
        // Intent: a transient delivery failure (e.g., webhook 5xx, network timeout) must propagate
        // so Hangfire's [AutomaticRetry(3, 10/20/40s)] applies. The predecessor manually re-pushed
        // onto the Redis list with a custom backoff; Hangfire handles this natively now.
        IAlertEventRepository eventRepo = Substitute.For<IAlertEventRepository>();
        IAlertRuleRepository ruleRepo = Substitute.For<IAlertRuleRepository>();
        IAlertDeliveryService delivery = Substitute.For<IAlertDeliveryService>();
        ILogger<IntegrationDeliveryJob> logger = Substitute.For<ILogger<IntegrationDeliveryJob>>();

        AlertEvent evt = BuildEvent(id: 1);
        AlertRule rule = BuildRule(id: 1);
        eventRepo.GetAlertEventByIdAsync(1, Arg.Any<CancellationToken>()).Returns(evt);
        ruleRepo.GetAlertRuleByIdAsync(1, Arg.Any<CancellationToken>()).Returns(rule);
        delivery.DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("webhook 503"));

        IntegrationDeliveryJob job = new(eventRepo, ruleRepo, delivery, logger);

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.DeliverAsync(eventId: 1, ruleId: 1, tenantId: 1, CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.Message).IsEqualTo("webhook 503");
    }

    [Test]
    public async Task DeliverAsync_TokenForwardedToReposAndDelivery()
    {
        using CancellationTokenSource cts = new();

        IAlertEventRepository eventRepo = Substitute.For<IAlertEventRepository>();
        IAlertRuleRepository ruleRepo = Substitute.For<IAlertRuleRepository>();
        IAlertDeliveryService delivery = Substitute.For<IAlertDeliveryService>();
        ILogger<IntegrationDeliveryJob> logger = Substitute.For<ILogger<IntegrationDeliveryJob>>();

        AlertEvent evt = BuildEvent(id: 1);
        AlertRule rule = BuildRule(id: 1);
        eventRepo.GetAlertEventByIdAsync(1, Arg.Any<CancellationToken>()).Returns(evt);
        ruleRepo.GetAlertRuleByIdAsync(1, Arg.Any<CancellationToken>()).Returns(rule);

        IntegrationDeliveryJob job = new(eventRepo, ruleRepo, delivery, logger);

        await job.DeliverAsync(eventId: 1, ruleId: 1, tenantId: 1, cts.Token);

        await eventRepo.Received(1).GetAlertEventByIdAsync(1, cts.Token);
        await ruleRepo.Received(1).GetAlertRuleByIdAsync(1, cts.Token);
        await delivery.Received(1).DeliverAsync(evt, rule, cts.Token);
    }

    [Test]
    public async Task DeliverAsync_ZeroEventId_Throws()
    {
        IntegrationDeliveryJob job = BuildJob();

        ArgumentOutOfRangeException? ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => job.DeliverAsync(eventId: 0, ruleId: 1, tenantId: 1, CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("eventId");
    }

    [Test]
    public async Task DeliverAsync_NegativeEventId_Throws()
    {
        IntegrationDeliveryJob job = BuildJob();

        ArgumentOutOfRangeException? ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => job.DeliverAsync(eventId: -1, ruleId: 1, tenantId: 1, CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("eventId");
    }

    [Test]
    public async Task DeliverAsync_ZeroRuleId_Throws()
    {
        IntegrationDeliveryJob job = BuildJob();

        ArgumentOutOfRangeException? ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => job.DeliverAsync(eventId: 1, ruleId: 0, tenantId: 1, CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("ruleId");
    }

    [Test]
    public async Task DeliverAsync_NegativeRuleId_Throws()
    {
        IntegrationDeliveryJob job = BuildJob();

        ArgumentOutOfRangeException? ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => job.DeliverAsync(eventId: 1, ruleId: -7, tenantId: 1, CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("ruleId");
    }

    [Test]
    public async Task DeliverAsync_ZeroTenantId_Throws()
    {
        IntegrationDeliveryJob job = BuildJob();

        ArgumentOutOfRangeException? ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => job.DeliverAsync(eventId: 1, ruleId: 1, tenantId: 0, CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("tenantId");
    }

    [Test]
    public async Task DeliverAsync_NegativeTenantId_Throws()
    {
        IntegrationDeliveryJob job = BuildJob();

        ArgumentOutOfRangeException? ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => job.DeliverAsync(eventId: 1, ruleId: 1, tenantId: -1, CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("tenantId");
    }

    [Test]
    public async Task Constructor_NullAlertEventRepository_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            IntegrationDeliveryJob _ = new(
                null!,
                Substitute.For<IAlertRuleRepository>(),
                Substitute.For<IAlertDeliveryService>(),
                Substitute.For<ILogger<IntegrationDeliveryJob>>());

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("alertEventRepository");
    }

    [Test]
    public async Task Constructor_NullAlertRuleRepository_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            IntegrationDeliveryJob _ = new(
                Substitute.For<IAlertEventRepository>(),
                null!,
                Substitute.For<IAlertDeliveryService>(),
                Substitute.For<ILogger<IntegrationDeliveryJob>>());

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("alertRuleRepository");
    }

    [Test]
    public async Task Constructor_NullDeliveryService_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            IntegrationDeliveryJob _ = new(
                Substitute.For<IAlertEventRepository>(),
                Substitute.For<IAlertRuleRepository>(),
                null!,
                Substitute.For<ILogger<IntegrationDeliveryJob>>());

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("deliveryService");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            IntegrationDeliveryJob _ = new(
                Substitute.For<IAlertEventRepository>(),
                Substitute.For<IAlertRuleRepository>(),
                Substitute.For<IAlertDeliveryService>(),
                null!);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    private static IntegrationDeliveryJob BuildJob()
    {
        return new IntegrationDeliveryJob(
            Substitute.For<IAlertEventRepository>(),
            Substitute.For<IAlertRuleRepository>(),
            Substitute.For<IAlertDeliveryService>(),
            Substitute.For<ILogger<IntegrationDeliveryJob>>());
    }

    [Test]
    public async Task DeliverAsync_RetryAttribute_UsesCorrectDelays()
    {
        // Intent: pin the retry delays to {10, 20, 40} seconds matching the predecessor service's
        // SLO. The transient-failure window for Slack/PagerDuty/Discord is typically seconds, not
        // minutes; stretching the final retry past a minute risks SLA breach on alert delivery.
        MethodInfo method = typeof(IntegrationDeliveryJob).GetMethod(nameof(IntegrationDeliveryJob.DeliverAsync))!;
        AutomaticRetryAttribute attr = method.GetCustomAttribute<AutomaticRetryAttribute>()!;

        await Assert.That(attr).IsNotNull();
        await Assert.That(attr.Attempts).IsEqualTo(3);
        await Assert.That(attr.DelaysInSeconds).IsEquivalentTo(new[] { 10, 20, 40 });
    }

    [Test]
    public async Task DeliverAsync_ExhaustedRetries_TerminalBehaviorIsDocumented()
    {
        // Intent: pin the current contract after [AutomaticRetry(Attempts=3)] is exhausted.
        // Today the job has NO terminal hook: no dead-letter push, no compensating action, no
        // state-filter attribute that absorbs the final failure. Hangfire transitions the job to
        // the Failed state and that is the end of it.
        //
        // The predecessor service (IntegrationDeliveryWorkerService) DID move exhausted jobs to
        // a Redis dead-letter list. That behavior was intentionally dropped — the Hangfire Failed
        // tab is the new dead letter. If a future change re-introduces dead-lettering (custom
        // filter or in-method fallback), this test must fail and force an explicit update so the
        // reviewer can confirm the dead-letter sink, alerting, and replay path.
        IAlertEventRepository eventRepo = Substitute.For<IAlertEventRepository>();
        IAlertRuleRepository ruleRepo = Substitute.For<IAlertRuleRepository>();
        IAlertDeliveryService delivery = Substitute.For<IAlertDeliveryService>();
        ILogger<IntegrationDeliveryJob> logger = Substitute.For<ILogger<IntegrationDeliveryJob>>();

        AlertEvent evt = BuildEvent(id: 1);
        AlertRule rule = BuildRule(id: 1);
        eventRepo.GetAlertEventByIdAsync(1, Arg.Any<CancellationToken>()).Returns(evt);
        ruleRepo.GetAlertRuleByIdAsync(1, Arg.Any<CancellationToken>()).Returns(rule);
        delivery.DeliverAsync(Arg.Any<AlertEvent>(), Arg.Any<AlertRule>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("permanent webhook 410"));

        IntegrationDeliveryJob job = new(eventRepo, ruleRepo, delivery, logger);

        // Same exception type, every invocation. The handler does not catch on a hypothetical
        // "last attempt" — it has no way to know which attempt it is on, by design.
        InvalidOperationException? ex1 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.DeliverAsync(eventId: 1, ruleId: 1, tenantId: 1, CancellationToken.None));
        await Assert.That(ex1).IsNotNull();
        InvalidOperationException? ex2 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.DeliverAsync(eventId: 1, ruleId: 1, tenantId: 1, CancellationToken.None));
        await Assert.That(ex2).IsNotNull();
        InvalidOperationException? ex3 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.DeliverAsync(eventId: 1, ruleId: 1, tenantId: 1, CancellationToken.None));
        await Assert.That(ex3).IsNotNull();
        InvalidOperationException? ex4 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.DeliverAsync(eventId: 1, ruleId: 1, tenantId: 1, CancellationToken.None));
        await Assert.That(ex4).IsNotNull();

        await Assert.That(ex1!.Message).IsEqualTo("permanent webhook 410");
        await Assert.That(ex4!.Message).IsEqualTo("permanent webhook 410");

        // Delivery service was hit on each attempt — no short-circuit, no special last-attempt path.
        await delivery.Received(4).DeliverAsync(evt, rule, Arg.Any<CancellationToken>());

        // No JobFilterAttribute (state filter, electing filter, etc.) is present on the class or
        // method that could quietly intercept the terminal failure. The only Hangfire attribute
        // on DeliverAsync is AutomaticRetry, which only governs retry scheduling.
        MethodInfo method = typeof(IntegrationDeliveryJob).GetMethod(nameof(IntegrationDeliveryJob.DeliverAsync))!;
        JobFilterAttribute[] methodFilters = (JobFilterAttribute[])method.GetCustomAttributes(typeof(JobFilterAttribute), inherit: false);
        JobFilterAttribute[] classFilters = (JobFilterAttribute[])typeof(IntegrationDeliveryJob).GetCustomAttributes(typeof(JobFilterAttribute), inherit: false);

        await Assert.That(methodFilters.Length).IsEqualTo(1);
        await Assert.That(methodFilters[0]).IsTypeOf<AutomaticRetryAttribute>();
        await Assert.That(classFilters.Length).IsEqualTo(0);
    }

    [Test]
    public async Task DeliverAsync_HasNoDisableConcurrentExecution()
    {
        // Intent: integration delivery is idempotent per (event, integration) via the attempt
        // registry; serializing all deliveries through one Hangfire lock would create a
        // queue bottleneck under fan-out. Pin absence of [DisableConcurrentExecution].
        MethodInfo method = typeof(IntegrationDeliveryJob).GetMethod(nameof(IntegrationDeliveryJob.DeliverAsync))
            ?? throw new InvalidOperationException("DeliverAsync not found");
        DisableConcurrentExecutionAttribute? attr = method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();

        await Assert.That(attr).IsNull();
    }
}
