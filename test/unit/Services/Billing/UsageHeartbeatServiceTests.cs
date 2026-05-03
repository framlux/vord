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
            RetentionDays = 30,
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
}
