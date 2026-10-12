// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics.Metrics;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Models.Dashboard;
using Framlux.FleetManagement.Services.Core.Models.Kubernetes;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.Telemetry;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="MachineStateService"/>.
/// </summary>
public class MachineStateServiceTests
{
    // ========== GetFleetOverviewAsync tests ==========

    [Test]
    public async Task GetFleetOverviewAsync_NullTenantId_ReturnsEmptyResult()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

        MachineDetailDto? result = await service.GetMachineDetailAsync(1, null, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_MachineNotFound_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_ValidMachine_ReturnsDetail()
    {
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, healthStatus: 0, lastSeenAt: DateTimeOffset.UtcNow));

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(machine.Id);
        await Assert.That(result.IsOnline).IsTrue();
        await Assert.That(result.LastPing).IsNotNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_WithMemoryInfoRow_ReturnsSwapAndAvailableWithItsReceiptTime()
    {
        // Swap and MemAvailable travel in MemoryInfo (type 4), a different record from the memory
        // usage the vitals bar reads, and one the agent sends every fifteen minutes. The receipt
        // time travels with it because the detail lookup spans seven days.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        DateTimeOffset receivedAt = DateTimeOffset.UtcNow.AddMinutes(-12);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineTelemetry(
            machineId: machine.Id,
            telemetryType: TelemetryTypeIds.MemoryInfo,
            payload: """{"memory_total":17179869184,"memory_free":2147483648,"memory_available":4294967296,"swap_total":8589934592,"swap_free":6442450944}""",
            receivedAt: receivedAt));

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.MemoryInfo).IsNotNull();
        await Assert.That(result.MemoryInfo!.MemoryAvailable).IsEqualTo(4294967296L);
        await Assert.That(result.MemoryInfo.SwapTotal).IsEqualTo(8589934592L);
        await Assert.That(result.MemoryInfo.SwapFree).IsEqualTo(6442450944L);
        await Assert.That(result.MemoryInfoReceivedAt).IsNotNull();
        await Assert.That(result.MemoryInfoReceivedAt!.Value.ToUnixTimeSeconds())
            .IsEqualTo(receivedAt.ToUnixTimeSeconds());
    }

    [Test]
    public async Task GetMachineDetailAsync_WithoutMemoryInfoRow_ReturnsNoMemoryInfoAndNoTimestamp()
    {
        // A machine that has reported memory usage but not yet the slow-tick memory info must not
        // present a timestamp with nothing behind it.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineTelemetry(
            machineId: machine.Id,
            telemetryType: TelemetryTypeIds.MemoryUsage,
            payload: """{"memory_total":17179869184,"memory_used":8589934592,"memory_usage_percent":50}"""));

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.MemoryUsage).IsNotNull();
        await Assert.That(result.MemoryInfo).IsNull();
        await Assert.That(result.MemoryInfoReceivedAt).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_WithCpuUsageRow_ReturnsTheJiffyBreakdown()
    {
        // The breakdown is already on the wire and already stored; only the DTO dropped it.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineTelemetry(
            machineId: machine.Id,
            telemetryType: TelemetryTypeIds.CpuUsage,
            payload: """{"cpu_usage_percent":61,"user_time":34,"system_time":11,"nice_time":1,"idle_time":39,"iowait_time":14,"irq_time":0,"softirq_time":2,"steal_time":9}"""));

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.CpuUsage).IsNotNull();
        await Assert.That(result.CpuUsage!.CpuUsagePercent).IsEqualTo(61);
        await Assert.That(result.CpuUsage.UserTime).IsEqualTo(34);
        await Assert.That(result.CpuUsage.IowaitTime).IsEqualTo(14);
        await Assert.That(result.CpuUsage.StealTime).IsEqualTo(9);
    }

    [Test]
    public async Task GetMachineDetailAsync_OfflineMachine_HealthIsOffline()
    {
        // No summary row at all, so the health verdict is Offline regardless of the ping.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.RecentSshSessions.Count).IsEqualTo(1);
        await Assert.That(result.RecentSshSessions[0].User).IsEqualTo("admin");
        await Assert.That(result.RecentSshSessions[0].Action).IsEqualTo("connect");
    }

    [Test]
    public async Task GetMachineDetailAsync_NoState_ReportsOfflineOnBothAxes()
    {
        // With no summary row the machine has never been heard from, which the fleet query behind
        // the machine list reports as Offline. The detail page agrees on both axes, because both
        // now come from the same row rather than from two separate clocks.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.HealthStatus).IsEqualTo(MachineHealthStatus.Offline);
        await Assert.That(result.IsOnline).IsFalse();
        await Assert.That(result.LastPing).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_ReportsSweptHealthColumn_NotRecomputedFromRawMetrics()
    {
        // The detail page is the one place a live recomputation might seem defensible, since the
        // user is looking at a single machine. It is not: a status here that disagrees with the
        // list the user clicked through from, and with the alert that may have sent them, is
        // worse than a value up to one sweep old. Raw metrics say Critical, the column says
        // Healthy, and the page reports the column.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, cpuPercent: 99, memoryPercent: 99, healthStatus: 0);
        state.FailedServices = 4;
        state.MaxDiskUsagePercent = 99;
        state.HasDiskHealthIssue = true;
        state.HasHardwareIssue = true;
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.HealthStatus).IsEqualTo(MachineHealthStatus.Healthy);
    }

    [Test]
    public async Task GetMachineDetailAsync_SweptColumnCritical_SurvivesAnOnlinePing()
    {
        // The mirror of the previous case. A live ping must not soften a swept Critical into
        // something friendlier; online-ness travels on IsOnline, not on the health status.
        using TestDatabaseFactory dbFactory = new();
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        MachineStateSummary state = TestDataBuilder.BuildMachineStateSummary(
            machineId: machine.Id, cpuPercent: 5, memoryPercent: 5, healthStatus: 2);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machine.Id, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.HealthStatus).IsEqualTo(MachineHealthStatus.Critical);
        await Assert.That(result.IsOnline).IsTrue();
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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

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
        MachineStateService service = new(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(
            1, 25, 1, null, null, "name", "desc", CancellationToken.None);

        await Assert.That(result.Machines.Count).IsEqualTo(2);
        await Assert.That(result.Machines[0].Name).IsEqualTo("zzz-host");
        await Assert.That(result.Machines[1].Name).IsEqualTo("aaa-host");
    }

    [Test]
    public async Task NormalizeAgentVersion_ReportedVersion_IsTrimmed()
    {
        await Assert.That(MachineStateService.NormalizeAgentVersion("1.16.0")).IsEqualTo("1.16.0");
        await Assert.That(MachineStateService.NormalizeAgentVersion("  1.16.0 ")).IsEqualTo("1.16.0");
    }

    [Test]
    public async Task NormalizeAgentVersion_MissingOrBlankVersion_IsNull()
    {
        // Intent: an agent that reported nothing usable is surfaced as "not reported" rather than
        // as an empty string that would render as a blank value in the UI.
        await Assert.That(MachineStateService.NormalizeAgentVersion(null)).IsNull();
        await Assert.That(MachineStateService.NormalizeAgentVersion("")).IsNull();
        await Assert.That(MachineStateService.NormalizeAgentVersion("   ")).IsNull();
    }

    // ========== Paused hosts ==========

    // Ids ascend in step with the statuses, so a cutoff at ids[n] pauses everything after position n.
    private static async Task<List<long>> SeedFleet(TestDatabaseFactory dbFactory, params short[] healthStatuses)
    {
        List<long> ids = [];
        foreach (short status in healthStatuses)
        {
            Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
            machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
            MachineStateSummary summary = TestDataBuilder.BuildMachineStateSummary(machineId: machine.Id, healthStatus: status);
            summary.SecurityUpdates = 2;
            await dbFactory.Context.InsertAsync(summary);
            ids.Add(machine.Id);
        }

        return ids;
    }

    private static MachineStateService ServiceWith(TestDatabaseFactory dbFactory, IQuotaPauseService quotaPause, QuotaPauseMetrics? metrics = null)
    {
        Dictionary<Type, object> services = new() { [typeof(IQuotaPauseService)] = quotaPause };

        return new MachineStateService(
            new TestServiceScopeFactory(dbFactory.Context, services),
            metrics ?? TestMetricsFactory.CreateQuotaPauseMetrics(),
            NullLogger<MachineStateService>.Instance);
    }

    [Test]
    public async Task GetFleetOverviewAsync_PausedMachines_AreCountedOnlyAsPausedAndStillInTheTotal()
    {
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedFleet(dbFactory, 0, 2, 1, 3);
        MachineStateService service = ServiceWith(dbFactory, TestQuotaPause.With(QuotaCutoff.After(ids[1]), 2));

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Summary.TotalMachines).IsEqualTo(4);
        await Assert.That(result.Summary.PausedCount).IsEqualTo(2);
        await Assert.That(result.Summary.OnlineMachines).IsEqualTo(2);
        await Assert.That(result.Summary.CriticalCount).IsEqualTo(1);
        await Assert.That(result.Summary.WarningCount).IsEqualTo(0);
        await Assert.That(result.Summary.OfflineCount).IsEqualTo(0);
        await Assert.That(result.TotalCount).IsEqualTo(4);
    }

    [Test]
    public async Task GetFleetOverviewAsync_Rows_CarryIsPausedFromTheCutoff()
    {
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedFleet(dbFactory, 0, 2, 1);
        MachineStateService service = ServiceWith(dbFactory, TestQuotaPause.With(QuotaCutoff.After(ids[1]), 1));

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        Dictionary<long, FleetMachineDto> byId = result.Machines.ToDictionary(m => m.Id);
        await Assert.That(byId[ids[0]].IsPaused).IsFalse();
        await Assert.That(byId[ids[1]].IsPaused).IsFalse();
        await Assert.That(byId[ids[2]].IsPaused).IsTrue();
    }

    [Test]
    [Arguments("warning", 0)]
    [Arguments("offline", 0)]
    [Arguments("critical", 0)]
    [Arguments("healthy", 1)]
    public async Task GetFleetOverviewAsync_HealthStatusFilter_NeverReturnsAPausedMachine(string status, int expectedRows)
    {
        // Only the first machine is active (healthy); the warning, critical and offline ones are past
        // the cutoff, so their stored health must not make them match.
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedFleet(dbFactory, 0, 1, 2, 3);
        MachineStateService service = ServiceWith(dbFactory, TestQuotaPause.With(QuotaCutoff.After(ids[0]), 3));

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(1, 25, 1, null, status, "name", "asc", CancellationToken.None);

        await Assert.That(result.Machines.Count).IsEqualTo(expectedRows);
        await Assert.That(result.TotalCount).IsEqualTo(expectedRows);
        await Assert.That(result.Machines.Any(m => m.IsPaused)).IsFalse();
    }

    [Test]
    public async Task GetFleetOverviewAsync_PausedStatusFilter_ReturnsOnlyPausedMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedFleet(dbFactory, 0, 1, 2, 3);
        MachineStateService service = ServiceWith(dbFactory, TestQuotaPause.With(QuotaCutoff.After(ids[1]), 2));

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(1, 25, 1, null, "Paused", "name", "asc", CancellationToken.None);

        await Assert.That(result.Machines.Select(m => m.Id).Order().ToList()).IsEquivalentTo(new List<long> { ids[2], ids[3] });
        await Assert.That(result.TotalCount).IsEqualTo(2);

        // The summary describes the whole fleet whatever the row filter is.
        await Assert.That(result.Summary.TotalMachines).IsEqualTo(4);
    }

    [Test]
    public async Task GetFleetOverviewAsync_SecurityUpdates_LeaveOutPausedMachines()
    {
        // Every seeded machine reports two security updates; only the two active ones are summed.
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedFleet(dbFactory, 0, 1, 0, 3);
        MachineStateService service = ServiceWith(dbFactory, TestQuotaPause.With(QuotaCutoff.After(ids[1]), 2));

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Summary.SecurityUpdates).IsEqualTo(4);

        // The rows still show what each machine reported; only the fleet total skips the paused ones.
        await Assert.That(result.Machines.Sum(m => m.SecurityUpdates)).IsEqualTo(8);
    }

    [Test]
    public async Task GetFleetOverviewAsync_SortByStatus_ListsPausedMachinesAfterTheActiveOnes()
    {
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedFleet(dbFactory, 2, 3, 0, 1);
        MachineStateService service = ServiceWith(dbFactory, TestQuotaPause.With(QuotaCutoff.After(ids[1]), 2));

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(1, 25, 1, null, null, "status", "asc", CancellationToken.None);
        PaginatedFleetOverviewDto reversed = await service.GetFleetOverviewAsync(1, 25, 1, null, null, "status", "desc", CancellationToken.None);

        // The paused machines froze at healthy and warning, which would otherwise sort ahead of the
        // critical and offline machines that are really in that state.
        await Assert.That(string.Join(",", result.Machines.Select(m => m.IsPaused))).IsEqualTo("False,False,True,True");
        await Assert.That(string.Join(",", result.Machines.Take(2).Select(m => m.Id))).IsEqualTo($"{ids[0]},{ids[1]}");

        // Reversed, the active machines' health order flips and the paused ones still come last.
        await Assert.That(string.Join(",", reversed.Machines.Select(m => m.IsPaused))).IsEqualTo("False,False,True,True");
        await Assert.That(string.Join(",", reversed.Machines.Take(2).Select(m => m.Id))).IsEqualTo($"{ids[1]},{ids[0]}");
    }

    [Test]
    public async Task GetFleetOverviewAsync_EveryMachinePaused_HasNoOnlineOrHealthCounts()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedFleet(dbFactory, 0, 2);
        MachineStateService service = ServiceWith(dbFactory, TestQuotaPause.With(QuotaCutoff.All, 2));

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Summary.PausedCount).IsEqualTo(2);
        await Assert.That(result.Summary.TotalMachines).IsEqualTo(2);
        await Assert.That(result.Summary.OnlineMachines).IsEqualTo(0);
        await Assert.That(result.Summary.CriticalCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetFleetOverviewAsync_PauseDecisionUnavailable_CountsEveryMachineByHealthAndRecordsTheFailure()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedFleet(dbFactory, 0, 3);
        IMeterFactory meterFactory = TestMetricsFactory.CreateMeterFactory();
        using MetricCollector<long> failures = new(meterFactory, VordMeter.Name, "vord.quota.pause_check_failures");
        MachineStateService service = ServiceWith(dbFactory, TestQuotaPause.Unavailable(), new QuotaPauseMetrics(meterFactory));

        PaginatedFleetOverviewDto result = await service.GetFleetOverviewAsync(1, 25, 1, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Summary.PausedCount).IsEqualTo(0);
        await Assert.That(result.Summary.OfflineCount).IsEqualTo(1);
        await Assert.That(result.Summary.OnlineMachines).IsEqualTo(1);
        await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(1);
        await Assert.That(failures.GetMeasurementSnapshot()[0].Tags["surface"]).IsEqualTo("display");
    }

    [Test]
    public async Task GetMachineDetailAsync_ReportsPausedAndTheOneBasedRankAmongRegisteredMachines()
    {
        using TestDatabaseFactory dbFactory = new();

        // A deleted machine and another tenant's machine in the id range must not push the rank down.
        Machine deleted = TestDataBuilder.BuildMachine(tenantId: 1);
        deleted.IsDeleted = true;
        await dbFactory.Context.InsertWithInt64IdentityAsync(deleted);
        await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 2));
        List<long> ids = await SeedFleet(dbFactory, 0, 0, 3);
        MachineStateService service = ServiceWith(dbFactory, TestQuotaPause.With(QuotaCutoff.After(ids[1]), 1));

        MachineDetailDto? second = await service.GetMachineDetailAsync(ids[1], 1, CancellationToken.None);
        MachineDetailDto? third = await service.GetMachineDetailAsync(ids[2], 1, CancellationToken.None);

        await Assert.That(second!.IsPaused).IsFalse();
        await Assert.That(second.ActiveRank).IsEqualTo(2);
        await Assert.That(third!.IsPaused).IsTrue();
        await Assert.That(third.ActiveRank).IsEqualTo(3);
    }

    [Test]
    public async Task GetMachineDetailAsync_PauseDecisionUnavailable_IsNotPausedButStillHasARank()
    {
        using TestDatabaseFactory dbFactory = new();
        List<long> ids = await SeedFleet(dbFactory, 0, 3);
        MachineStateService service = ServiceWith(dbFactory, TestQuotaPause.Unavailable());

        MachineDetailDto? detail = await service.GetMachineDetailAsync(ids[1], 1, CancellationToken.None);

        await Assert.That(detail!.IsPaused).IsFalse();
        await Assert.That(detail.ActiveRank).IsEqualTo(2);
    }

    [Test]
    public async Task Constructor_NullScopeFactory_Throws()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            _ = new MachineStateService(null!, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);

            return Task.CompletedTask;
        });

        await Assert.That(ex!.ParamName).IsEqualTo("scopeFactory");
    }

    [Test]
    public async Task Constructor_NullQuotaPauseMetrics_Throws()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            _ = new MachineStateService(scopeFactory, null!, NullLogger<MachineStateService>.Instance);

            return Task.CompletedTask;
        });

        await Assert.That(ex!.ParamName).IsEqualTo("quotaPauseMetrics");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            _ = new MachineStateService(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    // ========== GetMachineDetailAsync: Kubernetes block ==========

    private const string KubernetesFingerprintA = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";
    private const string KubernetesFingerprintB = "0f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a6978";

    private static string KubernetesPayload(
        string fingerprint,
        string nodeName = "cp-1",
        bool hasKubelet = true,
        bool controlPlane = true,
        string nodeNameSource = "certificate")
    {
        return $$"""
            {"distro":"k3s","kubelet_version":"v1.30.4+k3s1","node_name":"{{nodeName}}","node_name_source":"{{nodeNameSource}}","has_kubelet":{{(hasKubelet ? "true" : "false")}},"control_plane":{{(controlPlane ? "true" : "false")}},"ca_fingerprint":"{{fingerprint}}","admin_credential_present":true,"reporter_enrolled":false}
            """;
    }

    private static async Task<long> SeedKubernetesMachineAsync(TestDatabaseFactory dbFactory, int tenantId = 1, string? name = null)
    {
        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: name);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        return machine.Id;
    }

    private static async Task SeedKubernetesPayloadAsync(
        TestDatabaseFactory dbFactory, long machineId, string payload, int tenantId = 1, DateTimeOffset? receivedAt = null)
    {
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineTelemetry(
            machineId: machineId,
            tenantId: tenantId,
            telemetryType: TelemetryTypeIds.KubernetesNode,
            payload: payload,
            receivedAt: receivedAt));
    }

    private static async Task<long> SeedKubernetesClusterAsync(
        TestDatabaseFactory dbFactory, int tenantId, string fingerprint, string? displayName = null)
    {
        return await dbFactory.Context.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = fingerprint,
            DisplayName = displayName,
            Distro = "k3s",
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task<long> SeedKubernetesNodeAsync(
        TestDatabaseFactory dbFactory,
        int tenantId,
        long clusterId,
        string nodeName,
        long? machineId,
        bool controlPlane = false,
        bool mismatch = false)
    {
        return await dbFactory.Context.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = nodeName,
            MachineId = machineId,
            LocalControlPlane = controlPlane,
            FingerprintMismatch = mismatch,
            KubeletVersion = "v1.30.4+k3s1",
            LastLocalReportAt = DateTimeOffset.UtcNow,
        });
    }

    private static MachineStateService CreateKubernetesDetailService(TestDatabaseFactory dbFactory)
    {
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);

        return new MachineStateService(scopeFactory, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineStateService>.Instance);
    }

    [Test]
    public async Task GetMachineDetailAsync_NoKubernetesPayloadOrNode_HasNoKubernetesBlock()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedKubernetesMachineAsync(dbFactory);
        MachineStateService service = CreateKubernetesDetailService(dbFactory);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Kubernetes).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_LinkedNode_IsGroupedWithItsClusterAndLocalPayload()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedKubernetesMachineAsync(dbFactory, name: "cp-host");
        long clusterId = await SeedKubernetesClusterAsync(dbFactory, 1, KubernetesFingerprintA);
        long nodeId = await SeedKubernetesNodeAsync(dbFactory, 1, clusterId, "cp-1", machineId, controlPlane: true, mismatch: true);
        await SeedKubernetesNodeAsync(dbFactory, 1, clusterId, "worker-1", null);
        await SeedKubernetesPayloadAsync(dbFactory, machineId, KubernetesPayload(KubernetesFingerprintA));
        MachineStateService service = CreateKubernetesDetailService(dbFactory);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machineId, 1, CancellationToken.None);

        MachineKubernetesDto kubernetes = result!.Kubernetes!;
        await Assert.That(kubernetes.Grouping).IsEqualTo("grouped");
        await Assert.That(kubernetes.ClusterId).IsEqualTo(clusterId);
        await Assert.That(kubernetes.ClusterName).IsEqualTo("k3s cp-1");
        await Assert.That(kubernetes.Node).IsNotNull();
        await Assert.That(kubernetes.Node!.Id).IsEqualTo(nodeId);
        await Assert.That(kubernetes.Node.NodeName).IsEqualTo("cp-1");
        await Assert.That(kubernetes.Node.MachineId).IsEqualTo(machineId);
        await Assert.That(kubernetes.Node.MachineName).IsEqualTo("cp-host");
        await Assert.That(kubernetes.Node.ControlPlane).IsTrue();
        await Assert.That(kubernetes.Node.FingerprintMismatch).IsTrue();
        await Assert.That(kubernetes.Local).IsNotNull();
        await Assert.That(kubernetes.Local!.Distro).IsEqualTo("k3s");
        await Assert.That(kubernetes.Local.KubeletVersion).IsEqualTo("v1.30.4+k3s1");
        await Assert.That(kubernetes.Local.NodeName).IsEqualTo("cp-1");
        await Assert.That(kubernetes.Local.NodeNameSource).IsEqualTo("certificate");
        await Assert.That(kubernetes.Local.HasKubelet).IsTrue();
        await Assert.That(kubernetes.Local.ControlPlane).IsTrue();
        await Assert.That(kubernetes.Local.AdminCredentialPresent).IsTrue();
        await Assert.That(kubernetes.Local.ReporterEnrolled).IsFalse();
    }

    [Test]
    public async Task GetMachineDetailAsync_LinkedNodeInRenamedCluster_UsesTheDisplayName()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedKubernetesMachineAsync(dbFactory);
        long clusterId = await SeedKubernetesClusterAsync(dbFactory, 1, KubernetesFingerprintA, displayName: "Production");
        await SeedKubernetesNodeAsync(dbFactory, 1, clusterId, "cp-1", machineId, controlPlane: true);
        MachineStateService service = CreateKubernetesDetailService(dbFactory);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result!.Kubernetes!.ClusterName).IsEqualTo("Production");
    }

    [Test]
    public async Task GetMachineDetailAsync_LinkedNodeWithoutRecentPayload_KeepsTheNodeAndHasNoLocalBlock()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedKubernetesMachineAsync(dbFactory);
        long clusterId = await SeedKubernetesClusterAsync(dbFactory, 1, KubernetesFingerprintA);
        await SeedKubernetesNodeAsync(dbFactory, 1, clusterId, "cp-1", machineId, controlPlane: true);
        MachineStateService service = CreateKubernetesDetailService(dbFactory);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machineId, 1, CancellationToken.None);

        MachineKubernetesDto kubernetes = result!.Kubernetes!;
        await Assert.That(kubernetes.Local).IsNull();
        await Assert.That(kubernetes.Node).IsNotNull();
        await Assert.That(kubernetes.Grouping).IsEqualTo("grouped");
        await Assert.That(kubernetes.ClusterId).IsEqualTo(clusterId);
    }

    [Test]
    public async Task GetMachineDetailAsync_LinkedNode_WinsOverAnotherClusterHoldingThePayloadFingerprint()
    {
        // Intent: established membership decides the cluster. A payload fingerprint that matches a
        // different cluster is a mismatch for the node, not a reason to show the machine elsewhere.
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedKubernetesMachineAsync(dbFactory);
        long ownClusterId = await SeedKubernetesClusterAsync(dbFactory, 1, KubernetesFingerprintA);
        await SeedKubernetesClusterAsync(dbFactory, 1, KubernetesFingerprintB);
        await SeedKubernetesNodeAsync(dbFactory, 1, ownClusterId, "cp-1", machineId, controlPlane: true);
        await SeedKubernetesPayloadAsync(dbFactory, machineId, KubernetesPayload(KubernetesFingerprintB));
        MachineStateService service = CreateKubernetesDetailService(dbFactory);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result!.Kubernetes!.Grouping).IsEqualTo("grouped");
        await Assert.That(result.Kubernetes.ClusterId).IsEqualTo(ownClusterId);
    }

    [Test]
    public async Task GetMachineDetailAsync_NoNodeAndExactlyOneClusterHoldsTheFingerprint_IsGroupedWithThatCluster()
    {
        // A host without a kubelet, such as a control-plane-only machine, is not a node but its CA
        // still identifies the one cluster it belongs to.
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedKubernetesMachineAsync(dbFactory);
        long clusterId = await SeedKubernetesClusterAsync(dbFactory, 1, KubernetesFingerprintA);
        await SeedKubernetesNodeAsync(dbFactory, 1, clusterId, "worker-1", null);
        await SeedKubernetesPayloadAsync(dbFactory, machineId, KubernetesPayload(KubernetesFingerprintA, nodeName: "", hasKubelet: false));
        MachineStateService service = CreateKubernetesDetailService(dbFactory);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machineId, 1, CancellationToken.None);

        MachineKubernetesDto kubernetes = result!.Kubernetes!;
        await Assert.That(kubernetes.Grouping).IsEqualTo("grouped");
        await Assert.That(kubernetes.ClusterId).IsEqualTo(clusterId);
        await Assert.That(kubernetes.ClusterName).IsEqualTo("k3s worker-1");
        await Assert.That(kubernetes.Node).IsNull();
        await Assert.That(kubernetes.Local).IsNotNull();
        await Assert.That(kubernetes.Local!.HasKubelet).IsFalse();
    }

    [Test]
    public async Task GetMachineDetailAsync_NoNodeAndSeveralClustersHoldTheFingerprint_IsAmbiguous()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedKubernetesMachineAsync(dbFactory);
        await SeedKubernetesClusterAsync(dbFactory, 1, KubernetesFingerprintA);
        await SeedKubernetesClusterAsync(dbFactory, 1, KubernetesFingerprintA);
        await SeedKubernetesPayloadAsync(dbFactory, machineId, KubernetesPayload(KubernetesFingerprintA, hasKubelet: false));
        MachineStateService service = CreateKubernetesDetailService(dbFactory);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machineId, 1, CancellationToken.None);

        MachineKubernetesDto kubernetes = result!.Kubernetes!;
        await Assert.That(kubernetes.Grouping).IsEqualTo("ambiguous");
        await Assert.That(kubernetes.ClusterId).IsNull();
        await Assert.That(kubernetes.ClusterName).IsNull();
        await Assert.That(kubernetes.Node).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_PayloadWithoutFingerprint_IsUnknown()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedKubernetesMachineAsync(dbFactory);
        await SeedKubernetesClusterAsync(dbFactory, 1, KubernetesFingerprintA);
        await SeedKubernetesPayloadAsync(dbFactory, machineId, KubernetesPayload(""));
        MachineStateService service = CreateKubernetesDetailService(dbFactory);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machineId, 1, CancellationToken.None);

        MachineKubernetesDto kubernetes = result!.Kubernetes!;
        await Assert.That(kubernetes.Grouping).IsEqualTo("unknown");
        await Assert.That(kubernetes.ClusterId).IsNull();
        await Assert.That(kubernetes.ClusterName).IsNull();
        await Assert.That(kubernetes.Local).IsNotNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_NoClusterHoldsTheFingerprintYet_IsUngrouped()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedKubernetesMachineAsync(dbFactory);
        await SeedKubernetesClusterAsync(dbFactory, 1, KubernetesFingerprintB);
        await SeedKubernetesPayloadAsync(dbFactory, machineId, KubernetesPayload(KubernetesFingerprintA));
        MachineStateService service = CreateKubernetesDetailService(dbFactory);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machineId, 1, CancellationToken.None);

        MachineKubernetesDto kubernetes = result!.Kubernetes!;
        await Assert.That(kubernetes.Grouping).IsEqualTo("ungrouped");
        await Assert.That(kubernetes.ClusterId).IsNull();
        await Assert.That(kubernetes.ClusterName).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_ClusterOfAnotherTenantHoldingTheFingerprint_DoesNotGroupTheMachine()
    {
        // Intent: two tenants can report the same CA, and one must never be grouped into, or told the
        // name of, the other's cluster.
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedKubernetesMachineAsync(dbFactory);
        await SeedKubernetesClusterAsync(dbFactory, 2, KubernetesFingerprintA, displayName: "Other tenant's cluster");
        await SeedKubernetesPayloadAsync(dbFactory, machineId, KubernetesPayload(KubernetesFingerprintA, hasKubelet: false));
        MachineStateService service = CreateKubernetesDetailService(dbFactory);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machineId, 1, CancellationToken.None);

        MachineKubernetesDto kubernetes = result!.Kubernetes!;
        await Assert.That(kubernetes.Grouping).IsEqualTo("ungrouped");
        await Assert.That(kubernetes.ClusterId).IsNull();
        await Assert.That(kubernetes.ClusterName).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_PayloadOlderThanSevenDays_IsIgnored()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedKubernetesMachineAsync(dbFactory);
        await SeedKubernetesPayloadAsync(
            dbFactory, machineId, KubernetesPayload(KubernetesFingerprintA), receivedAt: DateTimeOffset.UtcNow.AddDays(-8));
        MachineStateService service = CreateKubernetesDetailService(dbFactory);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result!.Kubernetes).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_UnreadableKubernetesPayloadAndNoNode_HasNoKubernetesBlock()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedKubernetesMachineAsync(dbFactory);
        await SeedKubernetesPayloadAsync(dbFactory, machineId, "{ not json");
        MachineStateService service = CreateKubernetesDetailService(dbFactory);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result!.Kubernetes).IsNull();
    }

    [Test]
    public async Task GetMachineDetailAsync_KubernetesLocalBlock_NeverCarriesTheCaFingerprint()
    {
        // Intent: the CA fingerprint is used to group machines but is not part of what the machine page
        // shows or returns.
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedKubernetesMachineAsync(dbFactory);
        await SeedKubernetesPayloadAsync(dbFactory, machineId, KubernetesPayload(KubernetesFingerprintA));
        MachineStateService service = CreateKubernetesDetailService(dbFactory);

        MachineDetailDto? result = await service.GetMachineDetailAsync(machineId, 1, CancellationToken.None);

        string json = System.Text.Json.JsonSerializer.Serialize(result!.Kubernetes);
        await Assert.That(json.Contains(KubernetesFingerprintA, StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains("CaFingerprint", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }
}
