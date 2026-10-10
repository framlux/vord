// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics.Metrics;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Dashboard;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="DashboardHandler"/>.
/// </summary>
public class DashboardHandlerTests
{
    [Test]
    public async Task GetSummaryAsync_EmptyFleet_ReturnsAllZeros()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = CreateRepo(dbFactory);
        DashboardHandler handler = new(repo, repo, TestQuotaPause.Nothing(), TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<DashboardHandler>.Instance);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.TotalMachines).IsEqualTo(0);
        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(0);
        await Assert.That(result.Data!.PendingApprovals).IsEqualTo(0);
    }

    [Test]
    public async Task GetSummaryAsync_WithMachines_CountsOnlineFromTheSweptHealthStatus()
    {
        using TestDatabaseFactory dbFactory = new();

        // Create 2 active machines and 1 deleted
        Machine m1 = TestDataBuilder.BuildMachine(tenantId: 1);
        m1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m1);

        Machine m2 = TestDataBuilder.BuildMachine(tenantId: 1);
        m2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m2);

        Machine m3 = TestDataBuilder.BuildMachine(tenantId: 1);
        m3.IsDeleted = true;
        m3.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m3);

        // Only m1 is online: the sweep left it Healthy while m2 was declared Offline.
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
            machineId: m1.Id, tenantId: 1, healthStatus: 0));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
            machineId: m2.Id, tenantId: 1, healthStatus: 3));

        DatabaseRepository repo = CreateRepo(dbFactory);
        DashboardHandler handler = new(repo, repo, TestQuotaPause.Nothing(), TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<DashboardHandler>.Instance);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.TotalMachines).IsEqualTo(2); // Excludes deleted
        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(1); // Only m1
    }

    [Test]
    public async Task GetSummaryAsync_WarningAndCriticalMachines_CountAsOnline()
    {
        // Offline is the only status that means "we have not heard from it"; a degraded machine
        // is still online, and the count must not treat unhealthy as unreachable.
        using TestDatabaseFactory dbFactory = new();

        Machine warning = TestDataBuilder.BuildMachine(tenantId: 1);
        warning.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(warning);

        Machine critical = TestDataBuilder.BuildMachine(tenantId: 1);
        critical.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(critical);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
            machineId: warning.Id, tenantId: 1, healthStatus: 1));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
            machineId: critical.Id, tenantId: 1, healthStatus: 2));

        DatabaseRepository repo = CreateRepo(dbFactory);
        DashboardHandler handler = new(repo, repo, TestQuotaPause.Nothing(), TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<DashboardHandler>.Instance);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(2);
    }

    [Test]
    public async Task GetSummaryAsync_MachineWithNoSummaryRow_CountsAsOffline()
    {
        // A machine that has never been heard from has no summary row, and every read path
        // treats that absence as offline.
        using TestDatabaseFactory dbFactory = new();

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        DatabaseRepository repo = CreateRepo(dbFactory);
        DashboardHandler handler = new(repo, repo, TestQuotaPause.Nothing(), TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<DashboardHandler>.Instance);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.TotalMachines).IsEqualTo(1);
        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(0);
    }

    [Test]
    public async Task GetSummaryAsync_PendingApprovals_ReturnsZero_FeatureNotYetImplemented()
    {
        // Pending approvals are not yet implemented in the dashboard summary.
        // This test documents that the field always returns zero until the approval
        // workflow feature is built.
        using TestDatabaseFactory dbFactory = new();

        Machine m1 = TestDataBuilder.BuildMachine(tenantId: 1);
        m1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m1);

        DatabaseRepository repo = CreateRepo(dbFactory);
        DashboardHandler handler = new(repo, repo, TestQuotaPause.Nothing(), TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<DashboardHandler>.Instance);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.PendingApprovals).IsEqualTo(0);
    }

    [Test]
    public async Task GetSummaryAsync_PausedMachines_AreCountedAsPausedAndNeverAsOnline()
    {
        // A paused host keeps heartbeating and keeps the health it froze at, so by liveness alone
        // it would still count as online. The total still includes it.
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = [];
        foreach (short health in new short[] { 0, 2, 1, 0 })
        {
            Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
            machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
            await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
                machineId: machine.Id, tenantId: 1, healthStatus: health));
            ids.Add(machine.Id);
        }

        DatabaseRepository repo = CreateRepo(dbFactory);
        DashboardHandler handler = new(repo, repo, TestQuotaPause.With(QuotaCutoff.After(ids[1]), 2), TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<DashboardHandler>.Instance);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.TotalMachines).IsEqualTo(4);
        await Assert.That(result.Data!.PausedMachines).IsEqualTo(2);
        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(2);
    }

    [Test]
    public async Task GetSummaryAsync_EveryMachinePaused_CountsNoneOnline()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, tenantId: 1, healthStatus: 0));

        DatabaseRepository repo = CreateRepo(dbFactory);
        DashboardHandler handler = new(repo, repo, TestQuotaPause.With(QuotaCutoff.All, 1), TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<DashboardHandler>.Instance);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(0);
        await Assert.That(result.Data!.PausedMachines).IsEqualTo(1);
    }

    [Test]
    public async Task GetSummaryAsync_PauseDecisionUnavailable_ShowsNothingPausedAndCountsOneFailure()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, tenantId: 1, healthStatus: 0));

        IMeterFactory meterFactory = TestMetricsFactory.CreateMeterFactory();
        using MetricCollector<long> failures = new(meterFactory, VordMeter.Name, "vord.quota.pause_check_failures");
        DatabaseRepository repo = CreateRepo(dbFactory);
        DashboardHandler handler = new(repo, repo, TestQuotaPause.Unavailable(), new QuotaPauseMetrics(meterFactory), NullLogger<DashboardHandler>.Instance);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        // The dashboard must still render: with no answer every host counts as it did before pausing.
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(1);
        await Assert.That(result.Data!.PausedMachines).IsEqualTo(0);

        // One check decides the whole summary, so an outage is one failure, not one per count.
        IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["surface"]).IsEqualTo("display");
    }

    [Test]
    public async Task GetSummaryAsync_PausedCountComesFromTheCutoffOverTheLoadedMachines_NotFromTheLiveCount()
    {
        // The cutoff can be up to a cache lifetime old while the live count follows the limit as it is
        // now. Both counts must come from the one cutoff, or online plus paused can exceed the total.
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = [];
        for (int i = 0; i < 4; i++)
        {
            Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
            machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
            await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
                machineId: machine.Id, tenantId: 1, healthStatus: 0));
            ids.Add(machine.Id);
        }

        DatabaseRepository repo = CreateRepo(dbFactory);
        IQuotaPauseService quotaPause = TestQuotaPause.With(QuotaCutoff.After(ids[1]), pausedCount: 4);
        DashboardHandler handler = new(repo, repo, quotaPause, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<DashboardHandler>.Instance);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        DashboardSummaryDto summary = result.Data!;
        await Assert.That(summary.PausedMachines).IsEqualTo(2);
        await Assert.That(summary.OnlineMachines).IsEqualTo(2);
        await Assert.That(summary.OnlineMachines + summary.PausedMachines).IsLessThanOrEqualTo(summary.TotalMachines);
        await quotaPause.DidNotReceive().GetPausedCountAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Constructor_NullQuotaPause_Throws()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = CreateRepo(dbFactory);

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            _ = new DashboardHandler(repo, repo, null!, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<DashboardHandler>.Instance);

            return Task.CompletedTask;
        });

        await Assert.That(ex!.ParamName).IsEqualTo("quotaPause");
    }

    [Test]
    public async Task Constructor_NullQuotaPauseMetrics_Throws()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = CreateRepo(dbFactory);

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            _ = new DashboardHandler(repo, repo, TestQuotaPause.Nothing(), null!, NullLogger<DashboardHandler>.Instance);

            return Task.CompletedTask;
        });

        await Assert.That(ex!.ParamName).IsEqualTo("quotaPauseMetrics");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = CreateRepo(dbFactory);

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            _ = new DashboardHandler(repo, repo, TestQuotaPause.Nothing(), TestMetricsFactory.CreateQuotaPauseMetrics(), null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    // ========== Helper methods ==========

    private static DatabaseRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }
}
