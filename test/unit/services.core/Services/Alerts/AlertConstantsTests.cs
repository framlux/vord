// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Services.Core.Alerts;

namespace Framlux.FleetManagement.Test.Services.Alerts;

/// <summary>
/// Unit tests for <see cref="AlertConstants"/> metric classification methods.
/// </summary>
public sealed class AlertConstantsTests
{
    [Test]
    public async Task GetMinimumDurationMinutes_CpuUsage_Returns5()
    {
        int result = AlertConstants.GetMinimumDurationMinutes(AlertMetric.CpuUsage);

        await Assert.That(result).IsEqualTo(5);
    }

    [Test]
    public async Task GetMinimumDurationMinutes_MemoryUsage_Returns5()
    {
        int result = AlertConstants.GetMinimumDurationMinutes(AlertMetric.MemoryUsage);

        await Assert.That(result).IsEqualTo(5);
    }

    [Test]
    public async Task GetMinimumDurationMinutes_DiskUsage_Returns5()
    {
        int result = AlertConstants.GetMinimumDurationMinutes(AlertMetric.DiskUsage);

        await Assert.That(result).IsEqualTo(5);
    }

    [Test]
    public async Task GetMinimumDurationMinutes_MachineOffline_Returns1()
    {
        int result = AlertConstants.GetMinimumDurationMinutes(AlertMetric.MachineOffline);

        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task GetMinimumDurationMinutes_FailedServices_Returns1()
    {
        int result = AlertConstants.GetMinimumDurationMinutes(AlertMetric.FailedServices);

        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task GetMinimumDurationMinutes_SecurityUpdates_Returns1()
    {
        int result = AlertConstants.GetMinimumDurationMinutes(AlertMetric.SecurityUpdates);

        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task GetMinimumDurationMinutes_DiskHealth_Returns1()
    {
        int result = AlertConstants.GetMinimumDurationMinutes(AlertMetric.DiskHealth);

        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task GetMinimumDurationMinutes_SshConnection_Returns0()
    {
        int result = AlertConstants.GetMinimumDurationMinutes(AlertMetric.SshConnection);

        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task IsEventMetric_SshConnection_ReturnsTrue()
    {
        bool result = AlertConstants.IsEventMetric(AlertMetric.SshConnection);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsEventMetric_CpuUsage_ReturnsFalse()
    {
        bool result = AlertConstants.IsEventMetric(AlertMetric.CpuUsage);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsEventMetric_MemoryUsage_ReturnsFalse()
    {
        bool result = AlertConstants.IsEventMetric(AlertMetric.MemoryUsage);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsEventMetric_MachineOffline_ReturnsFalse()
    {
        bool result = AlertConstants.IsEventMetric(AlertMetric.MachineOffline);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsEventMetric_FailedServices_ReturnsFalse()
    {
        bool result = AlertConstants.IsEventMetric(AlertMetric.FailedServices);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsEventMetric_DiskHealth_ReturnsFalse()
    {
        bool result = AlertConstants.IsEventMetric(AlertMetric.DiskHealth);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsEventMetric_DiskUsage_ReturnsFalse()
    {
        bool result = AlertConstants.IsEventMetric(AlertMetric.DiskUsage);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsEventMetric_SecurityUpdates_ReturnsFalse()
    {
        bool result = AlertConstants.IsEventMetric(AlertMetric.SecurityUpdates);

        await Assert.That(result).IsFalse();
    }
}
