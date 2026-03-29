// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using System.Text.Json;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="HealthComputer"/>.
/// </summary>
public class HealthComputerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    // ========== Offline ==========

    [Test]
    public async Task Compute_OfflineMachine_ReturnsOffline()
    {
        MachineState state = new() { MachineId = 1 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: false);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Offline);
    }

    // ========== Critical checks ==========

    [Test]
    public async Task Compute_CpuAt95_ReturnsCritical()
    {
        MachineState state = new() { MachineId = 1, CpuUsagePercent = 95 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task Compute_MemoryAt95_ReturnsCritical()
    {
        MachineState state = new() { MachineId = 1, MemoryUsagePercent = 95 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task Compute_FailedServices_ReturnsCritical()
    {
        MachineState state = new() { MachineId = 1, FailedServices = 1 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task Compute_DiskAt95Percent_ReturnsCritical()
    {
        List<DiskUsageEntryDto> disks = [new() { Device = "/dev/sda1", Path = "/", UsagePercent = 96 }];
        MachineState state = new()
        {
            MachineId = 1,
            DiskUsages = JsonSerializer.Serialize(disks, JsonOptions),
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task Compute_SmartFailed_ReturnsCritical()
    {
        HardwareHealthPayload hw = new()
        {
            DiskSmart = [new() { Device = "/dev/sda", HealthStatus = "FAILED" }],
        };
        MachineState state = new()
        {
            MachineId = 1,
            HardwareHealth = JsonSerializer.Serialize(hw, JsonOptions),
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task Compute_FanRpmZero_ReturnsCritical()
    {
        HardwareHealthPayload hw = new()
        {
            Fans = [new() { Name = "cpu_fan", Rpm = 0 }],
        };
        MachineState state = new()
        {
            MachineId = 1,
            HardwareHealth = JsonSerializer.Serialize(hw, JsonOptions),
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task Compute_PowerSupplyNotOk_ReturnsCritical()
    {
        HardwareHealthPayload hw = new()
        {
            PowerSupplies = [new() { Name = "psu1", Status = "FAILED" }],
        };
        MachineState state = new()
        {
            MachineId = 1,
            HardwareHealth = JsonSerializer.Serialize(hw, JsonOptions),
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    // ========== Warning checks ==========

    [Test]
    public async Task Compute_CpuAt80_ReturnsWarning()
    {
        MachineState state = new() { MachineId = 1, CpuUsagePercent = 80 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Warning);
    }

    [Test]
    public async Task Compute_MemoryAt80_ReturnsWarning()
    {
        MachineState state = new() { MachineId = 1, MemoryUsagePercent = 80 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Warning);
    }

    [Test]
    public async Task Compute_DiskAt80Percent_ReturnsWarning()
    {
        List<DiskUsageEntryDto> disks = [new() { Device = "/dev/sda1", Path = "/", UsagePercent = 85 }];
        MachineState state = new()
        {
            MachineId = 1,
            DiskUsages = JsonSerializer.Serialize(disks, JsonOptions),
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Warning);
    }

    [Test]
    public async Task Compute_DiskWearoutAbove80_ReturnsWarning()
    {
        HardwareHealthPayload hw = new()
        {
            DiskSmart = [new() { Device = "/dev/sda", HealthStatus = "PASSED", WearoutPercent = 85 }],
        };
        MachineState state = new()
        {
            MachineId = 1,
            HardwareHealth = JsonSerializer.Serialize(hw, JsonOptions),
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Warning);
    }

    [Test]
    public async Task Compute_DiskTemperatureAt55_ReturnsWarning()
    {
        HardwareHealthPayload hw = new()
        {
            DiskSmart = [new() { Device = "/dev/sda", HealthStatus = "PASSED", TemperatureCelsius = 55 }],
        };
        MachineState state = new()
        {
            MachineId = 1,
            HardwareHealth = JsonSerializer.Serialize(hw, JsonOptions),
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Warning);
    }

    // ========== Healthy ==========

    [Test]
    public async Task Compute_AllNominal_ReturnsHealthy()
    {
        MachineState state = new()
        {
            MachineId = 1,
            CpuUsagePercent = 30,
            MemoryUsagePercent = 50,
            FailedServices = 0,
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Healthy);
    }

    [Test]
    public async Task Compute_NoMetrics_ReturnsHealthy()
    {
        MachineState state = new() { MachineId = 1 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Healthy);
    }

    [Test]
    public async Task Compute_InvalidDiskJson_ReturnsHealthy()
    {
        MachineState state = new()
        {
            MachineId = 1,
            DiskUsages = "not-valid-json",
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Healthy);
    }

    [Test]
    public async Task Compute_InvalidHardwareHealthJson_ReturnsHealthy()
    {
        MachineState state = new()
        {
            MachineId = 1,
            HardwareHealth = "not-valid-json",
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Healthy);
    }

    [Test]
    public async Task Compute_HealthyHardware_ReturnsHealthy()
    {
        HardwareHealthPayload hw = new()
        {
            Fans = [new() { Name = "cpu_fan", Rpm = 1500 }],
            PowerSupplies = [new() { Name = "psu1", Status = "ok" }],
            DiskSmart = [new() { Device = "/dev/sda", HealthStatus = "PASSED", TemperatureCelsius = 30, WearoutPercent = 10 }],
        };
        MachineState state = new()
        {
            MachineId = 1,
            CpuUsagePercent = 40,
            MemoryUsagePercent = 50,
            HardwareHealth = JsonSerializer.Serialize(hw, JsonOptions),
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Healthy);
    }

    // ========== Edge cases ==========

    [Test]
    public async Task Compute_CpuAt79_ReturnsHealthy()
    {
        MachineState state = new() { MachineId = 1, CpuUsagePercent = 79 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Healthy);
    }

    [Test]
    public async Task Compute_CpuAt94_ReturnsWarning()
    {
        MachineState state = new() { MachineId = 1, CpuUsagePercent = 94 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Warning);
    }
}
