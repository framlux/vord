// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using Framlux.FleetManagement.Services.Core.Telemetry;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Alerts;

/// <summary>
/// Tests the windowed failed-SSH-login evaluation: per-rule windows counted from telemetry rows,
/// firing, and the resolve-or-re-arm decision that keeps a second incident from being swallowed.
/// </summary>
public sealed class FailedSshLoginWindowEvaluationTests
{
    private const int TenantId = 1;
    private const long MachineId = 100;

    private readonly IAlertRuleRepository _alertRuleRepo = Substitute.For<IAlertRuleRepository>();
    private readonly IAlertEventRepository _alertEventRepo = Substitute.For<IAlertEventRepository>();
    private readonly IMachineStateRepository _machineStateRepo = Substitute.For<IMachineStateRepository>();
    private readonly ISubscriptionService _subscriptionService = Substitute.For<ISubscriptionService>();
    private readonly IAlertDeliveryService _deliveryService = Substitute.For<IAlertDeliveryService>();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));

    private EventAlertService CreateService()
    {
        ServiceCollection services = new();
        services.AddSingleton(_alertRuleRepo);
        services.AddSingleton(_alertEventRepo);
        services.AddSingleton(_machineStateRepo);
        services.AddSingleton(_subscriptionService);
        ServiceProvider provider = services.BuildServiceProvider();

        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new EventAlertService(
            scopeFactory,
            _deliveryService,
            TestMetricsFactory.CreateAlertPipelineMetrics(),
            _timeProvider,
            NullLogger<EventAlertService>.Instance);
    }

    private void SetupSubscription(SubscriptionTier tier)
    {
        _subscriptionService.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new TenantSubscription
            {
                TenantId = TenantId,
                Tier = tier,
                Status = SubscriptionStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
    }

    private static AlertRule BuildRule(int id, int durationMinutes, decimal threshold = 5, bool isCustom = false, AlertOperator op = AlertOperator.GreaterThan)
    {
        return new AlertRule
        {
            Id = id,
            TenantId = TenantId,
            Name = $"More than {threshold} failed SSH logins",
            Metric = AlertMetric.FailedSshLogin,
            Operator = op,
            Threshold = threshold,
            DurationMinutes = durationMinutes,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = isCustom,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private void SetupRules(params AlertRule[] rules)
    {
        _alertRuleRepo.GetEnabledRulesForMachineByMetricAsync(TenantId, MachineId, AlertMetric.FailedSshLogin, Arg.Any<CancellationToken>())
            .Returns(rules.ToList());
    }

    private void SetupCount(int windowMinutes, int count)
    {
        DateTimeOffset windowEnd = _timeProvider.GetUtcNow();

        _machineStateRepo.CountTelemetryWithPayloadMarkerAsync(
                MachineId,
                TelemetryTypeIds.SshSessions,
                AlertConstants.FailedSshLoginPayloadMarker,
                windowEnd.AddMinutes(-windowMinutes),
                windowEnd,
                Arg.Any<CancellationToken>())
            .Returns(count);
    }

    private void SetupPayloads(params SshSessionPayload[] attempts)
    {
        _machineStateRepo.GetTelemetryPayloadsWithMarkerAsync(
                Arg.Any<long>(),
                Arg.Any<short>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(attempts.Select(a => JsonSerializer.Serialize(a, JsonDefaults.SnakeCase)).ToList());
    }

    private static SshSessionPayload Attempt(string user, string sourceIp)
    {
        return new SshSessionPayload
        {
            User = user,
            SourceIp = sourceIp,
            SourcePort = 55000,
            Action = "failed",
            AuthMethod = "password",
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
        };
    }

    /// <summary>
    /// Makes the event repository behave like the real de-duplication: an event is created only
    /// while no unresolved event exists for the rule and machine, and resolving clears that state.
    /// </summary>
    private List<AlertEvent> SetupDeduplicatingEventRepository()
    {
        List<AlertEvent> created = [];
        HashSet<int> liveRules = [];
        long nextId = 1;

        _alertEventRepo.CreateEventIfNotExistsAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                AlertEvent candidate = callInfo.Arg<AlertEvent>();

                if (liveRules.Add(candidate.AlertRuleId) == false)
                {
                    return null;
                }

                candidate.Id = nextId++;
                created.Add(candidate);

                return candidate;
            });

        _alertEventRepo.HasActiveEventForRuleMachineAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => liveRules.Contains(callInfo.ArgAt<int>(0)));

        _alertEventRepo.ResolveEventsForRuleMachineAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                liveRules.Remove(callInfo.ArgAt<int>(0));

                return Task.CompletedTask;
            });

        return created;
    }

    [Test]
    public async Task FreeTier_IsNotEvaluatedAtAll()
    {
        SetupSubscription(SubscriptionTier.Free);
        EventAlertService service = CreateService();

        bool stillLive = await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        await Assert.That(stillLive).IsFalse();
        await _alertRuleRepo.DidNotReceive()
            .GetEnabledRulesForMachineByMetricAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<AlertMetric>(), Arg.Any<CancellationToken>());
        await _machineStateRepo.DidNotReceive()
            .CountTelemetryWithPayloadMarkerAsync(Arg.Any<long>(), Arg.Any<short>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoRulesForTheMachine_EndsTheChain()
    {
        SetupSubscription(SubscriptionTier.Pro);
        SetupRules();
        EventAlertService service = CreateService();

        bool stillLive = await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        await Assert.That(stillLive).IsFalse();
    }

    /// <summary>
    /// A custom rule belongs to Team. The downgrade paths clear its enabled flag, but that flag is a
    /// stored bit several writers maintain, so the tier is asked here as well.
    /// </summary>
    [Test]
    public async Task ProTier_TeamAuthoredCustomRule_IsRefused()
    {
        SetupSubscription(SubscriptionTier.Pro);
        SetupRules(BuildRule(id: 11, durationMinutes: 10, isCustom: true));
        EventAlertService service = CreateService();

        bool stillLive = await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        await Assert.That(stillLive).IsFalse();
        await _machineStateRepo.DidNotReceive()
            .CountTelemetryWithPayloadMarkerAsync(Arg.Any<long>(), Arg.Any<short>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await _alertEventRepo.DidNotReceive()
            .CreateEventIfNotExistsAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The Redis gate is fixed at the platform window and only decides whether to wake an evaluation
    /// up. The window each rule is judged against comes from its own DurationMinutes, which is why
    /// two rules disagreeing about the window length is not a contradiction the gate has to resolve.
    /// </summary>
    [Test]
    public async Task TwoRulesWithDifferentDurations_EachCountsItsOwnWindow()
    {
        SetupSubscription(SubscriptionTier.Team);
        SetupRules(BuildRule(id: 21, durationMinutes: 5), BuildRule(id: 22, durationMinutes: 10, threshold: 20));
        SetupCount(windowMinutes: 5, count: 7);
        SetupCount(windowMinutes: 10, count: 12);
        SetupPayloads(Attempt("root", "203.0.113.7"));
        SetupDeduplicatingEventRepository();

        EventAlertService service = CreateService();

        await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        DateTimeOffset windowEnd = _timeProvider.GetUtcNow();

        await _machineStateRepo.Received(1).CountTelemetryWithPayloadMarkerAsync(
            MachineId, TelemetryTypeIds.SshSessions, AlertConstants.FailedSshLoginPayloadMarker,
            windowEnd.AddMinutes(-5), windowEnd, Arg.Any<CancellationToken>());
        await _machineStateRepo.Received(1).CountTelemetryWithPayloadMarkerAsync(
            MachineId, TelemetryTypeIds.SshSessions, AlertConstants.FailedSshLoginPayloadMarker,
            windowEnd.AddMinutes(-10), windowEnd, Arg.Any<CancellationToken>());

        // The five-minute rule is over its threshold of five; the ten-minute rule's twelve failures
        // are under its threshold of twenty, so only one event exists.
        await _alertEventRepo.Received(1).CreateEventIfNotExistsAsync(
            Arg.Is<AlertEvent>(e => e.AlertRuleId == 21), Arg.Any<CancellationToken>());
        await _alertEventRepo.DidNotReceive().CreateEventIfNotExistsAsync(
            Arg.Is<AlertEvent>(e => e.AlertRuleId == 22), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WindowOverThreshold_FiresAndEnqueuesDelivery()
    {
        SetupSubscription(SubscriptionTier.Pro);
        SetupRules(BuildRule(id: 31, durationMinutes: 5));
        SetupCount(windowMinutes: 5, count: 7);
        SetupPayloads(Attempt("root", "203.0.113.7"), Attempt("admin", "203.0.113.7"), Attempt("root", "198.51.100.4"));
        List<AlertEvent> created = SetupDeduplicatingEventRepository();

        EventAlertService service = CreateService();

        bool stillLive = await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        await Assert.That(stillLive).IsTrue();
        await Assert.That(created.Count).IsEqualTo(1);
        await Assert.That(created[0].Severity).IsEqualTo(AlertSeverity.Warning);
        await Assert.That(created[0].Status).IsEqualTo(AlertEventStatus.Triggered);
        await Assert.That(created[0].Message).IsEqualTo("7 failed SSH logins in 5 minute(s) — most attempts from 203.0.113.7");
        await _deliveryService.Received(1).EnqueueAsync(created[0].Id, 31, TenantId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A window gathers many attempts from many addresses against many user names, so its details
    /// must describe the window rather than whichever single attempt happened to be read first.
    /// </summary>
    [Test]
    public async Task FiredEventDetails_CarryWindowAggregatesNotOneAttempt()
    {
        SetupSubscription(SubscriptionTier.Pro);
        SetupRules(BuildRule(id: 41, durationMinutes: 5, threshold: 3));
        SetupCount(windowMinutes: 5, count: 4);
        SetupPayloads(
            Attempt("root", "198.51.100.4"),
            Attempt("root", "203.0.113.7"),
            Attempt("admin", "203.0.113.7"),
            Attempt("root", "203.0.113.7"));
        List<AlertEvent> created = SetupDeduplicatingEventRepository();

        EventAlertService service = CreateService();

        await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        FailedSshLoginWindowSummary? summary = JsonSerializer.Deserialize<FailedSshLoginWindowSummary>(
            created[0].Details!, JsonDefaults.CamelCase);

        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.FailureCount).IsEqualTo(4);
        await Assert.That(summary.WindowMinutes).IsEqualTo(5);
        await Assert.That(summary.WindowEnd).IsEqualTo(_timeProvider.GetUtcNow());
        await Assert.That(summary.WindowStart).IsEqualTo(_timeProvider.GetUtcNow().AddMinutes(-5));
        await Assert.That(summary.SampledAttempts).IsEqualTo(4);
        await Assert.That(summary.DistinctSourceIpCount).IsEqualTo(2);
        await Assert.That(summary.TopSourceIp).IsEqualTo("203.0.113.7");
        await Assert.That(summary.TopSourceIpAttempts).IsEqualTo(3);
        await Assert.That(summary.DistinctUserCount).IsEqualTo(2);
        await Assert.That(summary.Users).Contains("root");
        await Assert.That(summary.Users).Contains("admin");
    }

    /// <summary>
    /// The stored payload is text and the count is a substring match, so the serialized shape is a
    /// contract. A serializer change that moved away from this spelling would count zero failures
    /// forever and silently un-ship the feature.
    /// </summary>
    [Test]
    public async Task SerializedSshPayload_ContainsTheFailedActionMarkerTheCountMatchesOn()
    {
        string payload = JsonSerializer.Serialize(Attempt("root", "203.0.113.7"), JsonDefaults.SnakeCase);

        await Assert.That(payload).Contains(AlertConstants.FailedSshLoginPayloadMarker);
    }

    /// <summary>
    /// The operator is read from the rule rather than assumed, so a count that merely equals the
    /// threshold does not fire a "greater than" rule.
    /// </summary>
    [Test]
    public async Task CountEqualToThreshold_DoesNotFireAGreaterThanRule()
    {
        SetupSubscription(SubscriptionTier.Pro);
        SetupRules(BuildRule(id: 51, durationMinutes: 5, threshold: 5));
        SetupCount(windowMinutes: 5, count: 5);
        SetupDeduplicatingEventRepository();

        EventAlertService service = CreateService();

        bool stillLive = await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        await Assert.That(stillLive).IsTrue();
        await _alertEventRepo.DidNotReceive().CreateEventIfNotExistsAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>());
        await _alertEventRepo.DidNotReceive().ResolveEventsForRuleMachineAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The evaluation never asks Redis anything. When the gate is unreachable the ingest path fails
    /// open and schedules the job anyway, and it lands in exactly this code, counting the same
    /// telemetry rows — which is the whole reason the count is not kept in Redis.
    /// </summary>
    [Test]
    public async Task WithNoGateInvolved_TheTelemetryCountStillFires()
    {
        SetupSubscription(SubscriptionTier.Pro);
        SetupRules(BuildRule(id: 61, durationMinutes: 5));
        SetupCount(windowMinutes: 5, count: 9);
        SetupPayloads(Attempt("root", "203.0.113.7"));
        List<AlertEvent> created = SetupDeduplicatingEventRepository();

        EventAlertService service = CreateService();

        bool stillLive = await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        await Assert.That(stillLive).IsTrue();
        await Assert.That(created.Count).IsEqualTo(1);
    }

    /// <summary>
    /// The regression the design existed to prevent: an incident spanning more than one window, then
    /// quiet, then a second attack. Because an event is created only while none is unresolved, a
    /// chain that failed to reach a quiet window would leave the first event Triggered and the
    /// de-duplication would swallow the second attack entirely.
    /// </summary>
    [Test]
    public async Task AttackSpanningWindowsThenQuietThenASecondAttack_FiresTwice()
    {
        SetupSubscription(SubscriptionTier.Pro);
        SetupRules(BuildRule(id: 71, durationMinutes: 5));
        SetupPayloads(Attempt("root", "203.0.113.7"));
        List<AlertEvent> created = SetupDeduplicatingEventRepository();

        EventAlertService service = CreateService();

        SetupCount(windowMinutes: 5, count: 9);
        bool afterFirstWindow = await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        // Second window: still under attack, so the incident stays open and the chain re-arms.
        _timeProvider.Advance(AlertConstants.FailedSshLoginWindow);
        SetupCount(windowMinutes: 5, count: 6);
        bool afterSecondWindow = await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        // Third window: the attack has stopped. This is the only shape that resolves.
        _timeProvider.Advance(AlertConstants.FailedSshLoginWindow);
        SetupCount(windowMinutes: 5, count: 0);
        bool afterQuietWindow = await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        // A later attack must be able to raise its own incident.
        _timeProvider.Advance(TimeSpan.FromHours(3));
        SetupCount(windowMinutes: 5, count: 11);
        bool afterSecondAttack = await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        await Assert.That(afterFirstWindow).IsTrue();
        await Assert.That(afterSecondWindow).IsTrue();
        await Assert.That(afterQuietWindow).IsFalse();
        await Assert.That(afterSecondAttack).IsTrue();
        await Assert.That(created.Count).IsEqualTo(2);
        await Assert.That(created[1].Message).IsEqualTo("11 failed SSH logins in 5 minute(s) — most attempts from 203.0.113.7");
    }

    /// <summary>
    /// A window still holding failures must not resolve, even when it is below the rule's threshold:
    /// the incident is live and the trailing chain has to keep running.
    /// </summary>
    [Test]
    public async Task WindowUnderThresholdButNotEmpty_DoesNotResolve()
    {
        SetupSubscription(SubscriptionTier.Pro);
        SetupRules(BuildRule(id: 81, durationMinutes: 5));
        SetupCount(windowMinutes: 5, count: 2);
        SetupDeduplicatingEventRepository();

        EventAlertService service = CreateService();

        bool stillLive = await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        await Assert.That(stillLive).IsTrue();
        await _alertEventRepo.DidNotReceive().ResolveEventsForRuleMachineAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Resolution is per rule. The per-metric resolve would clear a longer custom rule's still-live
    /// incident on the strength of the built-in's shorter quiet window.
    /// </summary>
    [Test]
    public async Task QuietWindow_ResolvesOnlyTheRuleWhoseWindowIsEmpty()
    {
        SetupSubscription(SubscriptionTier.Team);
        SetupRules(BuildRule(id: 91, durationMinutes: 5), BuildRule(id: 92, durationMinutes: 10, isCustom: true));
        SetupCount(windowMinutes: 5, count: 0);
        SetupCount(windowMinutes: 10, count: 8);
        SetupPayloads(Attempt("root", "203.0.113.7"));
        SetupDeduplicatingEventRepository();

        EventAlertService service = CreateService();

        bool stillLive = await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        await Assert.That(stillLive).IsTrue();
        await _alertEventRepo.Received(1).ResolveEventsForRuleMachineAsync(91, MachineId, Arg.Any<CancellationToken>());
        await _alertEventRepo.DidNotReceive().ResolveEventsForRuleMachineAsync(92, MachineId, Arg.Any<CancellationToken>());
        await _alertEventRepo.DidNotReceive().ResolveEventsForMachineByMetricAsync(Arg.Any<long>(), Arg.Any<AlertMetric>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The evaluation is scheduled one window ahead and runs at that instant plus queue latency, so a
    /// window measured purely back from the run instant would start after the telemetry that opened
    /// it. Every row in an envelope carries one server receipt time, so a burst delivered in a single
    /// envelope would fall out of the window entirely and the incident would never be seen.
    /// </summary>
    [Test]
    public async Task WindowOpenedBeforeTheJobRan_CountsBackFromWhenTheWindowOpened()
    {
        SetupSubscription(SubscriptionTier.Pro);
        SetupRules(BuildRule(id: 101, durationMinutes: 5));
        SetupPayloads(Attempt("root", "203.0.113.7"));
        List<AlertEvent> created = SetupDeduplicatingEventRepository();

        // The burst lands here, in one envelope, and opens the window.
        DateTimeOffset windowOpenedAt = _timeProvider.GetUtcNow();

        // The job is scheduled a full window out and picked up a little after that.
        _timeProvider.Advance(AlertConstants.FailedSshLoginWindow + TimeSpan.FromSeconds(8));

        DateTimeOffset windowEnd = _timeProvider.GetUtcNow();
        _machineStateRepo.CountTelemetryWithPayloadMarkerAsync(
                MachineId,
                TelemetryTypeIds.SshSessions,
                AlertConstants.FailedSshLoginPayloadMarker,
                windowOpenedAt,
                windowEnd,
                Arg.Any<CancellationToken>())
            .Returns(200);

        EventAlertService service = CreateService();

        bool stillLive = await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, windowOpenedAt, CancellationToken.None);

        await Assert.That(stillLive).IsTrue();
        await Assert.That(created.Count).IsEqualTo(1);
        await _machineStateRepo.Received(1).CountTelemetryWithPayloadMarkerAsync(
            MachineId, TelemetryTypeIds.SshSessions, AlertConstants.FailedSshLoginPayloadMarker,
            windowOpenedAt, windowEnd, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Widening backwards is bounded. A run recovered from a long backlog must not count hours of
    /// attempts against a five-minute rule just because its window was opened that long ago.
    /// </summary>
    [Test]
    public async Task WindowOpenedFarInThePast_IsBoundedByTheSlackAllowance()
    {
        SetupSubscription(SubscriptionTier.Pro);
        SetupRules(BuildRule(id: 111, durationMinutes: 5));
        SetupDeduplicatingEventRepository();

        DateTimeOffset windowEnd = _timeProvider.GetUtcNow();
        DateTimeOffset windowOpenedAt = windowEnd.AddHours(-3);
        DateTimeOffset expectedStart = windowEnd.AddMinutes(-5) - AlertConstants.FailedSshLoginMaxWindowSlack;

        EventAlertService service = CreateService();

        await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, windowOpenedAt, CancellationToken.None);

        await _machineStateRepo.Received(1).CountTelemetryWithPayloadMarkerAsync(
            MachineId, TelemetryTypeIds.SshSessions, AlertConstants.FailedSshLoginPayloadMarker,
            expectedStart, windowEnd, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A live incident stays over threshold on every re-armed run, so building the payload sample
    /// before the de-duplication check would read and deserialize hundreds of payload rows per run
    /// only to throw the result away when the insert found the event already open.
    /// </summary>
    [Test]
    public async Task SecondRunWhileTheEventIsStillOpen_ReadsNoPayloads()
    {
        SetupSubscription(SubscriptionTier.Pro);
        SetupRules(BuildRule(id: 121, durationMinutes: 5));
        SetupCount(windowMinutes: 5, count: 30);
        SetupPayloads(Attempt("root", "203.0.113.7"));
        List<AlertEvent> created = SetupDeduplicatingEventRepository();

        EventAlertService service = CreateService();

        await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        _machineStateRepo.ClearReceivedCalls();
        _alertEventRepo.ClearReceivedCalls();

        _timeProvider.Advance(AlertConstants.FailedSshLoginWindow);
        SetupCount(windowMinutes: 5, count: 30);

        bool stillLive = await service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), CancellationToken.None);

        await Assert.That(stillLive).IsTrue();
        await Assert.That(created.Count).IsEqualTo(1);
        await _machineStateRepo.DidNotReceive().GetTelemetryPayloadsWithMarkerAsync(
            Arg.Any<long>(), Arg.Any<short>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _alertEventRepo.DidNotReceive().CreateEventIfNotExistsAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>());
    }
}
