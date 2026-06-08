// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Commands;
using Framlux.FleetManagement.Services.Core.DataExport;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Hangfire;

namespace Framlux.FleetManagement.Test.Hangfire;

/// <summary>
/// H10 tests: every job-entry method carries the right <see cref="QueueAttribute"/> so the
/// worker's prioritized queue list ("critical", "default", "long") routes work correctly.
/// A regression that drops or misnames an attribute would silently send long-running work to
/// the critical queue, starving per-minute jobs.
/// </summary>
public sealed class JobQueueAttributesTests
{
    private static string? GetQueueName(Type jobType, string methodName)
    {
        MethodInfo? method = jobType.GetMethod(methodName);
        if (method is null)
        {
            return "<method-not-found>";
        }
        QueueAttribute? attr = method.GetCustomAttribute<QueueAttribute>();

        return attr?.Queue;
    }

    [Test]
    public async Task AlertEvaluationJob_RunAsync_IsCritical()
    {
        string? q = GetQueueName(typeof(AlertEvaluationJob), "RunAsync");

        await Assert.That(q).IsEqualTo("critical");
    }

    [Test]
    public async Task HealthSweepCoordinatorJob_RunAsync_IsCritical()
    {
        string? q = GetQueueName(typeof(HealthSweepCoordinatorJob), "RunAsync");

        await Assert.That(q).IsEqualTo("critical");
    }

    [Test]
    public async Task HealthSweepCoordinatorJob_FanOutAsync_IsCritical()
    {
        string? q = GetQueueName(typeof(HealthSweepCoordinatorJob), "FanOutAsync");

        await Assert.That(q).IsEqualTo("critical");
    }

    [Test]
    public async Task HealthSweepTenantJob_RunAsync_IsCritical()
    {
        string? q = GetQueueName(typeof(HealthSweepTenantJob), "RunAsync");

        await Assert.That(q).IsEqualTo("critical");
    }

    [Test]
    public async Task RemoteCommandExpiryJob_RunAsync_IsCritical()
    {
        string? q = GetQueueName(typeof(RemoteCommandExpiryJob), "RunAsync");

        await Assert.That(q).IsEqualTo("critical");
    }

    [Test]
    public async Task DataExportProcessingJob_RunAsync_IsLong()
    {
        string? q = GetQueueName(typeof(DataExportProcessingJob), "RunAsync");

        await Assert.That(q).IsEqualTo("long");
    }

    [Test]
    public async Task DataExportProcessingJob_ProcessSingleAsync_IsLong()
    {
        string? q = GetQueueName(typeof(DataExportProcessingJob), "ProcessSingleAsync");

        await Assert.That(q).IsEqualTo("long");
    }

    [Test]
    public async Task DataExportCleanupJob_RunAsync_IsLong()
    {
        string? q = GetQueueName(typeof(DataExportCleanupJob), "RunAsync");

        await Assert.That(q).IsEqualTo("long");
    }

    [Test]
    public async Task PartitionManagementJob_RunAsync_IsLong()
    {
        string? q = GetQueueName(typeof(PartitionManagementJob), "RunAsync");

        await Assert.That(q).IsEqualTo("long");
    }

    [Test]
    public async Task StripeSyncJob_RunAsync_UsesDefaultQueue()
    {
        string? q = GetQueueName(typeof(StripeSyncJob), "RunAsync");

        // Default queue = no [Queue] attribute.
        await Assert.That(q).IsNull();
    }

    [Test]
    public async Task UsageHeartbeatJob_RunAsync_UsesDefaultQueue()
    {
        string? q = GetQueueName(typeof(UsageHeartbeatJob), "RunAsync");

        await Assert.That(q).IsNull();
    }

    [Test]
    public async Task AlertConditionStateCleanupJob_RunAsync_UsesDefaultQueue()
    {
        string? q = GetQueueName(typeof(AlertConditionStateCleanupJob), "RunAsync");

        await Assert.That(q).IsNull();
    }

    [Test]
    public async Task IntegrationDeliveryJob_DeliverAsync_UsesDefaultQueue()
    {
        string? q = GetQueueName(typeof(IntegrationDeliveryJob), "DeliverAsync");

        await Assert.That(q).IsNull();
    }
}
