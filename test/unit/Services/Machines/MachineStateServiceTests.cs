// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Dashboard;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="MachineStateService"/>.
/// </summary>
public class MachineStateServiceTests
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
        pingService.IsOnlineAsync(Arg.Any<long>(), Arg.Any<TimeSpan>())
            .Returns(online);
        pingService.GetLastPingAsync(Arg.Any<long>())
            .Returns(online ? (DateTimeOffset?)DateTimeOffset.UtcNow : null);

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

    // ========== GetFleetOverviewAsync tests ==========

    [Test]
    public async Task GetFleetOverviewAsync_NullTenantId_ReturnsEmptyResult()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(0);
        await Assert.That(result.Machines.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetFleetOverviewAsync_NoMachines_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(0);
        await Assert.That(result.Summary.TotalMachines).IsEqualTo(0);
    }

    [Test]
    public async Task GetFleetOverviewAsync_WithMachines_ReturnsSummaryAndMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine1 = TestDataBuilder.BuildMachine(tenantId: 1);
        machine1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine1);
        Machine machine2 = TestDataBuilder.BuildMachine(tenantId: 1);
        machine2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine2);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Summary.TotalMachines).IsEqualTo(2);
        await Assert.That(result.Machines.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetFleetOverviewAsync_OnlineMachines_CountsCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Summary.OnlineMachines).IsEqualTo(1);
        await Assert.That(result.Summary.OfflineCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetFleetOverviewAsync_OfflineMachines_HealthStatusIsOffline()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: false), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Machines[0].HealthStatus).IsEqualTo(MachineHealthStatus.Offline);
    }

    [Test]
    public async Task GetFleetOverviewAsync_CriticalCpu_HealthStatusIsCritical()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineState state = TestDataBuilder.BuildMachineState(machineId: machine.Id, cpuPercent: 96);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Summary.CriticalCount).IsEqualTo(1);
    }

    [Test]
    public async Task GetFleetOverviewAsync_WarningMemory_HealthStatusIsWarning()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineState state = TestDataBuilder.BuildMachineState(machineId: machine.Id, cpuPercent: 50, memoryPercent: 85);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Summary.WarningCount).IsEqualTo(1);
    }

    [Test]
    public async Task GetFleetOverviewAsync_PageClamping_PageBelowOneBecomesOne()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            -5, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Page).IsEqualTo(1);
        await Assert.That(result.Machines.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetFleetOverviewAsync_PageSizeClamping_OutOfRangeDefaultsTo25()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 999, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.PageSize).IsEqualTo(25);
    }

    [Test]
    public async Task GetFleetOverviewAsync_SearchFilter_FiltersMatchingMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine1 = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "web-server-1");
        machine1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine1);
        Machine machine2 = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "db-server-1");
        machine2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine2);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, "web-server", null, "name", "asc", CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
    }

    [Test]
    public async Task GetFleetOverviewAsync_StatusFilter_FiltersByHealthStatus()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: false), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, "offline", "name", "asc", CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(1);
    }

    [Test]
    public async Task GetFleetOverviewAsync_StatusFilterHealthy_ExcludesOffline()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: false), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, "healthy", "name", "asc", CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetFleetOverviewAsync_DeletedMachine_IsExcluded()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.IsDeleted = true;
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetFleetOverviewAsync_DifferentTenant_ExcludesOtherTenantMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 2);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.TotalCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetFleetOverviewAsync_SortByCpuDesc_ReturnsSortedResults()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine1 = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "low-cpu");
        machine1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine1);
        MachineState state1 = TestDataBuilder.BuildMachineState(machineId: machine1.Id, cpuPercent: 10);
        await dbFactory.Context.InsertAsync(state1);

        Machine machine2 = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-cpu");
        machine2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine2);
        MachineState state2 = TestDataBuilder.BuildMachineState(machineId: machine2.Id, cpuPercent: 90);
        await dbFactory.Context.InsertAsync(state2);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, null, "cpu", "desc", CancellationToken.None);

        await Assert.That(result.Machines[0].CpuUsagePercent).IsEqualTo(90);
        await Assert.That(result.Machines[1].CpuUsagePercent).IsEqualTo(10);
    }

    // ========== GetMachineDetailAsync tests ==========

    [Test]
    public async Task GetMachineDetailAsync_NullTenantId_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(1, null, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_MachineNotFound_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(999, 1, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_WrongTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 2);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_DeletedMachine_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.IsDeleted = true;
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_ValidMachine_ReturnsDetail()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(machine.Id);
        await Assert.That(result.IsOnline).IsEqualTo(true);
    }

    [Test]
    public async Task GetMachineDetailAsync_OfflineMachine_HealthIsOffline()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: false), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.HealthStatus).IsEqualTo(MachineHealthStatus.Offline);
    }

    [Test]
    public async Task GetMachineDetailAsync_WithMachineState_ReturnsHostname()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineState state = TestDataBuilder.BuildMachineState(machineId: machine.Id);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Hostname).IsEqualTo($"host-{machine.Id}");
    }
}
