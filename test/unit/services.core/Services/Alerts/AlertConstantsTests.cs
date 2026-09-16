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

    /// <summary>
    /// The numeric value is persisted on every AlertRules row, so it is part of the schema rather
    /// than an implementation detail. TelemetryStale already occupies 9.
    /// </summary>
    [Test]
    public async Task FailedSshLogin_HasValue10()
    {
        short actual = (short)AlertMetric.FailedSshLogin;

        await Assert.That(actual).IsEqualTo((short)10);
    }

    /// <summary>
    /// Evaluations happen on the platform cadence, so a rule may not ask for a window shorter than
    /// it: such a rule would inspect its own duration out of every cadence and silently miss an
    /// attack that ended in the gap — a tighter rule detecting less than the built-in.
    /// </summary>
    [Test]
    public async Task GetMinimumDurationMinutes_FailedSshLogin_IsTheEvaluationCadence()
    {
        int result = AlertConstants.GetMinimumDurationMinutes(AlertMetric.FailedSshLogin);

        await Assert.That(result).IsEqualTo(AlertConstants.FailedSshLoginWindowMinutes);
        await Assert.That(result).IsEqualTo(5);
    }

    /// <summary>
    /// Both SSH metrics are evaluated at ingest, which is what keeps the periodic evaluator from
    /// trying to read them off a MachineStateSummary field that does not exist.
    /// </summary>
    [Test]
    public async Task IsEventMetric_FailedSshLogin_ReturnsTrue()
    {
        bool result = AlertConstants.IsEventMetric(AlertMetric.FailedSshLogin);

        await Assert.That(result).IsTrue();
    }

    /// <summary>
    /// Zero duration is a separate notion from ingest-time evaluation: a failed-login count is
    /// measured over a window, so it is an event metric that nonetheless needs a non-zero duration.
    /// </summary>
    [Test]
    public async Task RequiresZeroDuration_SshConnection_ReturnsTrue()
    {
        bool result = AlertConstants.RequiresZeroDuration(AlertMetric.SshConnection);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task RequiresZeroDuration_FailedSshLogin_ReturnsFalse()
    {
        bool result = AlertConstants.RequiresZeroDuration(AlertMetric.FailedSshLogin);

        await Assert.That(result).IsFalse();
    }

    [Test]
    [Arguments(AlertMetric.CpuUsage)]
    [Arguments(AlertMetric.MemoryUsage)]
    [Arguments(AlertMetric.DiskUsage)]
    [Arguments(AlertMetric.MachineOffline)]
    [Arguments(AlertMetric.FailedServices)]
    [Arguments(AlertMetric.SecurityUpdates)]
    [Arguments(AlertMetric.DiskHealth)]
    [Arguments(AlertMetric.TelemetryStale)]
    public async Task RequiresZeroDuration_ThresholdMetrics_ReturnFalse(AlertMetric metric)
    {
        bool result = AlertConstants.RequiresZeroDuration(metric);

        await Assert.That(result).IsFalse();
    }

    /// <summary>
    /// SshConnection is the only point-in-time metric. Stated as a sweep so a future metric cannot
    /// be quietly folded back into the zero-duration set.
    /// </summary>
    [Test]
    public async Task RequiresZeroDuration_SshConnectionIsTheOnlyMetric()
    {
        List<AlertMetric> zeroDuration = [.. Enum.GetValues<AlertMetric>().Where(AlertConstants.RequiresZeroDuration)];

        await Assert.That(zeroDuration.Count).IsEqualTo(1);
        await Assert.That(zeroDuration.Contains(AlertMetric.SshConnection)).IsTrue();
    }

    [Test]
    public async Task IsEventMetric_BothSshMetricsAreTheOnlyEventMetrics()
    {
        List<AlertMetric> eventMetrics = [.. Enum.GetValues<AlertMetric>().Where(AlertConstants.IsEventMetric)];

        await Assert.That(eventMetrics.Count).IsEqualTo(2);
        await Assert.That(eventMetrics.Contains(AlertMetric.SshConnection)).IsTrue();
        await Assert.That(eventMetrics.Contains(AlertMetric.FailedSshLogin)).IsTrue();
    }
}
