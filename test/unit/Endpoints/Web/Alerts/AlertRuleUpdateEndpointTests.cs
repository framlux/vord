// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Alerts;

/// <summary>
/// Unit tests for the metric constraint validation logic in <see cref="AlertRuleUpdateEndpoint"/>.
/// </summary>
public sealed class AlertRuleUpdateEndpointTests
{
    // --- Percentage Metric Threshold Validation ---

    [Test]
    public async Task ValidateMetricConstraints_CpuUsage_ThresholdAbove100_ReturnsError()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.CpuUsage, 101, 5);

        await Assert.That(result).IsEqualTo("Threshold for percentage metrics must be between 0 and 100");
    }

    [Test]
    public async Task ValidateMetricConstraints_CpuUsage_NegativeThreshold_ReturnsError()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.CpuUsage, -1, 5);

        await Assert.That(result).IsEqualTo("Threshold for percentage metrics must be between 0 and 100");
    }

    [Test]
    public async Task ValidateMetricConstraints_CpuUsage_ValidThreshold_ReturnsNull()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.CpuUsage, 80, 5);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ValidateMetricConstraints_MemoryUsage_Threshold100_ReturnsNull()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.MemoryUsage, 100, 5);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ValidateMetricConstraints_DiskUsage_Threshold0_ReturnsNull()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.DiskUsage, 0, 5);

        await Assert.That(result).IsNull();
    }

    // --- Binary Metric Threshold Validation ---

    [Test]
    public async Task ValidateMetricConstraints_MachineOffline_Threshold2_ReturnsError()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.MachineOffline, 2, 1);

        await Assert.That(result).IsEqualTo("Threshold for this metric must be 0 or 1");
    }

    [Test]
    public async Task ValidateMetricConstraints_DiskHealth_Threshold0_ReturnsNull()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.DiskHealth, 0, 1);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ValidateMetricConstraints_DiskHealth_Threshold1_ReturnsNull()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.DiskHealth, 1, 1);

        await Assert.That(result).IsNull();
    }

    // --- Count Metric Threshold Validation ---

    [Test]
    public async Task ValidateMetricConstraints_FailedServices_NegativeThreshold_ReturnsError()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.FailedServices, -1, 1);

        await Assert.That(result).IsEqualTo("Threshold must be zero or positive");
    }

    [Test]
    public async Task ValidateMetricConstraints_SecurityUpdates_ZeroThreshold_ReturnsNull()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.SecurityUpdates, 0, 1);

        await Assert.That(result).IsNull();
    }

    // --- Event Metric Duration Validation ---

    [Test]
    public async Task ValidateMetricConstraints_SshConnection_NonZeroDuration_ReturnsError()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.SshConnection, 1, 1);

        await Assert.That(result).IsEqualTo("Duration must be zero for event-based metrics");
    }

    [Test]
    public async Task ValidateMetricConstraints_SshConnection_ZeroDuration_ReturnsNull()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.SshConnection, 1, 0);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ValidateMetricConstraints_SshConnection_NegativeDuration_ReturnsEventError()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.SshConnection, 1, -1);

        await Assert.That(result).IsNotNull();
    }

    // --- Volatile Metric Duration Validation ---

    [Test]
    public async Task ValidateMetricConstraints_CpuUsage_DurationBelowMinimum_ReturnsError()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.CpuUsage, 80, 4);

        await Assert.That(result).IsEqualTo("Duration must be at least 5 minutes for CpuUsage alerts");
    }

    [Test]
    public async Task ValidateMetricConstraints_CpuUsage_DurationAtMinimum_ReturnsNull()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.CpuUsage, 80, 5);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ValidateMetricConstraints_CpuUsage_DurationAboveMinimum_ReturnsNull()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.CpuUsage, 80, 10);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ValidateMetricConstraints_CpuUsage_ZeroDuration_ReturnsError()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.CpuUsage, 80, 0);

        await Assert.That(result).IsEqualTo("Duration must be at least 5 minutes for CpuUsage alerts");
    }

    [Test]
    public async Task ValidateMetricConstraints_MemoryUsage_DurationBelowMinimum_ReturnsError()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.MemoryUsage, 80, 3);

        await Assert.That(result).IsEqualTo("Duration must be at least 5 minutes for MemoryUsage alerts");
    }

    [Test]
    public async Task ValidateMetricConstraints_DiskUsage_DurationBelowMinimum_ReturnsError()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.DiskUsage, 80, 2);

        await Assert.That(result).IsEqualTo("Duration must be at least 5 minutes for DiskUsage alerts");
    }

    // --- State Metric Duration Validation ---

    [Test]
    public async Task ValidateMetricConstraints_MachineOffline_ZeroDuration_ReturnsError()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.MachineOffline, 1, 0);

        await Assert.That(result).IsEqualTo("Duration must be at least 1 minutes for MachineOffline alerts");
    }

    [Test]
    public async Task ValidateMetricConstraints_MachineOffline_DurationAtMinimum_ReturnsNull()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.MachineOffline, 1, 1);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ValidateMetricConstraints_FailedServices_ZeroDuration_ReturnsError()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.FailedServices, 0, 0);

        await Assert.That(result).IsEqualTo("Duration must be at least 1 minutes for FailedServices alerts");
    }

    [Test]
    public async Task ValidateMetricConstraints_DiskHealth_DurationAtMinimum_ReturnsNull()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.DiskHealth, 1, 1);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ValidateMetricConstraints_SecurityUpdates_DurationAboveMinimum_ReturnsNull()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.SecurityUpdates, 0, 5);

        await Assert.That(result).IsNull();
    }

    // --- Boundary: Negative Duration ---

    [Test]
    public async Task ValidateMetricConstraints_CpuUsage_NegativeDuration_ReturnsError()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.CpuUsage, 80, -1);

        await Assert.That(result).IsEqualTo("Duration must be at least 5 minutes for CpuUsage alerts");
    }

    [Test]
    public async Task ValidateMetricConstraints_MachineOffline_NegativeDuration_ReturnsError()
    {
        string? result = AlertRuleUpdateEndpoint.ValidateMetricConstraints(AlertMetric.MachineOffline, 1, -5);

        await Assert.That(result).IsEqualTo("Duration must be at least 1 minutes for MachineOffline alerts");
    }
}
