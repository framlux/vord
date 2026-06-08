// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Hangfire;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="UsageHeartbeatJob"/>. Verifies that paid subscriptions are reported to the
/// billing API per-tenant, missing tenants are skipped, per-tenant errors are swallowed (matching
/// the predecessor BackgroundService), and top-level repository errors propagate so Hangfire's
/// failed-job tracking can record them.
/// </summary>
public sealed class UsageHeartbeatJobTests
{
    /// <summary>
    /// Returns a substitute <see cref="IAdvisoryLockProvider"/> whose <c>TryAcquireAsync</c>
    /// always returns an acquired (non-null) handle so happy-path tests do not skip the body.
    /// </summary>
    private static IAdvisoryLockProvider CreateAcquiredLockProvider()
    {
        IAdvisoryLockProvider provider = Substitute.For<IAdvisoryLockProvider>();
        provider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IAsyncDisposable>());

        return provider;
    }

    [Test]
    public async Task RunAsync_NoPaidSubscriptions_DoesNotCallBillingApi()
    {
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription>());

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        UsageHeartbeatJob job = new(subscriptionRepo, tenantRepo, subscriptionService, billingApi, CreateAcquiredLockProvider(), logger);

        await job.RunAsync(CancellationToken.None);

        await billingApi.DidNotReceive().ReportMachineUsageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_OnePaidTenant_CallsBillingApiOnceWithCorrectCount()
    {
        TenantSubscription subscription = BuildPaidSubscription(7);
        Tenant tenant = BuildTenant(7, "ext-tenant-7");

        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { subscription });

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(7, Arg.Any<CancellationToken>()).Returns(tenant);

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetMachineCountForTenantAsync(7, Arg.Any<CancellationToken>()).Returns(42);

        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        billingApi.ReportMachineUsageAsync("ext-tenant-7", 42, Arg.Any<CancellationToken>()).Returns(true);

        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        UsageHeartbeatJob job = new(subscriptionRepo, tenantRepo, subscriptionService, billingApi, CreateAcquiredLockProvider(), logger);

        await job.RunAsync(CancellationToken.None);

        await billingApi.Received(1).ReportMachineUsageAsync(
            "ext-tenant-7", 42, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_MultiplePaidTenants_CallsBillingApiPerTenant()
    {
        List<TenantSubscription> subs = new()
        {
            BuildPaidSubscription(1),
            BuildPaidSubscription(2),
            BuildPaidSubscription(3),
        };

        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>()).Returns(subs);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(BuildTenant(1, "ext-1"));
        tenantRepo.GetTenantByIdAsync(2, Arg.Any<CancellationToken>())
            .Returns(BuildTenant(2, "ext-2"));
        tenantRepo.GetTenantByIdAsync(3, Arg.Any<CancellationToken>())
            .Returns(BuildTenant(3, "ext-3"));

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetMachineCountForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(10);

        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        billingApi.ReportMachineUsageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);

        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        UsageHeartbeatJob job = new(subscriptionRepo, tenantRepo, subscriptionService, billingApi, CreateAcquiredLockProvider(), logger);

        await job.RunAsync(CancellationToken.None);

        await billingApi.Received(3).ReportMachineUsageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_TenantNotFound_SkipsThatTenantWithoutBillingCall()
    {
        // Intent: when a paid subscription's tenant has been deleted out from under us, the job
        // should skip that tenant and continue with the others. Preserves the predecessor's
        // tenant-missing handling.
        List<TenantSubscription> subs = new()
        {
            BuildPaidSubscription(100), // tenant does not exist
            BuildPaidSubscription(200), // tenant exists
        };

        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>()).Returns(subs);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(100, Arg.Any<CancellationToken>()).Returns((Tenant?)null);
        tenantRepo.GetTenantByIdAsync(200, Arg.Any<CancellationToken>())
            .Returns(BuildTenant(200, "ext-200"));

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetMachineCountForTenantAsync(200, Arg.Any<CancellationToken>()).Returns(5);

        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        billingApi.ReportMachineUsageAsync("ext-200", 5, Arg.Any<CancellationToken>()).Returns(true);

        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        UsageHeartbeatJob job = new(subscriptionRepo, tenantRepo, subscriptionService, billingApi, CreateAcquiredLockProvider(), logger);

        await job.RunAsync(CancellationToken.None);

        await billingApi.Received(1).ReportMachineUsageAsync(
            "ext-200", 5, Arg.Any<CancellationToken>());
        await billingApi.DidNotReceive().ReportMachineUsageAsync(
            Arg.Is<string>(s => s != "ext-200"), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_BillingApiReturnsFalse_DoesNotThrowAndContinues()
    {
        // Intent: when the billing API replies with success=false (rather than throwing), the job
        // logs a warning and moves on rather than aborting the cycle. Ported from the predecessor.
        List<TenantSubscription> subs = new()
        {
            BuildPaidSubscription(1),
            BuildPaidSubscription(2),
        };

        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>()).Returns(subs);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(BuildTenant(1, "ext-1"));
        tenantRepo.GetTenantByIdAsync(2, Arg.Any<CancellationToken>())
            .Returns(BuildTenant(2, "ext-2"));

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetMachineCountForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);

        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        billingApi.ReportMachineUsageAsync("ext-1", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        billingApi.ReportMachineUsageAsync("ext-2", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        UsageHeartbeatJob job = new(subscriptionRepo, tenantRepo, subscriptionService, billingApi, CreateAcquiredLockProvider(), logger);

        await Assert.That(async () => await job.RunAsync(CancellationToken.None)).ThrowsNothing();

        await billingApi.Received(1).ReportMachineUsageAsync(
            "ext-2", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_BillingApiThrowsForOneTenant_ContinuesWithOthers()
    {
        // Intent: per-tenant exceptions in the inner loop are swallowed and counted (preserved from
        // the predecessor). One bad tenant must not abort the entire cycle.
        List<TenantSubscription> subs = new()
        {
            BuildPaidSubscription(1),
            BuildPaidSubscription(2),
        };

        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>()).Returns(subs);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(BuildTenant(1, "ext-1"));
        tenantRepo.GetTenantByIdAsync(2, Arg.Any<CancellationToken>())
            .Returns(BuildTenant(2, "ext-2"));

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetMachineCountForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);

        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        billingApi.ReportMachineUsageAsync("ext-1", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new HttpRequestException("billing API down"));
        billingApi.ReportMachineUsageAsync("ext-2", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);

        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        UsageHeartbeatJob job = new(subscriptionRepo, tenantRepo, subscriptionService, billingApi, CreateAcquiredLockProvider(), logger);

        await Assert.That(async () => await job.RunAsync(CancellationToken.None)).ThrowsNothing();

        await billingApi.Received(1).ReportMachineUsageAsync(
            "ext-2", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_SubscriptionRepositoryThrows_ExceptionPropagates()
    {
        // Intent: top-level (outside the per-tenant try/catch) exceptions must propagate so
        // Hangfire records the failure. Distinguishes from the per-tenant swallow above.
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<List<TenantSubscription>>>(_ => throw new InvalidOperationException("DB down"));

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        UsageHeartbeatJob job = new(subscriptionRepo, tenantRepo, subscriptionService, billingApi, CreateAcquiredLockProvider(), logger);

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.RunAsync(CancellationToken.None));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).IsEqualTo("DB down");
    }

    // ========== Constructor null guards ==========

    [Test]
    public async Task Constructor_NullSubscriptionRepository_Throws()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            UsageHeartbeatJob _ = new(null!, tenantRepo, subscriptionService, billingApi, CreateAcquiredLockProvider(), logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("subscriptionRepository");
    }

    [Test]
    public async Task Constructor_NullTenantRepository_Throws()
    {
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            UsageHeartbeatJob _ = new(subscriptionRepo, null!, subscriptionService, billingApi, CreateAcquiredLockProvider(), logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("tenantRepository");
    }

    [Test]
    public async Task Constructor_NullSubscriptionService_Throws()
    {
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            UsageHeartbeatJob _ = new(subscriptionRepo, tenantRepo, null!, billingApi, CreateAcquiredLockProvider(), logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("subscriptionService");
    }

    [Test]
    public async Task Constructor_NullBillingApiClient_Throws()
    {
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            UsageHeartbeatJob _ = new(subscriptionRepo, tenantRepo, subscriptionService, null!, CreateAcquiredLockProvider(), logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("billingApiClient");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            UsageHeartbeatJob _ = new(subscriptionRepo, tenantRepo, subscriptionService, billingApi, CreateAcquiredLockProvider(), null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    private static Tenant BuildTenant(int id, string externalId) => new()
    {
        Id = id,
        ExternalId = externalId,
        Name = $"T{id}",
        CreatedAt = DateTimeOffset.UtcNow,
        CreatedByUserId = 1,
        IsActive = true,
        LogoUrl = "",
    };

    private static TenantSubscription BuildPaidSubscription(int tenantId) => new()
    {
        TenantId = tenantId,
        Tier = SubscriptionTier.Pro,
        Status = SubscriptionStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    // ========== Restored named scenarios from the predecessor BackgroundService ==========

    [Test]
    public async Task Heartbeat_SkipsFreeTierTenants()
    {
        // Intent: pin the contract that GetPaidSubscriptionsAsync is the sole gate for which
        // tenants are reported to the billing API. The job does not re-filter by tier — Free-tier
        // tenants are excluded by the repository returning an empty list (since Free is not a
        // "paid" subscription). If a future refactor breaks GetPaidSubscriptionsAsync to leak Free
        // rows, this test will not catch that — but a separate test on the repository covers that
        // contract. This test pins the job-side behaviour: a fleet of only Free tenants results
        // in zero billing API calls and zero machine-count lookups.
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription>());

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        UsageHeartbeatJob job = new(subscriptionRepo, tenantRepo, subscriptionService, billingApi, CreateAcquiredLockProvider(), logger);

        await job.RunAsync(CancellationToken.None);

        await subscriptionService.DidNotReceive().GetMachineCountForTenantAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        await billingApi.DidNotReceive().ReportMachineUsageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportUsage_GetMachineCountThrows_LogsWarningAndContinuesToNextTenant()
    {
        // Intent: failure isolation. When GetMachineCountForTenantAsync throws inside the
        // per-tenant loop (e.g., a transient DB timeout), the exception must be caught and the
        // loop must continue to the next tenant. Without this containment, one slow tenant query
        // would abort billing reporting for every tenant scheduled after it in the cycle.
        List<TenantSubscription> subs = new()
        {
            BuildPaidSubscription(1),
            BuildPaidSubscription(2),
        };

        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>()).Returns(subs);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(BuildTenant(1, "ext-1"));
        tenantRepo.GetTenantByIdAsync(2, Arg.Any<CancellationToken>()).Returns(BuildTenant(2, "ext-2"));

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("database timeout"));
        subscriptionService.GetMachineCountForTenantAsync(2, Arg.Any<CancellationToken>()).Returns(8);

        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        billingApi.ReportMachineUsageAsync("ext-2", 8, Arg.Any<CancellationToken>()).Returns(true);

        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        UsageHeartbeatJob job = new(subscriptionRepo, tenantRepo, subscriptionService, billingApi, CreateAcquiredLockProvider(), logger);

        await Assert.That(async () => await job.RunAsync(CancellationToken.None)).ThrowsNothing();

        // Tenant 1's failure must not have produced a billing call.
        await billingApi.DidNotReceive().ReportMachineUsageAsync(
            "ext-1", Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Tenant 2 must still have been reported with the correct count.
        await billingApi.Received(1).ReportMachineUsageAsync(
            "ext-2", 8, Arg.Any<CancellationToken>());
        // The error must have been logged at warning level (per-tenant swallow).
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task Heartbeat_MultipleTenants_EachUsesCorrectExternalIdAndCount()
    {
        // Intent: pin per-tenant routing of the billing report. The earlier multi-tenant test
        // only asserts a total call count of 3 with Arg.Any matchers, which would still pass if
        // the job sent every tenant's count to the same external id (a fatal mis-routing bug
        // that would attribute all usage to one customer). This test gives each tenant a unique
        // machine count and asserts each (externalId, count) pair appeared exactly once.
        List<TenantSubscription> subs = new()
        {
            BuildPaidSubscription(1),
            BuildPaidSubscription(2),
            BuildPaidSubscription(3),
        };

        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>()).Returns(subs);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(BuildTenant(1, "ext-1"));
        tenantRepo.GetTenantByIdAsync(2, Arg.Any<CancellationToken>()).Returns(BuildTenant(2, "ext-2"));
        tenantRepo.GetTenantByIdAsync(3, Arg.Any<CancellationToken>()).Returns(BuildTenant(3, "ext-3"));

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(10);
        subscriptionService.GetMachineCountForTenantAsync(2, Arg.Any<CancellationToken>()).Returns(20);
        subscriptionService.GetMachineCountForTenantAsync(3, Arg.Any<CancellationToken>()).Returns(30);

        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        billingApi.ReportMachineUsageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);

        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        UsageHeartbeatJob job = new(subscriptionRepo, tenantRepo, subscriptionService, billingApi, CreateAcquiredLockProvider(), logger);

        await job.RunAsync(CancellationToken.None);

        await billingApi.Received(1).ReportMachineUsageAsync("ext-1", 10, Arg.Any<CancellationToken>());
        await billingApi.Received(1).ReportMachineUsageAsync("ext-2", 20, Arg.Any<CancellationToken>());
        await billingApi.Received(1).ReportMachineUsageAsync("ext-3", 30, Arg.Any<CancellationToken>());
        // Defense against mis-routing: no call should have used a mismatched (externalId, count).
        await billingApi.DidNotReceive().ReportMachineUsageAsync(
            "ext-1", Arg.Is<int>(c => c != 10), Arg.Any<CancellationToken>());
        await billingApi.DidNotReceive().ReportMachineUsageAsync(
            "ext-2", Arg.Is<int>(c => c != 20), Arg.Any<CancellationToken>());
        await billingApi.DidNotReceive().ReportMachineUsageAsync(
            "ext-3", Arg.Is<int>(c => c != 30), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_NoLongerCarriesDisableConcurrentExecutionAttribute()
    {
        // M11 regression: serialization moved from Hangfire's [DisableConcurrentExecution]
        // (which blocks waiting for the lock) to IAdvisoryLockProvider (try-once). The attribute
        // must NOT be present — if a future PR re-adds it, semantics drift back to a queueing
        // model that does NOT match the metered-billing-tolerates-skip design.
        MethodInfo method = typeof(UsageHeartbeatJob).GetMethod(nameof(UsageHeartbeatJob.RunAsync))!;
        CustomAttributeData? attrData = method.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(DisableConcurrentExecutionAttribute));

        await Assert.That(attrData).IsNull();
    }

    [Test]
    public async Task RunAsync_LockNotAcquired_LogsAndReturnsWithoutWork()
    {
        IAdvisoryLockProvider lockProvider = Substitute.For<IAdvisoryLockProvider>();
        lockProvider.TryAcquireAsync(UsageHeartbeatJob.LockName, Arg.Any<CancellationToken>())
            .Returns((IAsyncDisposable?)null);

        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();
        UsageHeartbeatJob job = new(subscriptionRepo, tenantRepo, subscriptionService, billingApi, lockProvider, logger);

        await job.RunAsync(CancellationToken.None);

        // The body never ran: GetPaidSubscriptionsAsync should not have been touched.
        await subscriptionRepo.DidNotReceive().GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>());
        await billingApi.DidNotReceive().ReportMachineUsageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_LockAcquired_BodyExecutes()
    {
        IAdvisoryLockProvider lockProvider = Substitute.For<IAdvisoryLockProvider>();
        IAsyncDisposable handle = Substitute.For<IAsyncDisposable>();
        lockProvider.TryAcquireAsync(UsageHeartbeatJob.LockName, Arg.Any<CancellationToken>())
            .Returns(handle);

        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription>());
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();
        UsageHeartbeatJob job = new(subscriptionRepo, tenantRepo, subscriptionService, billingApi, lockProvider, logger);

        await job.RunAsync(CancellationToken.None);

        await subscriptionRepo.Received(1).GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>());
        await handle.Received(1).DisposeAsync();
    }

    [Test]
    public async Task Constructor_NullLockProvider_Throws()
    {
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        IBillingApiClient billingApi = Substitute.For<IBillingApiClient>();
        ILogger<UsageHeartbeatJob> logger = Substitute.For<ILogger<UsageHeartbeatJob>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            UsageHeartbeatJob _ = new(subscriptionRepo, tenantRepo, subscriptionService, billingApi, null!, logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("lockProvider");
    }
}
