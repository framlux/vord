// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Commands;
using Framlux.FleetManagement.Services.Core.DataExport;
using Framlux.FleetManagement.Services.Core.Extensions;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.Test.Hangfire;

/// <summary>
/// H8 tests: <see cref="ServiceCollectionExtensions.AddHangfireJobTypes"/> registers every
/// Hangfire-invoked job class as Scoped, with feature gating preserved. Resolves at the
/// registration layer (no need to wire every transitive dependency) — Hangfire's job
/// activator only requires the type to be in the container; the actual instantiation happens
/// per-invocation against the scope Hangfire creates.
/// </summary>
public sealed class HangfireJobTypesTests
{
    private static bool IsRegistered<T>(IServiceCollection services)
    {
        foreach (ServiceDescriptor sd in services)
        {
            if (sd.ServiceType == typeof(T))
            {
                return true;
            }
        }

        return false;
    }

    private static ServiceLifetime GetLifetime<T>(IServiceCollection services)
    {
        foreach (ServiceDescriptor sd in services)
        {
            if (sd.ServiceType == typeof(T))
            {
                return sd.Lifetime;
            }
        }

        throw new InvalidOperationException($"{typeof(T).Name} is not registered.");
    }

    [Test]
    public async Task AllFeaturesEnabled_EveryJobRegistered()
    {
        ServiceCollection services = new();

        services.AddHangfireJobTypes(billingEnabled: true, objectStorageEnabled: true);

        await Assert.That(IsRegistered<RemoteCommandExpiryJob>(services)).IsTrue();
        await Assert.That(IsRegistered<PartitionManagementJob>(services)).IsTrue();
        await Assert.That(IsRegistered<HealthSweepTenantJob>(services)).IsTrue();
        await Assert.That(IsRegistered<HealthSweepCoordinatorJob>(services)).IsTrue();
        await Assert.That(IsRegistered<AlertEvaluationJob>(services)).IsTrue();
        await Assert.That(IsRegistered<AlertConditionStateCleanupJob>(services)).IsTrue();
        await Assert.That(IsRegistered<IntegrationDeliveryJob>(services)).IsTrue();
        await Assert.That(IsRegistered<DataExportProcessingJob>(services)).IsTrue();
        await Assert.That(IsRegistered<DataExportCleanupJob>(services)).IsTrue();
        await Assert.That(IsRegistered<StripeSyncJob>(services)).IsTrue();
        await Assert.That(IsRegistered<UsageHeartbeatJob>(services)).IsTrue();
    }

    [Test]
    public async Task EveryJob_RegisteredAsScoped()
    {
        ServiceCollection services = new();

        services.AddHangfireJobTypes(billingEnabled: true, objectStorageEnabled: true);

        await Assert.That(GetLifetime<RemoteCommandExpiryJob>(services)).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(GetLifetime<PartitionManagementJob>(services)).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(GetLifetime<HealthSweepTenantJob>(services)).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(GetLifetime<HealthSweepCoordinatorJob>(services)).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(GetLifetime<AlertEvaluationJob>(services)).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(GetLifetime<AlertConditionStateCleanupJob>(services)).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(GetLifetime<IntegrationDeliveryJob>(services)).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(GetLifetime<DataExportProcessingJob>(services)).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(GetLifetime<DataExportCleanupJob>(services)).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(GetLifetime<StripeSyncJob>(services)).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(GetLifetime<UsageHeartbeatJob>(services)).IsEqualTo(ServiceLifetime.Scoped);
    }

    [Test]
    public async Task ObjectStorageDisabled_DataExportJobsNotRegistered()
    {
        ServiceCollection services = new();

        services.AddHangfireJobTypes(billingEnabled: true, objectStorageEnabled: false);

        await Assert.That(IsRegistered<DataExportProcessingJob>(services)).IsFalse();
        await Assert.That(IsRegistered<DataExportCleanupJob>(services)).IsFalse();
    }

    [Test]
    public async Task BillingDisabled_BillingJobsNotRegistered()
    {
        ServiceCollection services = new();

        services.AddHangfireJobTypes(billingEnabled: false, objectStorageEnabled: true);

        await Assert.That(IsRegistered<StripeSyncJob>(services)).IsFalse();
        await Assert.That(IsRegistered<UsageHeartbeatJob>(services)).IsFalse();
    }

    [Test]
    public async Task NullServices_ThrowsArgumentNullException()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            ServiceCollectionExtensions.AddHangfireJobTypes(null!, true, true);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }
}
