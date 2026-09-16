// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Test.Infrastructure;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Services.Alerts;

/// <summary>Tests the windowed failed-SSH-login evaluation job and its re-arming chain.</summary>
public class FailedSshLoginWindowJobTests
{
    private const int TenantId = 7;
    private const long MachineId = 42;
    private const string ChainToken = "chain-token";

    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));

    private (FailedSshLoginWindowJob Job, IEventAlertService Service, IFailedSshLoginChainGate Gate, IBackgroundJobClient Jobs) Create(
        ResilienceMetrics? resilienceMetrics = null)
    {
        IEventAlertService service = Substitute.For<IEventAlertService>();
        IFailedSshLoginChainGate gate = Substitute.For<IFailedSshLoginChainGate>();
        gate.RenewChainAsync(Arg.Any<long>(), Arg.Any<string>()).Returns(true);
        IBackgroundJobClient jobs = Substitute.For<IBackgroundJobClient>();

        FailedSshLoginWindowJob job = new(
            service,
            gate,
            jobs,
            resilienceMetrics ?? TestMetricsFactory.CreateResilienceMetrics(),
            _timeProvider,
            NullLogger<FailedSshLoginWindowJob>.Instance);

        return (job, service, gate, jobs);
    }

    [Test]
    public async Task RunAsync_EvaluatesTheMachinesWindowFromWhenItOpened()
    {
        (FailedSshLoginWindowJob job, IEventAlertService service, IFailedSshLoginChainGate _, IBackgroundJobClient _) = Create();
        DateTimeOffset windowOpenedAt = _timeProvider.GetUtcNow();
        service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, windowOpenedAt, Arg.Any<CancellationToken>()).Returns(false);

        await job.RunAsync(TenantId, MachineId, windowOpenedAt, ChainToken, CancellationToken.None);

        await service.Received(1).EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, windowOpenedAt, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenTheWindowStillHasFailures_SchedulesASuccessorOnTheSameLease()
    {
        // A fire can only happen while no unresolved event exists for the rule and machine, so a chain
        // that stops re-arming leaves the event Triggered forever and the de-dupe swallows every later
        // incident. The successor is what eventually reaches a quiet window and resolves.
        (FailedSshLoginWindowJob job, IEventAlertService service, IFailedSshLoginChainGate gate, IBackgroundJobClient jobs) = Create();
        DateTimeOffset windowOpenedAt = _timeProvider.GetUtcNow().AddMinutes(-AlertConstants.FailedSshLoginWindowMinutes);
        service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, windowOpenedAt, Arg.Any<CancellationToken>()).Returns(true);

        DateTimeOffset runAt = _timeProvider.GetUtcNow();

        await job.RunAsync(TenantId, MachineId, windowOpenedAt, ChainToken, CancellationToken.None);

        await gate.Received(1).RenewChainAsync(MachineId, ChainToken);
        jobs.Received(1).Create(
            Arg.Is<Job>(scheduled => (scheduled.Method.Name == nameof(FailedSshLoginWindowJob.RunAsync))
                && ((int)scheduled.Args[0] == TenantId)
                && ((long)scheduled.Args[1] == MachineId)
                && ((DateTimeOffset)scheduled.Args[2] == runAt)
                && ((string)scheduled.Args[3] == ChainToken)),
            Arg.Is<IState>(state => state is ScheduledState));
    }

    [Test]
    public async Task RunAsync_WhenTheWindowIsQuiet_ReleasesTheLeaseAndEndsTheChain()
    {
        (FailedSshLoginWindowJob job, IEventAlertService service, IFailedSshLoginChainGate gate, IBackgroundJobClient jobs) = Create();
        service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);

        await job.RunAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), ChainToken, CancellationToken.None);

        await gate.Received(1).ReleaseChainAsync(MachineId, ChainToken);
        await gate.DidNotReceive().RenewChainAsync(Arg.Any<long>(), Arg.Any<string>());
        jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    /// <summary>
    /// The regression that keeps a machine to one chain. Ingest and the re-arm both ask the same
    /// lease, so a run whose lease has been taken over by a newer chain must stand down instead of
    /// scheduling a successor alongside it — otherwise live chains grow by one per window for as long
    /// as failures keep arriving, which is the critical-queue flood the lease exists to prevent.
    /// </summary>
    [Test]
    public async Task RunAsync_WhenAnotherChainHoldsTheLease_SchedulesNoSuccessor()
    {
        (FailedSshLoginWindowJob job, IEventAlertService service, IFailedSshLoginChainGate gate, IBackgroundJobClient jobs) = Create();
        service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
        gate.RenewChainAsync(MachineId, ChainToken).Returns(false);

        await job.RunAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), ChainToken, CancellationToken.None);

        jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
        await gate.DidNotReceive().ReleaseChainAsync(Arg.Any<long>(), Arg.Any<string>());
    }

    /// <summary>
    /// A stranded chain is a permanent hole — the open event would never resolve and de-duplication
    /// would swallow every later incident — so an unreachable lease re-arms anyway and says so.
    /// </summary>
    [Test]
    public async Task RunAsync_WhenTheLeaseIsUnreachable_RearmsAnywayAndRecordsFailOpen()
    {
        IMeterFactory factory = TestMetricsFactory.CreateMeterFactory();
        ResilienceMetrics metrics = new(factory);
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.redis.fail_open");

        (FailedSshLoginWindowJob job, IEventAlertService service, IFailedSshLoginChainGate gate, IBackgroundJobClient jobs) = Create(metrics);
        service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
        gate.RenewChainAsync(MachineId, ChainToken)
            .Returns<bool>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Connection refused"));

        await job.RunAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), ChainToken, CancellationToken.None);

        jobs.Received(1).Create(
            Arg.Is<Job>(scheduled => scheduled.Method.Name == nameof(FailedSshLoginWindowJob.RunAsync)),
            Arg.Is<IState>(state => state is ScheduledState));

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["component"]).IsEqualTo("ssh_failure_window");
    }

    /// <summary>
    /// The lease carries its own expiry, so failing to release it is not worth failing the job for —
    /// the next incident simply waits the lease out rather than starting at once.
    /// </summary>
    [Test]
    public async Task RunAsync_WhenReleasingTheLeaseFails_DoesNotThrow()
    {
        (FailedSshLoginWindowJob job, IEventAlertService service, IFailedSshLoginChainGate gate, IBackgroundJobClient jobs) = Create();
        service.EvaluateFailedSshLoginWindowAsync(TenantId, MachineId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);
        gate.ReleaseChainAsync(MachineId, ChainToken)
            .Returns<Task>(_ => throw new RedisTimeoutException("timeout", CommandStatus.WaitingInBacklog));

        await job.RunAsync(TenantId, MachineId, _timeProvider.GetUtcNow(), ChainToken, CancellationToken.None);

        jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    [Test]
    [Arguments("failed", true)]
    [Arguments("FAILED", true)]
    [Arguments("connect", false)]
    [Arguments("disconnect", false)]
    public async Task IsFailedAction_RecognisesTheAgentsFailedAuthAction(string action, bool expected)
    {
        await Assert.That(FailedSshLoginWindowJob.IsFailedAction(action)).IsEqualTo(expected);
    }

    [Test]
    public async Task FailedAction_IsNotEvaluatedByThePerItemJob()
    {
        // The per-item job's action set must never be widened to include failed attempts; under brute
        // force that is one Postgres-backed job per attempt.
        await Assert.That(SshAlertEvaluationJob.IsEvaluatedAction(FailedSshLoginWindowJob.FailedAction)).IsFalse();
    }
}
