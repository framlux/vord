// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
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

        // Pre-computed HealthStatus=0 (Healthy) means the machine is online.
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, healthStatus: 0);
        await dbFactory.Context.InsertAsync(state);

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
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, cpuPercent: 96, healthStatus: 2);
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
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, cpuPercent: 50, memoryPercent: 85, healthStatus: 1);
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
        MachineStateSummary state1 = TestDataBuilder.BuildMachineStateSummary(machineId: machine1.Id, cpuPercent: 10);
        await dbFactory.Context.InsertAsync(state1);

        Machine machine2 = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "high-cpu");
        machine2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine2);
        MachineStateSummary state2 = TestDataBuilder.BuildMachineStateSummary(machineId: machine2.Id, cpuPercent: 90);
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
        await Assert.That(result.IsOnline).IsTrue();
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
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Hostname).IsEqualTo($"host-{machine.Id}");
    }

    // ========== GetMachineDetailAsync — telemetry deserialization branch coverage ==========

    [Test]
    public async Task GetMachineDetailAsync_WithSystemInfoTelemetry_DeserializesPayload()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        MachineTelemetry telemetry = TestDataBuilder.BuildMachineTelemetry(
            machineId: machine.Id,
            tenantId: 1,
            telemetryType: 1,
            payload: """{"hostname":"web-01","uuid":"abc-123","cpu_type":"x86_64","cpu_brand":"Intel","cpu_physical_cores":4,"cpu_logical_cores":8,"physical_memory":17179869184,"hardware_vendor":"Dell","hardware_model":"PowerEdge R640","hardware_version":"1.0","hardware_serial":"SN123","uptime_seconds":86400,"bios_version":"2.0","ip_addresses":["10.0.0.1"]}""");
        await dbFactory.Context.InsertAsync(telemetry);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.SystemInfo).IsNotNull();
        await Assert.That(result.SystemInfo!.Hostname).IsEqualTo("web-01");
        await Assert.That(result.SystemInfo.HardwareModel).IsEqualTo("PowerEdge R640");
        await Assert.That(result.SystemInfo.CpuPhysicalCores).IsEqualTo(4);
    }

    [Test]
    public async Task GetMachineDetailAsync_WithOsVersionTelemetry_DeserializesPayload()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        MachineTelemetry telemetry = TestDataBuilder.BuildMachineTelemetry(
            machineId: machine.Id,
            tenantId: 1,
            telemetryType: 2,
            payload: """{"name":"Ubuntu","version":"22.04","platform":"linux","arch":"amd64","build":"5.15.0-78-generic"}""");
        await dbFactory.Context.InsertAsync(telemetry);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.OsVersion).IsNotNull();
        await Assert.That(result.OsVersion!.Name).IsEqualTo("Ubuntu");
        await Assert.That(result.OsVersion.Version).IsEqualTo("22.04");
    }

    [Test]
    public async Task GetMachineDetailAsync_WithServiceStatusTelemetry_FiltersFailedServices()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        MachineTelemetry telemetry = TestDataBuilder.BuildMachineTelemetry(
            machineId: machine.Id,
            tenantId: 1,
            telemetryType: 12,
            payload: """{"services":[{"unit":"nginx.service","load_state":"loaded","active_state":"active","sub_state":"running","description":"nginx"},{"unit":"mysql.service","load_state":"loaded","active_state":"failed","sub_state":"failed","description":"MySQL"}]}""");
        await dbFactory.Context.InsertAsync(telemetry);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.FailedServices.Count).IsEqualTo(1);
        await Assert.That(result.FailedServices[0].Unit).IsEqualTo("mysql.service");
        await Assert.That(result.TotalServices).IsEqualTo(2);
    }

    [Test]
    public async Task GetMachineDetailAsync_WithMalformedPayload_ReturnsNullForThatType()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // Insert malformed JSON for SystemInfo (type=1) to exercise the catch branch in DeserializePayload
        MachineTelemetry telemetry = TestDataBuilder.BuildMachineTelemetry(
            machineId: machine.Id,
            tenantId: 1,
            telemetryType: 1,
            payload: "{{not valid json at all}}");
        await dbFactory.Context.InsertAsync(telemetry);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.SystemInfo).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_NoTelemetry_AllPayloadsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.SystemInfo).IsNull();
        await Assert.That(result.OsVersion).IsNull();
        await Assert.That(result.CpuUsage).IsNull();
        await Assert.That(result.MemoryUsage).IsNull();
        await Assert.That(result.DiskUsages).IsNull();
        await Assert.That(result.HardwareHealth).IsNull();
        await Assert.That(result.PackageUpdates).IsNull();
        await Assert.That(result.TotalServices).IsEqualTo(0);
        await Assert.That(result.FailedServices.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetMachineDetailAsync_WithCpuAndMemoryTelemetry_DeserializesBothPayloads()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        MachineTelemetry cpuTelemetry = TestDataBuilder.BuildMachineTelemetry(
            machineId: machine.Id,
            tenantId: 1,
            telemetryType: 6,
            payload: """{"cpu_usage_percent":75}""");
        await dbFactory.Context.InsertAsync(cpuTelemetry);

        MachineTelemetry memTelemetry = TestDataBuilder.BuildMachineTelemetry(
            machineId: machine.Id,
            tenantId: 1,
            telemetryType: 7,
            payload: """{"memory_total":17179869184,"memory_used":8589934592,"memory_usage_percent":50}""");
        await dbFactory.Context.InsertAsync(memTelemetry);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.CpuUsage).IsNotNull();
        await Assert.That(result.CpuUsage!.CpuUsagePercent).IsEqualTo(75);
        await Assert.That(result.MemoryUsage).IsNotNull();
        await Assert.That(result.MemoryUsage!.MemoryUsagePercent).IsEqualTo(50);
    }

    [Test]
    public async Task GetMachineDetailAsync_WithSshSessionTelemetry_DeserializesSessions()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        MachineTelemetry sshTelemetry = TestDataBuilder.BuildMachineTelemetry(
            machineId: machine.Id,
            tenantId: 1,
            telemetryType: 9,
            payload: """{"user":"admin","source_ip":"192.168.1.1","source_port":22,"action":"connect","auth_method":"publickey","timestamp":"2026-05-11T10:00:00Z"}""");
        await dbFactory.Context.InsertAsync(sshTelemetry);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.RecentSshSessions.Count).IsEqualTo(1);
        await Assert.That(result.RecentSshSessions[0].User).IsEqualTo("admin");
        await Assert.That(result.RecentSshSessions[0].Action).IsEqualTo("connect");
    }

    [Test]
    public async Task GetMachineDetailAsync_NoState_OnlineMachine_HealthIsHealthy()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.HealthStatus).IsEqualTo(MachineHealthStatus.Healthy);
    }

    [Test]
    public async Task GetMachineDetailAsync_NoState_HostnameFallsBackToSystemInfo()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // No MachineStateSummary, but SystemInfo telemetry provides hostname
        MachineTelemetry telemetry = TestDataBuilder.BuildMachineTelemetry(
            machineId: machine.Id,
            tenantId: 1,
            telemetryType: 1,
            payload: """{"hostname":"fallback-host","uuid":"","cpu_type":"","cpu_brand":"","cpu_physical_cores":0,"cpu_logical_cores":0,"physical_memory":0,"hardware_vendor":"","hardware_model":"","hardware_version":"","hardware_serial":"","uptime_seconds":0,"bios_version":"","ip_addresses":[]}""");
        await dbFactory.Context.InsertAsync(telemetry);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Hostname).IsEqualTo("fallback-host");
    }

    // ========== GetFleetOverviewAsync — ParseFirstIp and null-coalescing branch coverage ==========

    [Test]
    public async Task GetFleetOverviewAsync_NullIpAddresses_ReturnsNullIp()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // MachineStateSummary with null IpAddresses exercises the ParseFirstIp null/empty branch
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Machines.Count).IsEqualTo(1);
        await Assert.That(result.Machines[0].IpAddress).IsNull();
    }

    [Test]
    public async Task GetFleetOverviewAsync_NullableFieldsDefaultToZeroOrFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // MachineStateSummary without PendingUpdates/SecurityUpdates/FailedServices/TotalServices
        // exercises the null-coalescing branches in the fleet DTO mapping
        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(online: true), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Machines.Count).IsEqualTo(1);
        FleetMachineDto dto = result.Machines[0];
        await Assert.That(dto.PendingUpdates).IsEqualTo(0);
        await Assert.That(dto.SecurityUpdates).IsEqualTo(0);
        await Assert.That(dto.FailedServices).IsEqualTo(0);
        await Assert.That(dto.TotalServices).IsEqualTo(0);
        await Assert.That(dto.HasDiskHealthIssue).IsFalse();
        await Assert.That(dto.HasHardwareIssue).IsFalse();
    }

    [Test]
    public async Task GetFleetOverviewAsync_PageSizeZero_DefaultsTo25()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 0, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.PageSize).IsEqualTo(25);
    }

    [Test]
    public async Task GetFleetOverviewAsync_SortDescending_UsesDescendingOrder()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine1 = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "aaa-host");
        machine1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine1);
        Machine machine2 = TestDataBuilder.BuildMachine(tenantId: 1, hostname: "zzz-host");
        machine2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine2);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, CreateMockPingService(), CreateConfigService());

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, null, "name", "desc", CancellationToken.None);

        await Assert.That(result.Machines.Count).IsEqualTo(2);
        await Assert.That(result.Machines[0].Name).IsEqualTo("zzz-host");
        await Assert.That(result.Machines[1].Name).IsEqualTo("aaa-host");
    }
}
