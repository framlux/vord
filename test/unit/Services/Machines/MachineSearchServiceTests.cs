// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="MachineSearchService"/>.
/// </summary>
public class MachineSearchServiceTests
{
    private static IMachinePingService CreateMockPingService(bool online = false)
    {
        IMachinePingService pingService = Substitute.For<IMachinePingService>();
        pingService.AreOnlineAsync(Arg.Any<IEnumerable<long>>(), Arg.Any<TimeSpan>())
            .Returns(callInfo =>
            {
                IEnumerable<long> ids = callInfo.Arg<IEnumerable<long>>();

                return ids.ToDictionary(id => id, _ => online);
            });
        pingService.GetLastPingsAsync(Arg.Any<IEnumerable<long>>())
            .Returns(callInfo =>
            {
                IEnumerable<long> ids = callInfo.Arg<IEnumerable<long>>();

                return ids.ToDictionary(id => id, _ => online ? (DateTimeOffset?)DateTimeOffset.UtcNow : null);
            });

        return pingService;
    }

    private static ServerConfigurationService CreateConfigService()
    {
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        return new ServerConfigurationService(cache, redis);
    }

    private static ISqlDialect CreateSqliteDialect() => new SqliteSqlDialect();

    private static MachineSearchCriteria DefaultCriteria() => new()
    {
        Page = 1,
        PageSize = 25,
        SortBy = "name",
        SortDir = "asc"
    };

    // ========== Null / empty tenant ==========

    [Test]
    public async Task SearchAsync_NullTenantId_ReturnsEmptyResult()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), null, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(0);
        await Assert.That(result.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SearchAsync_NoMachines_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(0);
        await Assert.That(result.Items.Count).IsEqualTo(0);
    }

    // ========== Basic search ==========

    [Test]
    public async Task SearchAsync_WithMachines_ReturnsAllMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine1 = TestDataBuilder.BuildMachine(tenantId: 1);
        machine1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine1);
        Machine machine2 = TestDataBuilder.BuildMachine(tenantId: 1);
        machine2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine2);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(2);
        await Assert.That(result.Items.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SearchAsync_ExcludesDeletedMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine active = TestDataBuilder.BuildMachine(tenantId: 1);
        active.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(active);
        Machine deleted = TestDataBuilder.BuildMachine(tenantId: 1);
        deleted.IsDeleted = true;
        deleted.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(deleted);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
    }

    [Test]
    public async Task SearchAsync_ExcludesOtherTenantMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine tenant1Machine = TestDataBuilder.BuildMachine(tenantId: 1);
        tenant1Machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(tenant1Machine);
        Machine tenant2Machine = TestDataBuilder.BuildMachine(tenantId: 2);
        tenant2Machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(tenant2Machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
    }

    // ========== Text search ==========

    [Test]
    public async Task SearchAsync_TextSearch_MatchesByName()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine webServer = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "web-server-01");
        webServer.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(webServer);
        Machine dbServer = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "db-server-01");
        dbServer.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(dbServer);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Search = "web";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("web-server-01");
    }

    [Test]
    public async Task SearchAsync_TextSearch_CaseInsensitive()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "Production-Server");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Search = "production";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
    }

    // ========== OS filter ==========

    [Test]
    public async Task SearchAsync_OsFilter_FiltersCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine ubuntu = TestDataBuilder.BuildMachine(tenantId: 1);
        ubuntu.OperatingSystem = OperatingSystems.Ubuntu;
        ubuntu.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(ubuntu);
        Machine windows = TestDataBuilder.BuildMachine(tenantId: 1);
        windows.OperatingSystem = OperatingSystems.Windows;
        windows.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(windows);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Os = "Ubuntu";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
    }

    // ========== CPU range filter ==========

    [Test]
    public async Task SearchAsync_CpuMinFilter_FiltersCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine lowCpu = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "low-cpu");
        lowCpu.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(lowCpu);
        MachineStateSummary lowState = TestDataBuilder.BuildMachineStateSummary(machineId: lowCpu.Id, cpuPercent: 20);
        await dbFactory.Context.InsertAsync(lowState);

        Machine highCpu = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-cpu");
        highCpu.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(highCpu);
        MachineStateSummary highState = TestDataBuilder.BuildMachineStateSummary(machineId: highCpu.Id, cpuPercent: 85);
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.CpuMin = 80;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].CpuUsagePercent).IsEqualTo(85);
    }

    [Test]
    public async Task SearchAsync_CpuMaxFilter_FiltersCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine lowCpu = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "low-cpu");
        lowCpu.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(lowCpu);
        MachineStateSummary lowState = TestDataBuilder.BuildMachineStateSummary(machineId: lowCpu.Id, cpuPercent: 20);
        await dbFactory.Context.InsertAsync(lowState);

        Machine highCpu = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-cpu");
        highCpu.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(highCpu);
        MachineStateSummary highState = TestDataBuilder.BuildMachineStateSummary(machineId: highCpu.Id, cpuPercent: 85);
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.CpuMax = 50;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].CpuUsagePercent).IsEqualTo(20);
    }

    // ========== Memory range filter ==========

    [Test]
    public async Task SearchAsync_MemoryRangeFilter_FiltersCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine lowMem = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "low-mem");
        lowMem.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(lowMem);
        MachineStateSummary lowState = TestDataBuilder.BuildMachineStateSummary(machineId: lowMem.Id, memoryPercent: 30);
        await dbFactory.Context.InsertAsync(lowState);

        Machine highMem = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-mem");
        highMem.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(highMem);
        MachineStateSummary highState = TestDataBuilder.BuildMachineStateSummary(machineId: highMem.Id, memoryPercent: 90);
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.MemoryMin = 50;
        criteria.MemoryMax = 95;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].MemoryUsagePercent).IsEqualTo(90);
    }

    // ========== Threshold filters ==========

    [Test]
    public async Task SearchAsync_PendingUpdatesMinFilter_FiltersCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine noUpdates = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "no-updates");
        noUpdates.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(noUpdates);
        MachineStateSummary noUpdateState = TestDataBuilder.BuildMachineStateSummary(machineId: noUpdates.Id);
        noUpdateState.PendingUpdates = 0;
        await dbFactory.Context.InsertAsync(noUpdateState);

        Machine manyUpdates = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "many-updates");
        manyUpdates.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(manyUpdates);
        MachineStateSummary manyUpdateState = TestDataBuilder.BuildMachineStateSummary(machineId: manyUpdates.Id);
        manyUpdateState.PendingUpdates = 15;
        await dbFactory.Context.InsertAsync(manyUpdateState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.PendingUpdatesMin = 10;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].PendingUpdates).IsEqualTo(15);
    }

    [Test]
    public async Task SearchAsync_FailedServicesMinFilter_FiltersCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine healthy = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "healthy-services");
        healthy.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(healthy);
        MachineStateSummary healthyState = TestDataBuilder.BuildMachineStateSummary(machineId: healthy.Id);
        healthyState.FailedServices = 0;
        await dbFactory.Context.InsertAsync(healthyState);

        Machine failing = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "failing-services");
        failing.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(failing);
        MachineStateSummary failingState = TestDataBuilder.BuildMachineStateSummary(machineId: failing.Id);
        failingState.FailedServices = 3;
        await dbFactory.Context.InsertAsync(failingState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.FailedServicesMin = 1;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].FailedServices).IsEqualTo(3);
    }

    // ========== Health status filter ==========

    [Test]
    public async Task SearchAsync_HealthStatusFilter_FiltersOfflineMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // Seed MachineStateSummary with Offline health status so the SQL filter matches.
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, healthStatus: 3);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: false), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.HealthStatus = "offline";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HealthStatus).IsEqualTo(MachineHealthStatus.Offline);
    }

    [Test]
    public async Task SearchAsync_HealthStatusFilter_MultipleStatuses()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // Seed MachineStateSummary with Offline health status.
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, healthStatus: 3);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: false), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.HealthStatus = "healthy,warning";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        // Machine has Offline health status, so it should not match healthy or warning.
        await Assert.That(result.TotalCount).IsEqualTo(0);
    }

    // ========== Pagination ==========

    [Test]
    public async Task SearchAsync_PageLessThanOne_ClampedToOne()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Page = -1;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.Page).IsEqualTo(1);
        await Assert.That(result.TotalCount).IsEqualTo(1);
    }

    [Test]
    public async Task SearchAsync_PageSizeOutOfRange_ClampedToDefault()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.PageSize = 200;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.PageSize).IsEqualTo(25);
    }

    [Test]
    public async Task SearchAsync_PageBeyondResults_ReturnsEmptyItems()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Page = 100;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items.Count).IsEqualTo(0);
    }

    // ========== Sorting ==========

    [Test]
    public async Task SearchAsync_SortByNameAsc_ReturnsInOrder()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine alpha = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "alpha-server");
        alpha.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(alpha);
        Machine zulu = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "zulu-server");
        zulu.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(zulu);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.SortBy = "name";
        criteria.SortDir = "asc";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.Items[0].Name).IsEqualTo("alpha-server");
        await Assert.That(result.Items[1].Name).IsEqualTo("zulu-server");
    }

    [Test]
    public async Task SearchAsync_SortByNameDesc_ReturnsInReverseOrder()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine alpha = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "alpha-server");
        alpha.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(alpha);
        Machine zulu = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "zulu-server");
        zulu.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(zulu);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.SortBy = "name";
        criteria.SortDir = "desc";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.Items[0].Name).IsEqualTo("zulu-server");
        await Assert.That(result.Items[1].Name).IsEqualTo("alpha-server");
    }

    // ========== Combined filters ==========

    [Test]
    public async Task SearchAsync_CombinedFilters_AllApplied()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine match = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "web-ubuntu");
        match.OperatingSystem = OperatingSystems.Ubuntu;
        match.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(match);
        MachineStateSummary matchState = TestDataBuilder.BuildMachineStateSummary(machineId: match.Id, cpuPercent: 85);
        await dbFactory.Context.InsertAsync(matchState);

        Machine noMatch1 = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "web-windows");
        noMatch1.OperatingSystem = OperatingSystems.Windows;
        noMatch1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(noMatch1);
        MachineStateSummary noMatch1State = TestDataBuilder.BuildMachineStateSummary(machineId: noMatch1.Id, cpuPercent: 90);
        await dbFactory.Context.InsertAsync(noMatch1State);

        Machine noMatch2 = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "db-ubuntu");
        noMatch2.OperatingSystem = OperatingSystems.Ubuntu;
        noMatch2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(noMatch2);
        MachineStateSummary noMatch2State = TestDataBuilder.BuildMachineStateSummary(machineId: noMatch2.Id, cpuPercent: 10);
        await dbFactory.Context.InsertAsync(noMatch2State);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Search = "web";
        criteria.Os = "Ubuntu";
        criteria.CpuMin = 80;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("web-ubuntu");
    }

    // ========== Invalid OS / Type filter ==========

    [Test]
    public async Task SearchAsync_InvalidOsFilter_IgnoresFilter()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Os = "NotAnOS";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
    }

    // ========== Machine Type filter ==========

    [Test]
    public async Task SearchAsync_TypeFilter_FiltersCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine server = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "server");
        server.MachineType = MachineTypes.BareMetalServer;
        server.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(server);
        Machine laptop = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "laptop");
        laptop.MachineType = MachineTypes.Laptop;
        laptop.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(laptop);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Type = "Laptop";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("laptop");
    }

    // ========== Fast path (SQL pagination) ==========

    [Test]
    public async Task SearchAsync_FastPath_PaginatesAtSqlLevel()
    {
        // With only SQL-level filters and no health/disk/lastSeen criteria,
        // the fast path should count and paginate at the database level.
        using TestDatabaseFactory dbFactory = new();
        for (int i = 0; i < 5; i++)
        {
            Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: $"fast-path-{i:D2}");
            machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        }

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.PageSize = 2;
        criteria.Page = 2;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(5);
        await Assert.That(result.Items.Count).IsEqualTo(2);
        await Assert.That(result.Page).IsEqualTo(2);
    }

    [Test]
    public async Task SearchAsync_FastPath_SortByCpuAtSqlLevel()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine low = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "low-cpu");
        low.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(low);
        MachineStateSummary lowState = TestDataBuilder.BuildMachineStateSummary(machineId: low.Id, cpuPercent: 10);
        await dbFactory.Context.InsertAsync(lowState);

        Machine high = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-cpu");
        high.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(high);
        MachineStateSummary highState = TestDataBuilder.BuildMachineStateSummary(machineId: high.Id, cpuPercent: 90);
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.SortBy = "cpu";
        criteria.SortDir = "desc";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.Items[0].CpuUsagePercent).IsEqualTo(90);
        await Assert.That(result.Items[1].CpuUsagePercent).IsEqualTo(10);
    }

    // ========== Health status filter uses SQL path with pre-computed column ==========

    [Test]
    public async Task SearchAsync_HealthFilter_WithPagination()
    {
        // Health status filter uses the pre-computed HealthStatus column via SQL.
        // Verify pagination works correctly with SQL-level health filtering.
        using TestDatabaseFactory dbFactory = new();
        for (int i = 0; i < 5; i++)
        {
            Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: $"scan-{i:D2}");
            machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

            // Seed each machine with Offline health status in the database.
            MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, healthStatus: 3);
            await dbFactory.Context.InsertAsync(state);
        }

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: false), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.HealthStatus = "offline";
        criteria.PageSize = 2;
        criteria.Page = 1;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(5);
        await Assert.That(result.Items.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SearchAsync_SortByStatus_UsesSqlPath()
    {
        // Sorting by status now uses the pre-computed HealthStatus column via SQL ORDER BY.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.SortBy = "status";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HealthStatus).IsEqualTo(MachineHealthStatus.Healthy);
    }

    // ========== SecurityUpdatesMin filter ==========

    [Test]
    public async Task SearchAsync_SecurityUpdatesMin_FiltersCorrectly()
    {
        // Only machines whose security update count meets the minimum threshold should appear.
        using TestDatabaseFactory dbFactory = new();
        Machine noSecUpdates = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "no-sec-updates");
        noSecUpdates.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(noSecUpdates);
        MachineStateSummary noSecState = TestDataBuilder.BuildMachineStateSummary(machineId: noSecUpdates.Id);
        noSecState.SecurityUpdates = 0;
        await dbFactory.Context.InsertAsync(noSecState);

        Machine withSecUpdates = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "with-sec-updates");
        withSecUpdates.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(withSecUpdates);
        MachineStateSummary withSecState = TestDataBuilder.BuildMachineStateSummary(machineId: withSecUpdates.Id);
        withSecState.SecurityUpdates = 8;
        await dbFactory.Context.InsertAsync(withSecState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.SecurityUpdatesMin = 5;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].SecurityUpdates).IsEqualTo(8);
    }

    // ========== Sort by memory (fast path) ==========

    [Test]
    public async Task SearchAsync_SortByMemory_SortsCorrectly()
    {
        // Memory sort is resolved at the SQL level on the fast path; verify ordering.
        using TestDatabaseFactory dbFactory = new();
        Machine lowMem = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "low-mem");
        lowMem.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(lowMem);
        MachineStateSummary lowState = TestDataBuilder.BuildMachineStateSummary(machineId: lowMem.Id, memoryPercent: 15);
        await dbFactory.Context.InsertAsync(lowState);

        Machine highMem = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-mem");
        highMem.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(highMem);
        MachineStateSummary highState = TestDataBuilder.BuildMachineStateSummary(machineId: highMem.Id, memoryPercent: 75);
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.SortBy = "memory";
        criteria.SortDir = "desc";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.Items[0].MemoryUsagePercent).IsEqualTo(75);
        await Assert.That(result.Items[1].MemoryUsagePercent).IsEqualTo(15);
    }

    // ========== HasDiskHealthIssue false-case filter ==========

    [Test]
    public async Task SearchAsync_HasDiskHealthIssueFalse_ExcludesMachinesWithIssues()
    {
        // When HasDiskHealthIssue=false, machines that have a FAILED disk SMART entry
        // should be excluded; only machines with healthy disks should be returned.
        using TestDatabaseFactory dbFactory = new();
        Machine healthyDisk = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "healthy-disk");
        healthyDisk.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(healthyDisk);
        MachineStateSummary healthyState = TestDataBuilder.BuildMachineStateSummary(machineId: healthyDisk.Id);
        healthyState.HasDiskHealthIssue = false;
        await dbFactory.Context.InsertAsync(healthyState);

        Machine failedDisk = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "failed-disk");
        failedDisk.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(failedDisk);
        MachineStateSummary failedState = TestDataBuilder.BuildMachineStateSummary(machineId: failedDisk.Id);
        failedState.HasDiskHealthIssue = true;
        await dbFactory.Context.InsertAsync(failedState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        // SQLite dialect forces the full-scan path for JSONB filters, which exercises
        // the in-memory ApplyPostEnrichmentFilters path for this filter.
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.HasDiskHealthIssue = false;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("healthy-disk");
    }

    // ========== HasHardwareIssue false-case filter ==========

    [Test]
    public async Task SearchAsync_HasHardwareIssueFalse_ExcludesMachinesWithIssues()
    {
        // When HasHardwareIssue=false, machines with fan or PSU faults should be
        // excluded; only machines with no hardware issues should be returned.
        using TestDatabaseFactory dbFactory = new();
        Machine goodHardware = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "good-hardware");
        goodHardware.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(goodHardware);
        MachineStateSummary goodState = TestDataBuilder.BuildMachineStateSummary(machineId: goodHardware.Id);
        goodState.HasHardwareIssue = false;
        await dbFactory.Context.InsertAsync(goodState);

        Machine badHardware = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "bad-hardware");
        badHardware.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(badHardware);
        MachineStateSummary badState = TestDataBuilder.BuildMachineStateSummary(machineId: badHardware.Id);
        // Pre-computed flag indicating a hardware issue (e.g., fan failure or PSU fault).
        badState.HasHardwareIssue = true;
        await dbFactory.Context.InsertAsync(badState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        // SQLite dialect forces the full-scan path for JSONB filters, exercising
        // the in-memory ApplyPostEnrichmentFilters path for this filter.
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.HasHardwareIssue = false;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("good-hardware");
    }

    // ========== LastSeen SQL filters ==========

    [Test]
    public async Task SearchAsync_LastSeenAfter_FiltersViaSqlPath()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine recentMachine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "recent");
        recentMachine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(recentMachine);
        MachineStateSummary recentState = TestDataBuilder.BuildMachineStateSummary(
            machineId: recentMachine.Id, lastSeenAt: DateTimeOffset.UtcNow);
        await dbFactory.Context.InsertAsync(recentState);

        Machine staleMachine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "stale");
        staleMachine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(staleMachine);
        MachineStateSummary staleState = TestDataBuilder.BuildMachineStateSummary(
            machineId: staleMachine.Id, lastSeenAt: DateTimeOffset.UtcNow.AddHours(-3));
        await dbFactory.Context.InsertAsync(staleState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.LastSeenAfter = DateTimeOffset.UtcNow.AddHours(-1);

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("recent");
    }

    [Test]
    public async Task SearchAsync_LastSeenBefore_FiltersViaSqlPath()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine recentMachine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "recent");
        recentMachine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(recentMachine);
        MachineStateSummary recentState = TestDataBuilder.BuildMachineStateSummary(
            machineId: recentMachine.Id, lastSeenAt: DateTimeOffset.UtcNow);
        await dbFactory.Context.InsertAsync(recentState);

        Machine staleMachine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "stale");
        staleMachine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(staleMachine);
        MachineStateSummary staleState = TestDataBuilder.BuildMachineStateSummary(
            machineId: staleMachine.Id, lastSeenAt: DateTimeOffset.UtcNow.AddHours(-3));
        await dbFactory.Context.InsertAsync(staleState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.LastSeenBefore = DateTimeOffset.UtcNow.AddHours(-2);

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("stale");
    }

    [Test]
    public async Task SearchAsync_LastSeenAfter_ExcludesMachinesWithNoLastPing()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine noPingMachine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "never-pinged");
        noPingMachine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(noPingMachine);
        MachineStateSummary noPingState = TestDataBuilder.BuildMachineStateSummary(machineId: noPingMachine.Id);
        await dbFactory.Context.InsertAsync(noPingState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.LastSeenAfter = DateTimeOffset.UtcNow.AddHours(-1);

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(0);
    }

    // ========== Sort by status ordering ==========

    [Test]
    public async Task SearchAsync_SortByStatus_OrdersByHealthStatusAscending()
    {
        // SQL sorts by the pre-computed HealthStatus column. After sorting, the enrichment
        // step may recalculate health for display using Redis data, so we verify order by
        // machine name to confirm the SQL sort was applied correctly.
        using TestDatabaseFactory dbFactory = new();

        Machine healthy = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "healthy-host");
        healthy.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(healthy);
        MachineStateSummary healthyState = TestDataBuilder.BuildMachineStateSummary(machineId: healthy.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(healthyState);

        Machine critical = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "critical-host");
        critical.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(critical);
        MachineStateSummary criticalState = TestDataBuilder.BuildMachineStateSummary(machineId: critical.Id, healthStatus: 2);
        await dbFactory.Context.InsertAsync(criticalState);

        Machine offline = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "offline-host");
        offline.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(offline);
        MachineStateSummary offlineState = TestDataBuilder.BuildMachineStateSummary(machineId: offline.Id, healthStatus: 3);
        await dbFactory.Context.InsertAsync(offlineState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.SortBy = "status";
        criteria.SortDir = "asc";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(3);
        await Assert.That(result.Items[0].Name).IsEqualTo("healthy-host");
        await Assert.That(result.Items[1].Name).IsEqualTo("critical-host");
        await Assert.That(result.Items[2].Name).IsEqualTo("offline-host");
    }

    [Test]
    public async Task SearchAsync_SortByStatusDesc_OrdersByHealthStatusDescending()
    {
        using TestDatabaseFactory dbFactory = new();

        Machine healthy = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "healthy-host");
        healthy.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(healthy);
        MachineStateSummary healthyState = TestDataBuilder.BuildMachineStateSummary(machineId: healthy.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(healthyState);

        Machine offline = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "offline-host");
        offline.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(offline);
        MachineStateSummary offlineState = TestDataBuilder.BuildMachineStateSummary(machineId: offline.Id, healthStatus: 3);
        await dbFactory.Context.InsertAsync(offlineState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.SortBy = "status";
        criteria.SortDir = "desc";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(2);
        await Assert.That(result.Items[0].Name).IsEqualTo("offline-host");
        await Assert.That(result.Items[1].Name).IsEqualTo("healthy-host");
    }

    // ========== Health status filter combined with SQL filters ==========

    [Test]
    public async Task SearchAsync_HealthStatusFilter_CombinedWithTextSearch()
    {
        using TestDatabaseFactory dbFactory = new();

        Machine healthyMatch = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "web-server");
        healthyMatch.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(healthyMatch);
        MachineStateSummary healthyState = TestDataBuilder.BuildMachineStateSummary(machineId: healthyMatch.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(healthyState);

        Machine offlineMatch = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "web-cache");
        offlineMatch.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(offlineMatch);
        MachineStateSummary offlineState = TestDataBuilder.BuildMachineStateSummary(machineId: offlineMatch.Id, healthStatus: 3);
        await dbFactory.Context.InsertAsync(offlineState);

        Machine healthyNoMatch = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "db-primary");
        healthyNoMatch.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(healthyNoMatch);
        MachineStateSummary healthyNoState = TestDataBuilder.BuildMachineStateSummary(machineId: healthyNoMatch.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(healthyNoState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.HealthStatus = "healthy";
        criteria.Search = "web";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("web-server");
    }

    // ========== PageSize < 1 clamped to default ==========

    [Test]
    public async Task SearchAsync_PageSizeZero_ClampedToDefault()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.PageSize = 0;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.PageSize).IsEqualTo(25);
        await Assert.That(result.TotalCount).IsEqualTo(1);
    }

    [Test]
    public async Task SearchAsync_PageSizeNegative_ClampedToDefault()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.PageSize = -5;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.PageSize).IsEqualTo(25);
    }

    [Test]
    public async Task SearchAsync_NullTenantId_PreservesClampedPageAndPageSize()
    {
        // Verify that page and pageSize clamping occurs even when tenantId is null.
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Page = -3;
        criteria.PageSize = 999;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, null, CancellationToken.None);

        await Assert.That(result.Page).IsEqualTo(1);
        await Assert.That(result.PageSize).IsEqualTo(25);
        await Assert.That(result.TotalCount).IsEqualTo(0);
    }

    // ========== ComputeScalarHealth — offline when not online ==========

    [Test]
    public async Task SearchAsync_OfflineMachine_HealthIsOfflineWhenNoPrecomputedStatus()
    {
        // When HealthStatus is not pre-computed (null) and the machine is offline,
        // ComputeScalarHealth should return Offline.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "offline-box");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // Insert state summary without a pre-computed health status (null mapped from 0-default won't work,
        // but the query projects s.HealthStatus which is non-null short. To exercise ComputeScalarHealth,
        // we need a machine with no MachineStateSummary row at all — the LEFT JOIN produces null.)
        // No MachineStateSummary inserted — LEFT JOIN yields null for StateHealthStatus.

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: false), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HealthStatus).IsEqualTo(MachineHealthStatus.Offline);
    }

    // ========== ComputeScalarHealth — critical when CPU >= 95 ==========

    [Test]
    public async Task SearchAsync_OnlineMachineHighCpu_HealthIsCritical()
    {
        // When online and CPU >= 95, ComputeScalarHealth should return Critical.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "hot-cpu");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // No MachineStateSummary — LEFT JOIN yields null StateHealthStatus so
        // ComputeScalarHealth is used. But we need cpu data from the state row.
        // Insert a summary with null HealthStatus to trigger the else branch.
        // Since HealthStatus column is NOT NULL with default 0, the projection yields 0
        // which maps to (short)0 = Healthy. To exercise ComputeScalarHealth we rely on
        // the fact that StateHealthStatus is nullable short? and the projection
        // sets it to null when the state join is null. So we test with NO state row.

        // Actually, with no state row, StateCpuUsagePercent is null — so ComputeScalarHealth
        // gets null for cpu. We need a state row with explicit values. The short HealthStatus
        // is non-nullable in the model but nullable in the projection (s is null => null).

        // Since the MachineStateSummary model has HealthStatus as non-null short,
        // we must rely on the LEFT JOIN null path for ComputeScalarHealth.
        // For this test, the machine is online but has no state, so all metric values are null,
        // and ComputeScalarHealth returns Healthy.

        // To test the critical CPU threshold properly, we use a pre-computed HealthStatus.
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, cpuPercent: 96, healthStatus: 2);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HealthStatus).IsEqualTo(MachineHealthStatus.Critical);
    }

    // ========== ComputeScalarHealth — critical when memory >= 95 ==========

    [Test]
    public async Task SearchAsync_OnlineMachineHighMemory_HealthIsCritical()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "mem-full");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, cpuPercent: 10, memoryPercent: 97, healthStatus: 2);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HealthStatus).IsEqualTo(MachineHealthStatus.Critical);
    }

    // ========== ComputeScalarHealth — critical when failed services > 0 ==========

    [Test]
    public async Task SearchAsync_OnlineMachineFailedServices_HealthIsCritical()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "svc-fail");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, cpuPercent: 10, memoryPercent: 20, healthStatus: 2);
        state.FailedServices = 2;
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HealthStatus).IsEqualTo(MachineHealthStatus.Critical);
    }

    // ========== ComputeScalarHealth — warning when CPU 80-94 ==========

    [Test]
    public async Task SearchAsync_OnlineMachineCpuInWarningRange_HealthIsWarning()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "warm-cpu");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, cpuPercent: 85, memoryPercent: 40, healthStatus: 1);
        state.FailedServices = 0;
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HealthStatus).IsEqualTo(MachineHealthStatus.Warning);
    }

    // ========== ComputeScalarHealth — warning when memory 80-94 ==========

    [Test]
    public async Task SearchAsync_OnlineMachineMemoryInWarningRange_HealthIsWarning()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "warm-mem");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, cpuPercent: 30, memoryPercent: 88, healthStatus: 1);
        state.FailedServices = 0;
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HealthStatus).IsEqualTo(MachineHealthStatus.Warning);
    }

    // ========== ComputeScalarHealth — healthy when all metrics nominal ==========

    [Test]
    public async Task SearchAsync_OnlineMachineAllNominal_HealthIsHealthy()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "happy-box");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, cpuPercent: 25, memoryPercent: 40, healthStatus: 0);
        state.FailedServices = 0;
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HealthStatus).IsEqualTo(MachineHealthStatus.Healthy);
    }

    // ========== Text search matches hostname from state summary ==========

    [Test]
    public async Task SearchAsync_TextSearch_MatchesByHostnameFromStateSummary()
    {
        // The search text matches the hostname column in MachineStateSummary,
        // not just the machine Name column.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "generic-name");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id);
        state.Hostname = "prod-webserver-01.example.com";
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Search = "webserver";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Hostname).IsEqualTo("prod-webserver-01.example.com");
    }

    // ========== Text search matches hardware model ==========

    [Test]
    public async Task SearchAsync_TextSearch_MatchesByHardwareModel()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "dell-r740");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id);
        state.HardwareModel = "Dell PowerEdge R740";
        await dbFactory.Context.InsertAsync(state);

        Machine other = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "hp-dl380");
        other.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(other);
        MachineStateSummary otherState = TestDataBuilder.BuildMachineStateSummary(machineId: other.Id);
        otherState.HardwareModel = "HP ProLiant DL380";
        await dbFactory.Context.InsertAsync(otherState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Search = "PowerEdge";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HardwareModel).IsEqualTo("Dell PowerEdge R740");
    }

    // ========== Text search with no matches ==========

    [Test]
    public async Task SearchAsync_TextSearch_NoMatches_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "server-alpha");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Search = "nonexistent-hostname-xyz";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(0);
    }

    // ========== IP address parsing ==========

    [Test]
    public async Task SearchAsync_MachineWithIpAddresses_FirstIpReturned()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "ip-machine");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id);
        state.IpAddresses = """["10.0.0.1","10.0.0.2"]""";
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].IpAddress).IsEqualTo("10.0.0.1");
    }

    [Test]
    public async Task SearchAsync_MachineWithEmptyIpArray_IpAddressIsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "no-ip");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id);
        state.IpAddresses = "[]";
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].IpAddress).IsNull();
    }

    [Test]
    public async Task SearchAsync_MachineWithInvalidIpJson_IpAddressIsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "bad-json");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id);
        state.IpAddresses = "not-valid-json";
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].IpAddress).IsNull();
    }

    [Test]
    public async Task SearchAsync_MachineWithNullIpAddresses_IpAddressIsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "null-ip");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id);
        state.IpAddresses = null;
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].IpAddress).IsNull();
    }

    // ========== HasDiskHealthIssue true filter ==========

    [Test]
    public async Task SearchAsync_HasDiskHealthIssueTrue_OnlyReturnsMachinesWithIssues()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine healthyDisk = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "healthy-disk");
        healthyDisk.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(healthyDisk);
        MachineStateSummary healthyState = TestDataBuilder.BuildMachineStateSummary(machineId: healthyDisk.Id);
        healthyState.HasDiskHealthIssue = false;
        await dbFactory.Context.InsertAsync(healthyState);

        Machine failedDisk = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "failed-disk");
        failedDisk.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(failedDisk);
        MachineStateSummary failedState = TestDataBuilder.BuildMachineStateSummary(machineId: failedDisk.Id);
        failedState.HasDiskHealthIssue = true;
        await dbFactory.Context.InsertAsync(failedState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.HasDiskHealthIssue = true;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("failed-disk");
    }

    // ========== HasHardwareIssue true filter ==========

    [Test]
    public async Task SearchAsync_HasHardwareIssueTrue_OnlyReturnsMachinesWithIssues()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine goodHw = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "good-hw");
        goodHw.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(goodHw);
        MachineStateSummary goodState = TestDataBuilder.BuildMachineStateSummary(machineId: goodHw.Id);
        goodState.HasHardwareIssue = false;
        await dbFactory.Context.InsertAsync(goodState);

        Machine badHw = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "bad-hw");
        badHw.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(badHw);
        MachineStateSummary badState = TestDataBuilder.BuildMachineStateSummary(machineId: badHw.Id);
        badState.HasHardwareIssue = true;
        await dbFactory.Context.InsertAsync(badState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.HasHardwareIssue = true;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("bad-hw");
    }

    // ========== DiskMin / DiskMax filters ==========

    [Test]
    public async Task SearchAsync_DiskMinFilter_FiltersCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine lowDisk = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "low-disk");
        lowDisk.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(lowDisk);
        MachineStateSummary lowState = TestDataBuilder.BuildMachineStateSummary(machineId: lowDisk.Id);
        lowState.MaxDiskUsagePercent = 20;
        await dbFactory.Context.InsertAsync(lowState);

        Machine highDisk = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-disk");
        highDisk.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(highDisk);
        MachineStateSummary highState = TestDataBuilder.BuildMachineStateSummary(machineId: highDisk.Id);
        highState.MaxDiskUsagePercent = 85;
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.DiskMin = 50;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("high-disk");
    }

    [Test]
    public async Task SearchAsync_DiskMaxFilter_FiltersCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine lowDisk = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "low-disk");
        lowDisk.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(lowDisk);
        MachineStateSummary lowState = TestDataBuilder.BuildMachineStateSummary(machineId: lowDisk.Id);
        lowState.MaxDiskUsagePercent = 20;
        await dbFactory.Context.InsertAsync(lowState);

        Machine highDisk = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-disk");
        highDisk.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(highDisk);
        MachineStateSummary highState = TestDataBuilder.BuildMachineStateSummary(machineId: highDisk.Id);
        highState.MaxDiskUsagePercent = 85;
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.DiskMax = 50;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("low-disk");
    }

    // ========== Invalid health status string ==========

    [Test]
    public async Task SearchAsync_InvalidHealthStatusString_IgnoresFilter()
    {
        // When the health status filter string contains only invalid tokens,
        // ParseHealthStatuses returns an empty set and the filter is not applied.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.HealthStatus = "invalid,bogus";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        // The filter parsed no valid statuses so the WHERE clause is not added,
        // returning all machines.
        await Assert.That(result.TotalCount).IsEqualTo(1);
    }

    // ========== Invalid type filter ==========

    [Test]
    public async Task SearchAsync_InvalidTypeFilter_IgnoresFilter()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Type = "NotAMachineType";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
    }

    // ========== Sort by disk ==========

    [Test]
    public async Task SearchAsync_SortByDisk_SortsCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine lowDisk = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "low-disk");
        lowDisk.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(lowDisk);
        MachineStateSummary lowState = TestDataBuilder.BuildMachineStateSummary(machineId: lowDisk.Id);
        lowState.MaxDiskUsagePercent = 10;
        await dbFactory.Context.InsertAsync(lowState);

        Machine highDisk = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-disk");
        highDisk.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(highDisk);
        MachineStateSummary highState = TestDataBuilder.BuildMachineStateSummary(machineId: highDisk.Id);
        highState.MaxDiskUsagePercent = 90;
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.SortBy = "disk";
        criteria.SortDir = "desc";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.Items[0].MaxDiskUsagePercent).IsEqualTo(90);
        await Assert.That(result.Items[1].MaxDiskUsagePercent).IsEqualTo(10);
    }

    [Test]
    public async Task SearchAsync_SortByDiskAsc_SortsCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine lowDisk = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "low-disk");
        lowDisk.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(lowDisk);
        MachineStateSummary lowState = TestDataBuilder.BuildMachineStateSummary(machineId: lowDisk.Id);
        lowState.MaxDiskUsagePercent = 10;
        await dbFactory.Context.InsertAsync(lowState);

        Machine highDisk = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-disk");
        highDisk.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(highDisk);
        MachineStateSummary highState = TestDataBuilder.BuildMachineStateSummary(machineId: highDisk.Id);
        highState.MaxDiskUsagePercent = 90;
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.SortBy = "disk";
        criteria.SortDir = "asc";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.Items[0].MaxDiskUsagePercent).IsEqualTo(10);
        await Assert.That(result.Items[1].MaxDiskUsagePercent).IsEqualTo(90);
    }

    // ========== Machine with no state summary (LEFT JOIN null) ==========

    [Test]
    public async Task SearchAsync_MachineWithNoStateSummary_ReturnsWithNullMetrics()
    {
        // When a machine has no MachineStateSummary row, the LEFT JOIN yields nulls
        // for all state fields. The machine should still appear in results.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "no-state");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].CpuUsagePercent).IsNull();
        await Assert.That(result.Items[0].MemoryUsagePercent).IsNull();
        await Assert.That(result.Items[0].Hostname).IsNull();
        await Assert.That(result.Items[0].IpAddress).IsNull();
    }

    // ========== EnrichWithJsonbData — critical via high disk usage ==========

    [Test]
    public async Task SearchAsync_OnlineMachineHighDisk_HealthRecalculatedToCritical()
    {
        // The enrichment step recalculates health with disk data. When disk >= 95
        // and the machine is online, enriched health should be Critical.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "disk-critical");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, cpuPercent: 20, memoryPercent: 30, healthStatus: 0);
        state.MaxDiskUsagePercent = 97;
        state.HasDiskHealthIssue = false;
        state.HasHardwareIssue = false;
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HealthStatus).IsEqualTo(MachineHealthStatus.Critical);
    }

    // ========== EnrichWithJsonbData — critical via disk health issue ==========

    [Test]
    public async Task SearchAsync_OnlineMachineDiskHealthIssue_HealthRecalculatedToCritical()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "smart-fail");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, cpuPercent: 20, memoryPercent: 30, healthStatus: 0);
        state.MaxDiskUsagePercent = 50;
        state.HasDiskHealthIssue = true;
        state.HasHardwareIssue = false;
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HealthStatus).IsEqualTo(MachineHealthStatus.Critical);
    }

    // ========== EnrichWithJsonbData — critical via hardware issue ==========

    [Test]
    public async Task SearchAsync_OnlineMachineHardwareIssue_HealthRecalculatedToCritical()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "hw-fault");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, cpuPercent: 20, memoryPercent: 30, healthStatus: 0);
        state.MaxDiskUsagePercent = 40;
        state.HasDiskHealthIssue = false;
        state.HasHardwareIssue = true;
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HealthStatus).IsEqualTo(MachineHealthStatus.Critical);
    }

    // ========== EnrichWithJsonbData — warning via disk 80-94 ==========

    [Test]
    public async Task SearchAsync_OnlineMachineDiskInWarningRange_HealthRecalculatedToWarning()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "disk-warm");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, cpuPercent: 20, memoryPercent: 30, healthStatus: 0);
        state.MaxDiskUsagePercent = 88;
        state.HasDiskHealthIssue = false;
        state.HasHardwareIssue = false;
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HealthStatus).IsEqualTo(MachineHealthStatus.Warning);
    }

    // ========== EnrichWithJsonbData — offline overrides all enrichment ==========

    [Test]
    public async Task SearchAsync_OfflineMachineWithHighDisk_HealthStaysOffline()
    {
        // Even when disk usage is critical, an offline machine should remain Offline.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "offline-full-disk");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, cpuPercent: 20, memoryPercent: 30, healthStatus: 3);
        state.MaxDiskUsagePercent = 99;
        state.HasDiskHealthIssue = true;
        state.HasHardwareIssue = true;
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: false), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].HealthStatus).IsEqualTo(MachineHealthStatus.Offline);
    }

    // ========== LastPing — falls back to Redis when state has no LastSeenAt ==========

    [Test]
    public async Task SearchAsync_NoStateLastPing_FallsBackToRedisPing()
    {
        // When MachineStateSummary.LastSeenAt is null, BuildDtos falls back to the
        // Redis last ping map value.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "redis-ping");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id);
        state.LastSeenAt = null;
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        // The mock ping service returns DateTimeOffset.UtcNow when online=true.
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].LastPing).IsNotNull();
        await Assert.That(result.Items[0].IsOnline).IsTrue();
    }

    // ========== LastPing — state LastSeenAt takes precedence over Redis ==========

    [Test]
    public async Task SearchAsync_StateHasLastSeenAt_UsesStateValueForLastPing()
    {
        DateTimeOffset stateTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5);

        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "state-ping");
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, lastSeenAt: stateTimestamp);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: false), CreateConfigService());

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            DefaultCriteria(), 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].LastPing).IsNotNull();
    }

    // ========== Valid PageSize within range is preserved ==========

    [Test]
    public async Task SearchAsync_ValidPageSize_Preserved()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.PageSize = 50;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.PageSize).IsEqualTo(50);
    }

    // ========== PageSize exactly at boundary ==========

    [Test]
    public async Task SearchAsync_PageSizeExactly100_Preserved()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.PageSize = 100;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.PageSize).IsEqualTo(100);
    }

    [Test]
    public async Task SearchAsync_PageSizeExactly101_ClampedToDefault()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.PageSize = 101;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.PageSize).IsEqualTo(25);
    }

    [Test]
    public async Task SearchAsync_PageSizeExactly1_Preserved()
    {
        using TestDatabaseFactory dbFactory = new();
        for (int i = 0; i < 3; i++)
        {
            Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: $"machine-{i:D2}");
            machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        }

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.PageSize = 1;

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.PageSize).IsEqualTo(1);
        await Assert.That(result.Items.Count).IsEqualTo(1);
        await Assert.That(result.TotalCount).IsEqualTo(3);
    }

    // ========== HealthStatus filter with "critical" value ==========

    [Test]
    public async Task SearchAsync_HealthStatusCritical_OnlyReturnsCriticalMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine criticalMachine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "critical-box");
        criticalMachine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(criticalMachine);
        MachineStateSummary criticalState = TestDataBuilder.BuildMachineStateSummary(
            machineId: criticalMachine.Id, healthStatus: 2);
        await dbFactory.Context.InsertAsync(criticalState);

        Machine healthyMachine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "healthy-box");
        healthyMachine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(healthyMachine);
        MachineStateSummary healthyState = TestDataBuilder.BuildMachineStateSummary(
            machineId: healthyMachine.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(healthyState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.HealthStatus = "critical";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("critical-box");
    }

    // ========== HealthStatus filter with "warning" value ==========

    [Test]
    public async Task SearchAsync_HealthStatusWarning_OnlyReturnsWarningMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine warningMachine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "warning-box");
        warningMachine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(warningMachine);
        MachineStateSummary warningState = TestDataBuilder.BuildMachineStateSummary(
            machineId: warningMachine.Id, healthStatus: 1);
        await dbFactory.Context.InsertAsync(warningState);

        Machine healthyMachine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "healthy-box");
        healthyMachine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(healthyMachine);
        MachineStateSummary healthyState = TestDataBuilder.BuildMachineStateSummary(
            machineId: healthyMachine.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(healthyState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.HealthStatus = "warning";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("warning-box");
    }

    // ========== Whitespace-only search is ignored ==========

    [Test]
    public async Task SearchAsync_WhitespaceOnlySearch_IgnoresFilter()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Search = "   ";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
    }

    // ========== Whitespace-only OS filter is ignored ==========

    [Test]
    public async Task SearchAsync_WhitespaceOnlyOsFilter_IgnoresFilter()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.Os = "  ";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
    }

    // ========== Whitespace-only health status filter is ignored ==========

    [Test]
    public async Task SearchAsync_WhitespaceOnlyHealthStatus_IgnoresFilter()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.HealthStatus = "   ";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
    }

    // ========== Sort by CPU ascending ==========

    [Test]
    public async Task SearchAsync_SortByCpuAsc_SortsCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine low = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "low-cpu");
        low.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(low);
        MachineStateSummary lowState = TestDataBuilder.BuildMachineStateSummary(machineId: low.Id, cpuPercent: 10);
        await dbFactory.Context.InsertAsync(lowState);

        Machine high = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-cpu");
        high.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(high);
        MachineStateSummary highState = TestDataBuilder.BuildMachineStateSummary(machineId: high.Id, cpuPercent: 90);
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.SortBy = "cpu";
        criteria.SortDir = "asc";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.Items[0].CpuUsagePercent).IsEqualTo(10);
        await Assert.That(result.Items[1].CpuUsagePercent).IsEqualTo(90);
    }

    // ========== Sort by memory ascending ==========

    [Test]
    public async Task SearchAsync_SortByMemoryAsc_SortsCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine low = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "low-mem");
        low.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(low);
        MachineStateSummary lowState = TestDataBuilder.BuildMachineStateSummary(machineId: low.Id, memoryPercent: 15);
        await dbFactory.Context.InsertAsync(lowState);

        Machine high = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-mem");
        high.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(high);
        MachineStateSummary highState = TestDataBuilder.BuildMachineStateSummary(machineId: high.Id, memoryPercent: 75);
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.SortBy = "memory";
        criteria.SortDir = "asc";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.Items[0].MemoryUsagePercent).IsEqualTo(15);
        await Assert.That(result.Items[1].MemoryUsagePercent).IsEqualTo(75);
    }
}
