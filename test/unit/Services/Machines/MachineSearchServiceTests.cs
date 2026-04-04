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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState lowState = TestDataBuilder.BuildMachineState(machineId: lowCpu.Id, cpuPercent: 20);
        await dbFactory.Context.InsertAsync(lowState);

        Machine highCpu = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-cpu");
        highCpu.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(highCpu);
        MachineState highState = TestDataBuilder.BuildMachineState(machineId: highCpu.Id, cpuPercent: 85);
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState lowState = TestDataBuilder.BuildMachineState(machineId: lowCpu.Id, cpuPercent: 20);
        await dbFactory.Context.InsertAsync(lowState);

        Machine highCpu = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-cpu");
        highCpu.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(highCpu);
        MachineState highState = TestDataBuilder.BuildMachineState(machineId: highCpu.Id, cpuPercent: 85);
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState lowState = TestDataBuilder.BuildMachineState(machineId: lowMem.Id, memoryPercent: 30);
        await dbFactory.Context.InsertAsync(lowState);

        Machine highMem = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-mem");
        highMem.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(highMem);
        MachineState highState = TestDataBuilder.BuildMachineState(machineId: highMem.Id, memoryPercent: 90);
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState noUpdateState = TestDataBuilder.BuildMachineState(machineId: noUpdates.Id);
        noUpdateState.PendingUpdates = 0;
        await dbFactory.Context.InsertAsync(noUpdateState);

        Machine manyUpdates = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "many-updates");
        manyUpdates.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(manyUpdates);
        MachineState manyUpdateState = TestDataBuilder.BuildMachineState(machineId: manyUpdates.Id);
        manyUpdateState.PendingUpdates = 15;
        await dbFactory.Context.InsertAsync(manyUpdateState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState healthyState = TestDataBuilder.BuildMachineState(machineId: healthy.Id);
        healthyState.FailedServices = 0;
        await dbFactory.Context.InsertAsync(healthyState);

        Machine failing = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "failing-services");
        failing.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(failing);
        MachineState failingState = TestDataBuilder.BuildMachineState(machineId: failing.Id);
        failingState.FailedServices = 3;
        await dbFactory.Context.InsertAsync(failingState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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

        // Seed MachineState with Offline health status so the SQL filter matches.
        MachineState state = TestDataBuilder.BuildMachineState(machineId: machine.Id, healthStatus: 3);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: false), CreateConfigService(), CreateSqliteDialect());

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

        // Seed MachineState with Offline health status.
        MachineState state = TestDataBuilder.BuildMachineState(machineId: machine.Id, healthStatus: 3);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: false), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState matchState = TestDataBuilder.BuildMachineState(machineId: match.Id, cpuPercent: 85);
        await dbFactory.Context.InsertAsync(matchState);

        Machine noMatch1 = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "web-windows");
        noMatch1.OperatingSystem = OperatingSystems.Windows;
        noMatch1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(noMatch1);
        MachineState noMatch1State = TestDataBuilder.BuildMachineState(machineId: noMatch1.Id, cpuPercent: 90);
        await dbFactory.Context.InsertAsync(noMatch1State);

        Machine noMatch2 = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "db-ubuntu");
        noMatch2.OperatingSystem = OperatingSystems.Ubuntu;
        noMatch2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(noMatch2);
        MachineState noMatch2State = TestDataBuilder.BuildMachineState(machineId: noMatch2.Id, cpuPercent: 10);
        await dbFactory.Context.InsertAsync(noMatch2State);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState lowState = TestDataBuilder.BuildMachineState(machineId: low.Id, cpuPercent: 10);
        await dbFactory.Context.InsertAsync(lowState);

        Machine high = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-cpu");
        high.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(high);
        MachineState highState = TestDataBuilder.BuildMachineState(machineId: high.Id, cpuPercent: 90);
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
            MachineState state = TestDataBuilder.BuildMachineState(machineId: machine.Id, healthStatus: 3);
            await dbFactory.Context.InsertAsync(state);
        }

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: false), CreateConfigService(), CreateSqliteDialect());

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

        MachineState state = TestDataBuilder.BuildMachineState(machineId: machine.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService(), CreateSqliteDialect());

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
        MachineState noSecState = TestDataBuilder.BuildMachineState(machineId: noSecUpdates.Id);
        noSecState.SecurityUpdates = 0;
        await dbFactory.Context.InsertAsync(noSecState);

        Machine withSecUpdates = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "with-sec-updates");
        withSecUpdates.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(withSecUpdates);
        MachineState withSecState = TestDataBuilder.BuildMachineState(machineId: withSecUpdates.Id);
        withSecState.SecurityUpdates = 8;
        await dbFactory.Context.InsertAsync(withSecState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState lowState = TestDataBuilder.BuildMachineState(machineId: lowMem.Id, memoryPercent: 15);
        await dbFactory.Context.InsertAsync(lowState);

        Machine highMem = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-mem");
        highMem.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(highMem);
        MachineState highState = TestDataBuilder.BuildMachineState(machineId: highMem.Id, memoryPercent: 75);
        await dbFactory.Context.InsertAsync(highState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState healthyState = TestDataBuilder.BuildMachineState(machineId: healthyDisk.Id);
        healthyState.HardwareHealth = """{"fans":[],"power_supplies":[],"temperatures":[],"disk_smart":[{"device":"/dev/sda","model":"SSD","health_status":"PASSED","temperature_celsius":30,"wearout_percent":10,"power_on_hours":1000}],"bmc_firmware_version":""}""";
        await dbFactory.Context.InsertAsync(healthyState);

        Machine failedDisk = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "failed-disk");
        failedDisk.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(failedDisk);
        MachineState failedState = TestDataBuilder.BuildMachineState(machineId: failedDisk.Id);
        failedState.HardwareHealth = """{"fans":[],"power_supplies":[],"temperatures":[],"disk_smart":[{"device":"/dev/sdb","model":"HDD","health_status":"FAILED","temperature_celsius":55,"wearout_percent":95,"power_on_hours":50000}],"bmc_firmware_version":""}""";
        await dbFactory.Context.InsertAsync(failedState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        // SQLite dialect forces the full-scan path for JSONB filters, which exercises
        // the in-memory ApplyPostEnrichmentFilters path for this filter.
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState goodState = TestDataBuilder.BuildMachineState(machineId: goodHardware.Id);
        goodState.HardwareHealth = """{"fans":[{"name":"Fan0","rpm":1200}],"power_supplies":[{"name":"PSU0","status":"ok"}],"temperatures":[],"disk_smart":[],"bmc_firmware_version":""}""";
        await dbFactory.Context.InsertAsync(goodState);

        Machine badHardware = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "bad-hardware");
        badHardware.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(badHardware);
        MachineState badState = TestDataBuilder.BuildMachineState(machineId: badHardware.Id);
        // Fan with RPM=0 signals a failed fan, triggering HasHardwareIssue=true.
        badState.HardwareHealth = """{"fans":[{"name":"Fan0","rpm":0}],"power_supplies":[{"name":"PSU0","status":"ok"}],"temperatures":[],"disk_smart":[],"bmc_firmware_version":""}""";
        await dbFactory.Context.InsertAsync(badState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        // SQLite dialect forces the full-scan path for JSONB filters, exercising
        // the in-memory ApplyPostEnrichmentFilters path for this filter.
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState recentState = TestDataBuilder.BuildMachineState(
            machineId: recentMachine.Id, lastPingAt: DateTimeOffset.UtcNow);
        await dbFactory.Context.InsertAsync(recentState);

        Machine staleMachine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "stale");
        staleMachine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(staleMachine);
        MachineState staleState = TestDataBuilder.BuildMachineState(
            machineId: staleMachine.Id, lastPingAt: DateTimeOffset.UtcNow.AddHours(-3));
        await dbFactory.Context.InsertAsync(staleState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState recentState = TestDataBuilder.BuildMachineState(
            machineId: recentMachine.Id, lastPingAt: DateTimeOffset.UtcNow);
        await dbFactory.Context.InsertAsync(recentState);

        Machine staleMachine = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "stale");
        staleMachine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(staleMachine);
        MachineState staleState = TestDataBuilder.BuildMachineState(
            machineId: staleMachine.Id, lastPingAt: DateTimeOffset.UtcNow.AddHours(-3));
        await dbFactory.Context.InsertAsync(staleState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState noPingState = TestDataBuilder.BuildMachineState(machineId: noPingMachine.Id);
        await dbFactory.Context.InsertAsync(noPingState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState healthyState = TestDataBuilder.BuildMachineState(machineId: healthy.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(healthyState);

        Machine critical = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "critical-host");
        critical.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(critical);
        MachineState criticalState = TestDataBuilder.BuildMachineState(machineId: critical.Id, healthStatus: 2);
        await dbFactory.Context.InsertAsync(criticalState);

        Machine offline = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "offline-host");
        offline.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(offline);
        MachineState offlineState = TestDataBuilder.BuildMachineState(machineId: offline.Id, healthStatus: 3);
        await dbFactory.Context.InsertAsync(offlineState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState healthyState = TestDataBuilder.BuildMachineState(machineId: healthy.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(healthyState);

        Machine offline = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "offline-host");
        offline.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(offline);
        MachineState offlineState = TestDataBuilder.BuildMachineState(machineId: offline.Id, healthStatus: 3);
        await dbFactory.Context.InsertAsync(offlineState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

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
        MachineState healthyState = TestDataBuilder.BuildMachineState(machineId: healthyMatch.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(healthyState);

        Machine offlineMatch = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "web-cache");
        offlineMatch.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(offlineMatch);
        MachineState offlineState = TestDataBuilder.BuildMachineState(machineId: offlineMatch.Id, healthStatus: 3);
        await dbFactory.Context.InsertAsync(offlineState);

        Machine healthyNoMatch = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "db-primary");
        healthyNoMatch.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(healthyNoMatch);
        MachineState healthyNoState = TestDataBuilder.BuildMachineState(machineId: healthyNoMatch.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(healthyNoState);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineSearchService service = new(scopeFactory, CreateMockPingService(), CreateConfigService(), CreateSqliteDialect());

        MachineSearchCriteria criteria = DefaultCriteria();
        criteria.HealthStatus = "healthy";
        criteria.Search = "web";

        PaginatedResponse<FleetMachineDto> result = await service.SearchAsync(
            criteria, 1, CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
        await Assert.That(result.Items[0].Name).IsEqualTo("web-server");
    }
}
