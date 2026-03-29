// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="HealthComputer"/>.
/// </summary>
public class HealthComputationTests
{
    [Test]
    public async Task Offline_WhenNotOnline()
    {
        MachineState state = new() { MachineId = 1, CpuUsagePercent = 10, MemoryUsagePercent = 10 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: false);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Offline);
    }

    [Test]
    public async Task Critical_WhenCpuOver95()
    {
        MachineState state = new() { MachineId = 1, CpuUsagePercent = 96, MemoryUsagePercent = 10 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task Critical_WhenMemoryOver95()
    {
        MachineState state = new() { MachineId = 1, CpuUsagePercent = 10, MemoryUsagePercent = 96 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task Warning_WhenCpuOver80()
    {
        MachineState state = new() { MachineId = 1, CpuUsagePercent = 85, MemoryUsagePercent = 50 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Warning);
    }

    [Test]
    public async Task Warning_WhenMemoryOver80()
    {
        MachineState state = new() { MachineId = 1, CpuUsagePercent = 50, MemoryUsagePercent = 85 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Warning);
    }

    [Test]
    public async Task Healthy_WhenNominal()
    {
        MachineState state = new() { MachineId = 1, CpuUsagePercent = 30, MemoryUsagePercent = 45 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Healthy);
    }

    [Test]
    public async Task Critical_WhenFailedServices()
    {
        MachineState state = new() { MachineId = 1, CpuUsagePercent = 10, MemoryUsagePercent = 10, FailedServices = 2 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task Healthy_WhenNullMetrics()
    {
        MachineState state = new() { MachineId = 1 };

        MachineHealthStatus result = HealthComputer.Compute(state, isOnline: true);

        await Assert.That(result).IsEqualTo(MachineHealthStatus.Healthy);
    }
}
