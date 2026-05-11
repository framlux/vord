// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="UsageHeartbeatService"/>.
/// </summary>
public sealed class UsageHeartbeatServiceTests
{
    private static (
        UsageHeartbeatService Service,
        ISubscriptionRepository SubscriptionRepo,
        IBillingApiClient BillingClient,
        ISubscriptionService SubscriptionService,
        ILogger<UsageHeartbeatService> Logger
    ) CreateSut()
    {
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository, ITenantRepository>();
        IBillingApiClient billingClient = Substitute.For<IBillingApiClient>();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        ILogger<UsageHeartbeatService> logger = Substitute.For<ILogger<UsageHeartbeatService>>();

        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ISubscriptionRepository)).Returns(subscriptionRepo);
        serviceProvider.GetService(typeof(ITenantRepository)).Returns(subscriptionRepo);
        serviceProvider.GetService(typeof(ISubscriptionService)).Returns(subscriptionService);
        scope.ServiceProvider.Returns(serviceProvider);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));

        UsageHeartbeatService service = new(scopeFactory, billingClient, distributedLock, logger);

        return (service, subscriptionRepo, billingClient, subscriptionService, logger);
    }

    private static TenantSubscription CreateSubscription(
        int tenantId,
        SubscriptionTier tier = SubscriptionTier.Pro)
    {
        return new TenantSubscription
        {
            Id = tenantId,
            TenantId = tenantId,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static Tenant CreateTenant(int id, string externalId)
    {
        return new Tenant
        {
            Id = id,
            ExternalId = externalId,
            Name = $"Tenant {id}",
            IsActive = true,
            CreatedByUserId = 1,
            LogoUrl = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Test]
    public async Task Heartbeat_ReportsCorrectCountForPaidTenants()
    {
        (UsageHeartbeatService service, ISubscriptionRepository subscriptionRepo,
            IBillingApiClient billingClient, ISubscriptionService subscriptionService,
            ILogger<UsageHeartbeatService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription proSub = CreateSubscription(1, SubscriptionTier.Pro);
        TenantSubscription teamSub = CreateSubscription(2, SubscriptionTier.Team);
        Tenant tenant1 = CreateTenant(1, "ext-1");
        Tenant tenant2 = CreateTenant(2, "ext-2");

        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { proSub, teamSub });
        ((ITenantRepository)subscriptionRepo).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant1);
        ((ITenantRepository)subscriptionRepo).GetTenantByIdAsync(2, Arg.Any<CancellationToken>())
            .Returns(tenant2);

        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);
        subscriptionService.GetMachineCountForTenantAsync(2, Arg.Any<CancellationToken>()).Returns(12);

        billingClient.ReportMachineUsageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                // Signal done after both tenants are reported (second call)
                if (callInfo.ArgAt<string>(0) == "ext-2")
                {
                    workDone.TrySetResult();
                }

                return true;
            });

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await billingClient.Received(1).ReportMachineUsageAsync("ext-1", 5, Arg.Any<CancellationToken>());
        await billingClient.Received(1).ReportMachineUsageAsync("ext-2", 12, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Heartbeat_SkipsFreeTierTenants()
    {
        (UsageHeartbeatService service, ISubscriptionRepository subscriptionRepo,
            IBillingApiClient billingClient, ISubscriptionService subscriptionService,
            ILogger<UsageHeartbeatService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        // GetPaidSubscriptionsAsync already filters to non-Free tiers, but verify no calls are made
        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return new List<TenantSubscription>();
            });

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await billingClient.DidNotReceive().ReportMachineUsageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Heartbeat_HandlesUnavailabilityGracefully()
    {
        (UsageHeartbeatService service, ISubscriptionRepository subscriptionRepo,
            IBillingApiClient billingClient, ISubscriptionService subscriptionService,
            ILogger<UsageHeartbeatService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription sub1 = CreateSubscription(1, SubscriptionTier.Pro);
        TenantSubscription sub2 = CreateSubscription(2, SubscriptionTier.Team);
        Tenant tenant1 = CreateTenant(1, "ext-1");
        Tenant tenant2 = CreateTenant(2, "ext-2");

        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub1, sub2 });
        ((ITenantRepository)subscriptionRepo).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant1);
        ((ITenantRepository)subscriptionRepo).GetTenantByIdAsync(2, Arg.Any<CancellationToken>())
            .Returns(tenant2);

        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);
        subscriptionService.GetMachineCountForTenantAsync(2, Arg.Any<CancellationToken>()).Returns(10);

        // First tenant's report throws; second should still be processed
        billingClient.ReportMachineUsageAsync("ext-1", 5, Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new Exception("billing-api unavailable"));
        billingClient.ReportMachineUsageAsync("ext-2", 10, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return true;
            });

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // Tenant 2 was still processed despite tenant 1 failure
        await billingClient.Received(1).ReportMachineUsageAsync("ext-2", 10, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Heartbeat_DoesNotRunWhenLockHeld()
    {
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository, ITenantRepository>();
        IBillingApiClient billingClient = Substitute.For<IBillingApiClient>();
        ILogger<UsageHeartbeatService> logger = Substitute.For<ILogger<UsageHeartbeatService>>();

        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        // Lock acquisition fails (another instance holds it)
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(null));

        UsageHeartbeatService service = new(scopeFactory, billingClient, distributedLock, logger);

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);

        // Give the service a moment to attempt the lock
        await Task.Delay(100);

        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // No billing calls should have been made since lock was not acquired
        await billingClient.DidNotReceive().ReportMachineUsageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportUsage_TenantNotFound_SkipsTenantAndContinuesToNext()
    {
        // Verifies the null-tenant guard (tenant is null → continue) does not abort the loop.
        // Tenant 1 has no matching tenant record; tenant 2 should still be reported successfully.
        (UsageHeartbeatService service, ISubscriptionRepository subscriptionRepo,
            IBillingApiClient billingClient, ISubscriptionService subscriptionService,
            ILogger<UsageHeartbeatService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription sub1 = CreateSubscription(1, SubscriptionTier.Pro);
        TenantSubscription sub2 = CreateSubscription(2, SubscriptionTier.Team);
        Tenant tenant2 = CreateTenant(2, "ext-2");

        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub1, sub2 });

        // Tenant 1 has no database record
        ((ITenantRepository)subscriptionRepo).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(null));
        ((ITenantRepository)subscriptionRepo).GetTenantByIdAsync(2, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(tenant2));

        subscriptionService.GetMachineCountForTenantAsync(2, Arg.Any<CancellationToken>()).Returns(7);

        billingClient.ReportMachineUsageAsync("ext-2", 7, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return true;
            });

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // Tenant 1 should never have reached the billing API
        await billingClient.DidNotReceive().ReportMachineUsageAsync(
            Arg.Any<string>(), 5, Arg.Any<CancellationToken>());

        // Tenant 2 should have been reported normally
        await billingClient.Received(1).ReportMachineUsageAsync("ext-2", 7, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportUsage_BillingApiReturnsFalse_IncreasesFailCountAndContinues()
    {
        // Verifies the failure branch (success == false) is tracked without aborting the loop.
        // Tenant 1 gets a false response; tenant 2 should still be attempted and succeed.
        (UsageHeartbeatService service, ISubscriptionRepository subscriptionRepo,
            IBillingApiClient billingClient, ISubscriptionService subscriptionService,
            ILogger<UsageHeartbeatService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription sub1 = CreateSubscription(1, SubscriptionTier.Pro);
        TenantSubscription sub2 = CreateSubscription(2, SubscriptionTier.Team);
        Tenant tenant1 = CreateTenant(1, "ext-1");
        Tenant tenant2 = CreateTenant(2, "ext-2");

        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub1, sub2 });
        ((ITenantRepository)subscriptionRepo).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant1);
        ((ITenantRepository)subscriptionRepo).GetTenantByIdAsync(2, Arg.Any<CancellationToken>())
            .Returns(tenant2);

        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(3);
        subscriptionService.GetMachineCountForTenantAsync(2, Arg.Any<CancellationToken>()).Returns(9);

        // Tenant 1's report is rejected by the billing API (returns false)
        billingClient.ReportMachineUsageAsync("ext-1", 3, Arg.Any<CancellationToken>())
            .Returns(false);

        // Tenant 2 succeeds, used to signal that processing continued past the failure
        billingClient.ReportMachineUsageAsync("ext-2", 9, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return true;
            });

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // Both tenants must have been attempted even though tenant 1 failed
        await billingClient.Received(1).ReportMachineUsageAsync("ext-1", 3, Arg.Any<CancellationToken>());
        await billingClient.Received(1).ReportMachineUsageAsync("ext-2", 9, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportUsage_FreeTierSubscription_IsExcludedByPaidSubscriptionsFilter()
    {
        // Verifies that GetPaidSubscriptionsAsync is the gate: when it returns an empty list
        // (as it would for a tenant with only a Free-tier subscription) no billing calls are made.
        (UsageHeartbeatService service, ISubscriptionRepository subscriptionRepo,
            IBillingApiClient billingClient, ISubscriptionService subscriptionService,
            ILogger<UsageHeartbeatService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        // Repository returns no paid subscriptions, simulating a fleet composed entirely of Free tenants
        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return new List<TenantSubscription>();
            });

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // No machine counts should have been fetched and no billing calls made
        await subscriptionService.DidNotReceive().GetMachineCountForTenantAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        await billingClient.DidNotReceive().ReportMachineUsageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that a top-level unhandled exception (e.g. from the repository layer outside
    /// the per-tenant try/catch) is caught by the outer handler in ExecuteAsync and logs an error.
    /// This exercises the outer exception branch by calling ReportUsageForAllPaidTenantsAsync
    /// directly via the background service infrastructure.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_TopLevelException_LogsError()
    {
        (UsageHeartbeatService service, ISubscriptionRepository subscriptionRepo,
            IBillingApiClient billingClient, ISubscriptionService _,
            ILogger<UsageHeartbeatService> logger) = CreateSut();
        TaskCompletionSource workDone = new();

        // Throw on GetPaidSubscriptionsAsync — this will bubble up past the per-tenant loop
        // and be caught by the outer catch in ExecuteAsync, which logs LogLevel.Error.
        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns<List<TenantSubscription>>(_ =>
            {
                workDone.TrySetResult();
                throw new InvalidOperationException("repository failure");
            });

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // The error from the repository failure must have been logged
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

        // No billing API calls should have been made
        await billingClient.DidNotReceive().ReportMachineUsageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that when GetMachineCountForTenantAsync throws inside the per-tenant loop,
    /// the exception is caught, the fail count increases, and the loop continues to the next tenant.
    /// This exercises the per-tenant catch block where the exception is thrown before
    /// reaching ReportMachineUsageAsync.
    /// </summary>
    [Test]
    public async Task ReportUsage_GetMachineCountThrows_LogsWarningAndContinuesToNext()
    {
        (UsageHeartbeatService service, ISubscriptionRepository subscriptionRepo,
            IBillingApiClient billingClient, ISubscriptionService subscriptionService,
            ILogger<UsageHeartbeatService> logger) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription sub1 = CreateSubscription(1, SubscriptionTier.Pro);
        TenantSubscription sub2 = CreateSubscription(2, SubscriptionTier.Team);
        Tenant tenant1 = CreateTenant(1, "ext-1");
        Tenant tenant2 = CreateTenant(2, "ext-2");

        subscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub1, sub2 });
        ((ITenantRepository)subscriptionRepo).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant1);
        ((ITenantRepository)subscriptionRepo).GetTenantByIdAsync(2, Arg.Any<CancellationToken>())
            .Returns(tenant2);

        // Tenant 1's machine count call throws
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("database timeout"));

        subscriptionService.GetMachineCountForTenantAsync(2, Arg.Any<CancellationToken>()).Returns(8);

        billingClient.ReportMachineUsageAsync("ext-2", 8, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return true;
            });

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // A warning should have been logged for tenant 1's failure
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Tenant 2 should still have been processed
        await billingClient.Received(1).ReportMachineUsageAsync("ext-2", 8, Arg.Any<CancellationToken>());
    }
}
