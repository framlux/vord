// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Models;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Pins how <see cref="MachineHandler"/> shows a host past its tenant's machine limit: as paused,
/// never as online or offline, and never as an error when the pause decision cannot be made.
/// </summary>
public sealed class MachineHandlerPausedTests
{
    private static MachineHandler CreateHandler(TestDatabaseFactory dbFactory, IQuotaPauseService quotaPause, QuotaPauseMetrics? metrics = null)
    {
        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        return new MachineHandler(
            repo, repo, repo, repo, repo,
            Substitute.For<IMachineBillingSync>(),
            Substitute.For<IApiKeyCacheInvalidator>(),
            new QuotaCutoffInvalidator(FakeRedisConnection.Create(), NullLogger<QuotaCutoffInvalidator>.Instance),
            quotaPause,
            metrics ?? TestMetricsFactory.CreateQuotaPauseMetrics(),
            NullLogger<MachineHandler>.Instance);
    }

    // Ids ascend in step with the statuses; a null status leaves the machine with no summary row.
    private static async Task<List<long>> SeedMachines(TestDatabaseFactory dbFactory, params short?[] healthStatuses)
    {
        List<long> ids = [];
        foreach (short? healthStatus in healthStatuses)
        {
            Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
            machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
            if (healthStatus is not null)
            {
                await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
                    machineId: machine.Id, healthStatus: healthStatus.Value, lastSeenAt: DateTimeOffset.UtcNow));
            }

            ids.Add(machine.Id);
        }

        return ids;
    }

    private static Task<ServiceResult<PaginatedResponse<MachineDto>>> ListAsync(MachineHandler handler, string? status, int page = 1, int pageSize = 25)
    {
        return handler.ListAsync(page, pageSize, 1, null, null, null, status, "name", "asc", CancellationToken.None);
    }

    [Test]
    public async Task ListAsync_Unfiltered_MarksOnlyMachinesPastTheCutoffAsPaused()
    {
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedMachines(dbFactory, 0, 0, 0, 3);
        MachineHandler handler = CreateHandler(dbFactory, TestQuotaPause.With(QuotaCutoff.After(ids[2]), 1));

        ServiceResult<PaginatedResponse<MachineDto>> result = await ListAsync(handler, null);

        Dictionary<long, MachineDto> byId = result.Data!.Items.ToDictionary(m => m.Id);
        await Assert.That(byId[ids[0]].IsPaused).IsFalse();
        await Assert.That(byId[ids[2]].IsPaused).IsFalse();
        await Assert.That(byId[ids[3]].IsPaused).IsTrue();
    }

    [Test]
    public async Task ListAsync_AllPausedCutoff_MarksEveryMachineAsPaused()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachines(dbFactory, 0, 2);
        MachineHandler handler = CreateHandler(dbFactory, TestQuotaPause.With(QuotaCutoff.All, 2));

        ServiceResult<PaginatedResponse<MachineDto>> result = await ListAsync(handler, null);

        await Assert.That(result.Data!.Items.All(m => m.IsPaused)).IsTrue();
    }

    [Test]
    public async Task ListAsync_OnlineFilter_LeavesOutAPausedMachineThatIsStillHeartbeating()
    {
        // A paused host keeps its heartbeat and its last health, so liveness alone calls it online.
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedMachines(dbFactory, 0, 0, 0);
        MachineHandler handler = CreateHandler(dbFactory, TestQuotaPause.With(QuotaCutoff.After(ids[1]), 1));

        ServiceResult<PaginatedResponse<MachineDto>> result = await ListAsync(handler, "online");

        await Assert.That(result.Data!.Items.Select(m => m.Id).Order().ToList()).IsEquivalentTo(new List<long> { ids[0], ids[1] });
        await Assert.That(result.Data!.TotalCount).IsEqualTo(2);
    }

    [Test]
    public async Task ListAsync_OfflineFilter_LeavesOutPausedMachinesWhetherOrNotTheyHaveASummary()
    {
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedMachines(dbFactory, 3, 3, null);
        MachineHandler handler = CreateHandler(dbFactory, TestQuotaPause.With(QuotaCutoff.After(ids[0]), 2));

        ServiceResult<PaginatedResponse<MachineDto>> result = await ListAsync(handler, "offline");

        await Assert.That(result.Data!.Items.Select(m => m.Id).ToList()).IsEquivalentTo(new List<long> { ids[0] });
        await Assert.That(result.Data!.TotalCount).IsEqualTo(1);
    }

    [Test]
    public async Task ListAsync_PausedFilter_ReturnsOnlyPausedMachinesAndCountsOnlyThem()
    {
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedMachines(dbFactory, 0, 0, 0, 3, null);
        MachineHandler handler = CreateHandler(dbFactory, TestQuotaPause.With(QuotaCutoff.After(ids[2]), 2));

        ServiceResult<PaginatedResponse<MachineDto>> firstPage = await ListAsync(handler, "Paused", page: 1, pageSize: 1);
        ServiceResult<PaginatedResponse<MachineDto>> secondPage = await ListAsync(handler, "paused", page: 2, pageSize: 1);

        // The filter is applied before paging, so the total is the paused set and each page holds one of it.
        await Assert.That(firstPage.Data!.TotalCount).IsEqualTo(2);
        await Assert.That(firstPage.Data!.Items.Count).IsEqualTo(1);
        await Assert.That(secondPage.Data!.Items.Count).IsEqualTo(1);
        await Assert.That(new[] { firstPage.Data.Items[0].Id, secondPage.Data.Items[0].Id }.Order().ToList())
            .IsEquivalentTo(new List<long> { ids[3], ids[4] });
    }

    [Test]
    public async Task ListAsync_PausedFilter_WithNothingPaused_ReturnsNothing()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachines(dbFactory, 0, 3);
        MachineHandler handler = CreateHandler(dbFactory, TestQuotaPause.Nothing());

        ServiceResult<PaginatedResponse<MachineDto>> result = await ListAsync(handler, "paused");

        await Assert.That(result.Data!.Items.Count).IsEqualTo(0);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(0);
    }

    [Test]
    public async Task ListAsync_PauseDecisionUnavailable_ShowsEveryMachineAsActiveAndCountsTheFailureOnce()
    {
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedMachines(dbFactory, 0, 3);
        IMeterFactory meterFactory = TestMetricsFactory.CreateMeterFactory();
        using MetricCollector<long> failures = new(meterFactory, VordMeter.Name, "vord.quota.pause_check_failures");
        MachineHandler handler = CreateHandler(dbFactory, TestQuotaPause.Unavailable(), new QuotaPauseMetrics(meterFactory));

        ServiceResult<PaginatedResponse<MachineDto>> result = await ListAsync(handler, "offline");

        // An outage must not hide hosts or fail the page: they read as active, as they did before.
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Select(m => m.Id).ToList()).IsEquivalentTo(new List<long> { ids[1] });
        await Assert.That(result.Data!.Items[0].IsPaused).IsFalse();

        IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["surface"]).IsEqualTo("display");
    }

    [Test]
    public async Task UpdateAsync_ResponseCarriesThePausedStateOfTheUpdatedMachine()
    {
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedMachines(dbFactory, 0, 0);
        MachineHandler handler = CreateHandler(dbFactory, TestQuotaPause.With(QuotaCutoff.After(ids[0]), 1));

        ServiceResult<ApiResponse<MachineDto>> active = await handler.UpdateAsync(ids[0], 1, 1, "renamed-active", null, null, CancellationToken.None);
        ServiceResult<ApiResponse<MachineDto>> paused = await handler.UpdateAsync(ids[1], 1, 1, "renamed-paused", null, null, CancellationToken.None);

        await Assert.That(active.Data!.Data!.IsPaused).IsFalse();
        await Assert.That(paused.Data!.Data!.IsPaused).IsTrue();
    }
}
