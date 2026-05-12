// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="HealthComputer"/>.
/// </summary>
public class HealthComputerTests
{
    // ========== Offline ==========

    [Test]
    public async Task Compute_OfflineMachine_ReturnsOffline()
    {
        MachineStateSummary state = new() { MachineId = 1 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: false);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Offline);
    }

    // ========== Critical checks ==========

    [Test]
    public async Task Compute_CpuAt95_ReturnsCritical()
    {
        MachineStateSummary state = new() { MachineId = 1, CpuUsagePercent = 95 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task Compute_MemoryAt95_ReturnsCritical()
    {
        MachineStateSummary state = new() { MachineId = 1, MemoryUsagePercent = 95 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task Compute_FailedServices_ReturnsCritical()
    {
        MachineStateSummary state = new() { MachineId = 1, FailedServices = 1 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task Compute_DiskAt95Percent_ReturnsCritical()
    {
        MachineStateSummary state = new()
        {
            MachineId = 1,
            MaxDiskUsagePercent = 96,
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task Compute_DiskHealthIssue_ReturnsCritical()
    {
        MachineStateSummary state = new()
        {
            MachineId = 1,
            HasDiskHealthIssue = true,
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task Compute_HardwareIssue_ReturnsCritical()
    {
        MachineStateSummary state = new()
        {
            MachineId = 1,
            HasHardwareIssue = true,
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    // ========== Warning checks ==========

    [Test]
    public async Task Compute_CpuAt80_ReturnsWarning()
    {
        MachineStateSummary state = new() { MachineId = 1, CpuUsagePercent = 80 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Warning);
    }

    [Test]
    public async Task Compute_MemoryAt80_ReturnsWarning()
    {
        MachineStateSummary state = new() { MachineId = 1, MemoryUsagePercent = 80 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Warning);
    }

    [Test]
    public async Task Compute_DiskAt80Percent_ReturnsWarning()
    {
        MachineStateSummary state = new()
        {
            MachineId = 1,
            MaxDiskUsagePercent = 85,
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Warning);
    }

    // ========== Healthy ==========

    [Test]
    public async Task Compute_AllNominal_ReturnsHealthy()
    {
        MachineStateSummary state = new()
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
        MachineStateSummary state = new() { MachineId = 1 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Healthy);
    }

    [Test]
    public async Task Compute_NoDiskHealthIssue_ReturnsHealthy()
    {
        MachineStateSummary state = new()
        {
            MachineId = 1,
            CpuUsagePercent = 40,
            MemoryUsagePercent = 50,
            HasDiskHealthIssue = false,
            HasHardwareIssue = false,
            MaxDiskUsagePercent = 30,
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Healthy);
    }

    // ========== Edge cases ==========

    [Test]
    public async Task Compute_CpuAt79_ReturnsHealthy()
    {
        MachineStateSummary state = new() { MachineId = 1, CpuUsagePercent = 79 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Healthy);
    }

    [Test]
    public async Task Compute_CpuAt94_ReturnsWarning()
    {
        MachineStateSummary state = new() { MachineId = 1, CpuUsagePercent = 94 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Warning);
    }

    [Test]
    public async Task Compute_DiskAt79Percent_ReturnsHealthy()
    {
        MachineStateSummary state = new()
        {
            MachineId = 1,
            MaxDiskUsagePercent = 79,
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Healthy);
    }

    [Test]
    public async Task Compute_NullHardwareFlags_ReturnsHealthy()
    {
        MachineStateSummary state = new()
        {
            MachineId = 1,
            HasDiskHealthIssue = null,
            HasHardwareIssue = null,
        };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Healthy);
    }
}
