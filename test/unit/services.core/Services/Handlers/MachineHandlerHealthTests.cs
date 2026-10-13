// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Models;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Pins that the machine list reports each machine's swept health, so a machine that is online but
/// Critical reads as Critical on the list exactly as it does on its detail page and the dashboard.
/// </summary>
public sealed class MachineHandlerHealthTests
{
    private static readonly DateTimeOffset SeededLastSeen = new(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);

    private static MachineHandler CreateHandler(TestDatabaseFactory dbFactory, IQuotaPauseService? quotaPause = null)
    {
        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        return new MachineHandler(
            repo, repo, repo, repo, repo,
            Substitute.For<IMachineBillingSync>(),
            Substitute.For<IApiKeyCacheInvalidator>(),
            new QuotaCutoffInvalidator(FakeRedisConnection.Create(), NullLogger<QuotaCutoffInvalidator>.Instance),
            quotaPause ?? TestQuotaPause.Nothing(),
            TestMetricsFactory.CreateQuotaPauseMetrics(),
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
                    machineId: machine.Id, healthStatus: healthStatus.Value, lastSeenAt: SeededLastSeen));
            }

            ids.Add(machine.Id);
        }

        return ids;
    }

    private static async Task<long> SeedNamedMachine(
        TestDatabaseFactory dbFactory,
        string name,
        OperatingSystems operatingSystem,
        MachineTypes machineType,
        short healthStatus)
    {
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: name);
        machine.OperatingSystem = operatingSystem;
        machine.MachineType = machineType;
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, name: name, healthStatus: healthStatus, lastSeenAt: SeededLastSeen));

        return machine.Id;
    }

    private static Task<ServiceResult<PaginatedResponse<MachineDto>>> ListAsync(
        MachineHandler handler,
        string? status,
        string? search = null,
        string? os = null,
        string? type = null,
        int page = 1,
        int pageSize = 25)
    {
        return handler.ListAsync(page, pageSize, 1, search, os, type, status, "name", "asc", CancellationToken.None);
    }

    private static List<long> IdsOf(ServiceResult<PaginatedResponse<MachineDto>> result)
    {
        return result.Data!.Items.Select(m => m.Id).Order().ToList();
    }

    [Test]
    public async Task ListAsync_Unfiltered_OnlineCriticalMachine_ReportsCriticalWhileStillOnline()
    {
        // The owner-reported case: the detail page said Critical while the list said Online.
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedMachines(dbFactory, 2);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await ListAsync(handler, null);

        MachineDto machine = result.Data!.Items.Single(m => m.Id == ids[0]);
        await Assert.That(machine.HealthStatus).IsEqualTo(MachineHealthStatus.Critical);
        await Assert.That(machine.IsOnline).IsTrue();
    }

    [Test]
    public async Task ListAsync_Unfiltered_MapsEverySweptHealthValueAndReadsAMissingSummaryAsOffline()
    {
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedMachines(dbFactory, 0, 1, 2, 3, null);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await ListAsync(handler, null);

        Dictionary<long, MachineDto> byId = result.Data!.Items.ToDictionary(m => m.Id);
        await Assert.That(byId[ids[0]].HealthStatus).IsEqualTo(MachineHealthStatus.Healthy);
        await Assert.That(byId[ids[1]].HealthStatus).IsEqualTo(MachineHealthStatus.Warning);
        await Assert.That(byId[ids[2]].HealthStatus).IsEqualTo(MachineHealthStatus.Critical);
        await Assert.That(byId[ids[3]].HealthStatus).IsEqualTo(MachineHealthStatus.Offline);
        await Assert.That(byId[ids[4]].HealthStatus).IsEqualTo(MachineHealthStatus.Offline);
    }

    [Test]
    [Arguments("healthy", 0)]
    [Arguments("warning", 1)]
    [Arguments("critical", 2)]
    public async Task ListAsync_HealthFilter_ReturnsOnlyMachinesWithThatSweptHealth(string status, int machineIndex)
    {
        // One machine per health, plus one that has never reported. Each health name selects only
        // the machine carrying that health, and the total counts only it.
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedMachines(dbFactory, 0, 1, 2, 3, null);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await ListAsync(handler, status);

        await Assert.That(IdsOf(result)).IsEquivalentTo(new List<long> { ids[machineIndex] });
        await Assert.That(result.Data!.TotalCount).IsEqualTo(1);
    }

    [Test]
    public async Task ListAsync_OfflineFilter_ReturnsSweptOfflineAndNeverReportedMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedMachines(dbFactory, 0, 1, 2, 3, null);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await ListAsync(handler, "offline");

        await Assert.That(IdsOf(result)).IsEquivalentTo(new List<long> { ids[3], ids[4] });
        await Assert.That(result.Data!.TotalCount).IsEqualTo(2);
        await Assert.That(result.Data!.Items.All(m => m.HealthStatus == MachineHealthStatus.Offline)).IsTrue();
    }

    [Test]
    public async Task ListAsync_OnlineFilter_ReturnsEveryHealthButOffline()
    {
        // Legacy bookmarks still use online: not paused, and not swept Offline.
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedMachines(dbFactory, 0, 1, 2, 3, null);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await ListAsync(handler, "online");

        await Assert.That(IdsOf(result)).IsEquivalentTo(new List<long> { ids[0], ids[1], ids[2] });
        await Assert.That(result.Data!.TotalCount).IsEqualTo(3);
    }

    [Test]
    public async Task ListAsync_HealthFilter_IgnoresCaseOfTheStatusName()
    {
        // The web sends capitalised names, a hand-typed or bookmarked URL may not.
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedMachines(dbFactory, 0, 2);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> capitalised = await ListAsync(handler, "Critical");
        ServiceResult<PaginatedResponse<MachineDto>> shouting = await ListAsync(handler, "CRITICAL");

        await Assert.That(IdsOf(capitalised)).IsEquivalentTo(new List<long> { ids[1] });
        await Assert.That(IdsOf(shouting)).IsEquivalentTo(new List<long> { ids[1] });
    }

    [Test]
    public async Task ListAsync_HealthFilter_RowsCarryTheHealthTheyWereFilteredBy()
    {
        // A filtered row must show the same badge as the filter that matched it.
        using TestDatabaseFactory dbFactory = new();
        await SeedMachines(dbFactory, 0, 2, 2, 3);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await ListAsync(handler, "critical");

        await Assert.That(result.Data!.Items.Count).IsEqualTo(2);
        await Assert.That(result.Data!.Items.All(m => m.HealthStatus == MachineHealthStatus.Critical)).IsTrue();
        await Assert.That(result.Data!.Items.All(m => m.IsOnline)).IsTrue();
    }

    [Test]
    public async Task ListAsync_PausedCriticalMachine_KeepsItsHealthInTheDtoButMatchesOnlyThePausedFilter()
    {
        // Past the limit the machine is Paused on screen, whatever it last reported. The DTO still
        // carries the frozen value so the client has it, but no health filter may return the row.
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedMachines(dbFactory, 0, 2, 2);
        MachineHandler handler = CreateHandler(dbFactory, TestQuotaPause.With(QuotaCutoff.After(ids[1]), 1));

        ServiceResult<PaginatedResponse<MachineDto>> critical = await ListAsync(handler, "critical");
        ServiceResult<PaginatedResponse<MachineDto>> online = await ListAsync(handler, "online");
        ServiceResult<PaginatedResponse<MachineDto>> paused = await ListAsync(handler, "paused");
        ServiceResult<PaginatedResponse<MachineDto>> unfiltered = await ListAsync(handler, null);

        await Assert.That(IdsOf(critical)).IsEquivalentTo(new List<long> { ids[1] });
        await Assert.That(critical.Data!.TotalCount).IsEqualTo(1);
        await Assert.That(IdsOf(online)).IsEquivalentTo(new List<long> { ids[0], ids[1] });
        await Assert.That(IdsOf(paused)).IsEquivalentTo(new List<long> { ids[2] });
        await Assert.That(paused.Data!.TotalCount).IsEqualTo(1);

        MachineDto pausedDto = paused.Data!.Items.Single();
        await Assert.That(pausedDto.IsPaused).IsTrue();
        await Assert.That(pausedDto.HealthStatus).IsEqualTo(MachineHealthStatus.Critical);
        await Assert.That(unfiltered.Data!.Items.Single(m => m.Id == ids[2]).HealthStatus).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task ListAsync_HealthFilter_CombinesWithSearchOsAndTypeAndCountsOnlyTheMatches()
    {
        using TestDatabaseFactory dbFactory = new();
        long webCritical = await SeedNamedMachine(dbFactory, "web-critical", OperatingSystems.Ubuntu, MachineTypes.BareMetalServer, 2);
        await SeedNamedMachine(dbFactory, "web-healthy", OperatingSystems.Ubuntu, MachineTypes.BareMetalServer, 0);
        await SeedNamedMachine(dbFactory, "db-critical", OperatingSystems.Ubuntu, MachineTypes.BareMetalServer, 2);
        await SeedNamedMachine(dbFactory, "web-critical-fedora", OperatingSystems.Fedora, MachineTypes.BareMetalServer, 2);
        await SeedNamedMachine(dbFactory, "web-critical-vm", OperatingSystems.Ubuntu, MachineTypes.VirtualMachine, 2);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await ListAsync(
            handler, "critical", search: "web", os: "Ubuntu", type: "BareMetalServer");
        ServiceResult<PaginatedResponse<MachineDto>> criticalOnly = await ListAsync(handler, "critical");

        await Assert.That(IdsOf(result)).IsEquivalentTo(new List<long> { webCritical });
        await Assert.That(result.Data!.TotalCount).IsEqualTo(1);
        await Assert.That(criticalOnly.Data!.TotalCount).IsEqualTo(4);
    }

    [Test]
    public async Task ListAsync_HealthFilter_PagesTheMatchesAndTotalsThemAcrossPages()
    {
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedMachines(dbFactory, 2, 0, 2, 2, 1);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> firstPage = await ListAsync(handler, "critical", page: 1, pageSize: 2);
        ServiceResult<PaginatedResponse<MachineDto>> secondPage = await ListAsync(handler, "critical", page: 2, pageSize: 2);

        await Assert.That(firstPage.Data!.TotalCount).IsEqualTo(3);
        await Assert.That(secondPage.Data!.TotalCount).IsEqualTo(3);
        await Assert.That(firstPage.Data!.Items.Count).IsEqualTo(2);
        await Assert.That(secondPage.Data!.Items.Count).IsEqualTo(1);
        await Assert.That(IdsOf(firstPage).Concat(IdsOf(secondPage)).Order().ToList())
            .IsEquivalentTo(new List<long> { ids[0], ids[2], ids[3] });
    }

    [Test]
    [Arguments("all")]
    [Arguments("degraded")]
    [Arguments("")]
    public async Task ListAsync_UnrecognisedStatus_AppliesNoFilter(string status)
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachines(dbFactory, 0, 1, 2, 3, null);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await ListAsync(handler, status);

        await Assert.That(result.Data!.Items.Count).IsEqualTo(5);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(5);
    }
}
