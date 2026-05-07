// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.Vord.BillingGrpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="StripeSyncService"/>.
/// </summary>
public sealed class StripeSyncServiceTests
{
    private static (
        StripeSyncService Service,
        ISubscriptionRepository DbCache,
        IBillingApiClient BillingClient,
        IBillingWebhookHandler WebhookHandler,
        ISubscriptionService SubscriptionService,
        ILogger<StripeSyncService> Logger
    ) CreateSut(
        string proPriceId = "price_pro_123",
        string teamPriceId = "price_team_456")
    {
        ISubscriptionRepository dbCache = Substitute.For<ISubscriptionRepository, ITenantRepository>();
        IBillingApiClient billingClient = Substitute.For<IBillingApiClient>();
        IBillingWebhookHandler webhookHandler = Substitute.For<IBillingWebhookHandler>();
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        ILogger<StripeSyncService> logger = Substitute.For<ILogger<StripeSyncService>>();

        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ISubscriptionRepository)).Returns(dbCache);
        serviceProvider.GetService(typeof(ITenantRepository)).Returns(dbCache);
        serviceProvider.GetService(typeof(IBillingWebhookHandler)).Returns(webhookHandler);
        serviceProvider.GetService(typeof(ISubscriptionService)).Returns(subscriptionService);
        scope.ServiceProvider.Returns(serviceProvider);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        IOptions<BillingOptions> billingOptions = Options.Create(new BillingOptions
        {
            StripeProPriceId = proPriceId,
            StripeTeamPriceId = teamPriceId
        });

        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<LockHandle?>(new LockHandle(Substitute.For<IDatabase>(), "test-key", "test-value")));

        StripeSyncService service = new(scopeFactory, billingClient, billingOptions, distributedLock, logger);

        return (service, dbCache, billingClient, webhookHandler, subscriptionService, logger);
    }

    private static TenantSubscription CreateSubscription(
        int tenantId = 1,
        SubscriptionTier tier = SubscriptionTier.Pro,
        SubscriptionStatus status = SubscriptionStatus.Active,
        DateTimeOffset? currentPeriodEnd = null)
    {
        return new TenantSubscription
        {
            Id = tenantId,
            TenantId = tenantId,
            Tier = tier,
            Status = status,
            CurrentPeriodEnd = currentPeriodEnd,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static Tenant CreateTenant(int id = 1, string externalId = "ext-1")
    {
        return new Tenant
        {
            Id = id,
            ExternalId = externalId,
            Name = $"Tenant {id}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = string.Empty
        };
    }

    // --- Machine quantity sync ---

    [Test]
    public async Task SyncPaidSubscriptions_MachineQuantityDiffers_UpdatesStripeQuantity()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(15));
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(5);

        DateTimeOffset periodEnd = sub.CurrentPeriodEnd!.Value;
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "active", "price_pro_123", 3, periodEnd, BillingTier.Pro));
        billingClient.ReportMachineUsageAsync("ext-1", 5, Arg.Any<CancellationToken>())
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

        await billingClient.Received(1).ReportMachineUsageAsync("ext-1", 5, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_MachineQuantityMatches_NoUpdateCall()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(15));
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return 5;
            });

        DateTimeOffset periodEnd = sub.CurrentPeriodEnd!.Value;
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "active", "price_pro_123", 5, periodEnd, BillingTier.Pro));

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await billingClient.DidNotReceive().ReportMachineUsageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // --- Tier drift detection ---

    [Test]
    public async Task SyncPaidSubscriptions_TierDriftDetected_CorrectsTierToMatchStripe()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler webhookHandler, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        // Local says Pro, but Stripe says Team price
        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(15));
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(5);

        DateTimeOffset periodEnd = sub.CurrentPeriodEnd!.Value;
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "active", "price_team_456", 5, periodEnd, BillingTier.Team));

        webhookHandler.HandleTierCorrectionAsync(1, SubscriptionTier.Team, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return Task.CompletedTask;
            });

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await webhookHandler.Received(1).HandleTierCorrectionAsync(1, SubscriptionTier.Team, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_TierMatches_NoTierCorrection()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler webhookHandler, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(15));
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return 5;
            });

        DateTimeOffset periodEnd = sub.CurrentPeriodEnd!.Value;
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "active", "price_pro_123", 5, periodEnd, BillingTier.Pro));

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await webhookHandler.DidNotReceive().HandleTierCorrectionAsync(
            Arg.Any<int>(), Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_UnknownPriceId_NoTierCorrection()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler webhookHandler, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(15));
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return 5;
            });

        DateTimeOffset periodEnd = sub.CurrentPeriodEnd!.Value;
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "active", "price_unknown_789", 5, periodEnd, BillingTier.Unspecified));

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await webhookHandler.DidNotReceive().HandleTierCorrectionAsync(
            Arg.Any<int>(), Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>());
    }

    // --- Status drift detection ---

    [Test]
    public async Task SyncPaidSubscriptions_StatusDrift_ActiveToPastDue_CorrectStatus()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        // Local says Active, Stripe says past_due
        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            status: SubscriptionStatus.Active,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(15));
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(5);
        dbCache.SetSubscriptionPastDueAsync(1, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return Task.CompletedTask;
            });

        DateTimeOffset periodEnd = sub.CurrentPeriodEnd!.Value;
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "past_due", "price_pro_123", 5, periodEnd, BillingTier.Pro));

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await dbCache.Received(1).SetSubscriptionPastDueAsync(1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_StatusDrift_PastDueToActive_CorrectStatus()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        // Local says PastDue, Stripe says active
        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            status: SubscriptionStatus.PastDue,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(15));
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(5);
        dbCache.SetSubscriptionActiveAsync(1, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return Task.CompletedTask;
            });

        DateTimeOffset periodEnd = sub.CurrentPeriodEnd!.Value;
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "active", "price_pro_123", 5, periodEnd, BillingTier.Pro));

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await dbCache.Received(1).SetSubscriptionActiveAsync(1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_StatusDrift_ActiveToCanceled_DeactivatesSubscription()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            status: SubscriptionStatus.Active,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(15));
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(5);
        dbCache.DeactivateSubscriptionAsync(1, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return Task.CompletedTask;
            });

        DateTimeOffset periodEnd = sub.CurrentPeriodEnd!.Value;
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "canceled", "price_pro_123", 5, periodEnd, BillingTier.Pro));

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await dbCache.Received(1).DeactivateSubscriptionAsync(1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_StatusMatches_NoStatusCorrection()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            status: SubscriptionStatus.Active,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(15));
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return 5;
            });

        DateTimeOffset periodEnd = sub.CurrentPeriodEnd!.Value;
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "active", "price_pro_123", 5, periodEnd, BillingTier.Pro));

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await dbCache.DidNotReceive().SetSubscriptionActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await dbCache.DidNotReceive().SetSubscriptionPastDueAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await dbCache.DidNotReceive().DeactivateSubscriptionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // --- Period end sync ---

    [Test]
    public async Task SyncPaidSubscriptions_PeriodEndStale_UpdatesPeriodEnd()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        DateTimeOffset localPeriodEnd = DateTimeOffset.UtcNow.AddDays(10);
        DateTimeOffset stripePeriodEnd = DateTimeOffset.UtcNow.AddDays(30);

        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            currentPeriodEnd: localPeriodEnd);
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(5);
        dbCache.UpdateSubscriptionPeriodEndAsync(1, stripePeriodEnd, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return Task.CompletedTask;
            });

        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "active", "price_pro_123", 5, stripePeriodEnd, BillingTier.Pro));

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await dbCache.Received(1).UpdateSubscriptionPeriodEndAsync(1, stripePeriodEnd, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_PeriodEndNull_UpdatesPeriodEnd()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        DateTimeOffset stripePeriodEnd = DateTimeOffset.UtcNow.AddDays(30);

        // Local has no period end set
        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            currentPeriodEnd: null);
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(5);
        dbCache.UpdateSubscriptionPeriodEndAsync(1, stripePeriodEnd, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return Task.CompletedTask;
            });

        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "active", "price_pro_123", 5, stripePeriodEnd, BillingTier.Pro));

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await dbCache.Received(1).UpdateSubscriptionPeriodEndAsync(1, stripePeriodEnd, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_PeriodEndCurrent_NoUpdate()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        DateTimeOffset periodEnd = DateTimeOffset.UtcNow.AddDays(15);

        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            currentPeriodEnd: periodEnd);
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return 5;
            });

        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "active", "price_pro_123", 5, periodEnd, BillingTier.Pro));

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await dbCache.DidNotReceive().UpdateSubscriptionPeriodEndAsync(
            Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_LocalActive_StripePastDue_UpdatesToPastDue()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        // Local subscription is Active but Stripe reports past_due — local must be corrected
        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            status: SubscriptionStatus.Active,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(5));
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(3);
        dbCache.SetSubscriptionPastDueAsync(1, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return Task.CompletedTask;
            });

        DateTimeOffset periodEnd = sub.CurrentPeriodEnd!.Value;
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "past_due", "price_pro_123", 3, periodEnd, BillingTier.Pro));

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await dbCache.Received(1).SetSubscriptionPastDueAsync(1, Arg.Any<CancellationToken>());
        await dbCache.DidNotReceive().SetSubscriptionActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await dbCache.DidNotReceive().DeactivateSubscriptionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_LocalActive_StripeCanceled_ProcessesCancellation()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        // Local subscription is Active but Stripe reports canceled — local must be deactivated
        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Team,
            status: SubscriptionStatus.Active,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(3));
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(2);
        dbCache.DeactivateSubscriptionAsync(1, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return Task.CompletedTask;
            });

        DateTimeOffset periodEnd = sub.CurrentPeriodEnd!.Value;
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "canceled", "price_team_456", 2, periodEnd, BillingTier.Team));

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await dbCache.Received(1).DeactivateSubscriptionAsync(1, Arg.Any<CancellationToken>());
        await dbCache.DidNotReceive().SetSubscriptionActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await dbCache.DidNotReceive().SetSubscriptionPastDueAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_LocalPastDue_StripeActive_UpdatesToActive()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        // Local subscription is PastDue but Stripe reports active — payment was resolved
        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            status: SubscriptionStatus.PastDue,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(20));
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(4);
        dbCache.SetSubscriptionActiveAsync(1, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return Task.CompletedTask;
            });

        DateTimeOffset periodEnd = sub.CurrentPeriodEnd!.Value;
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "active", "price_pro_123", 4, periodEnd, BillingTier.Pro));

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        await dbCache.Received(1).SetSubscriptionActiveAsync(1, Arg.Any<CancellationToken>());
        await dbCache.DidNotReceive().SetSubscriptionPastDueAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await dbCache.DidNotReceive().DeactivateSubscriptionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // --- Error handling & resilience ---

    [Test]
    public async Task SyncPaidSubscriptions_ErrorForOneTenant_ContinuesWithNext()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> logger) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription sub1 = CreateSubscription(1, tier: SubscriptionTier.Pro,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(15));
        TenantSubscription sub2 = CreateSubscription(2, tier: SubscriptionTier.Team,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(20));
        Tenant tenant1 = CreateTenant(1, "ext-1");
        Tenant tenant2 = CreateTenant(2, "ext-2");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub1, sub2 });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant1);
        ((ITenantRepository)dbCache).GetTenantByIdAsync(2, Arg.Any<CancellationToken>())
            .Returns(tenant2);

        // First tenant throws
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("service error"));

        // Second tenant succeeds
        DateTimeOffset periodEnd = sub2.CurrentPeriodEnd!.Value;
        billingClient.GetSubscriptionStatusAsync("ext-2", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "active", "price_team_456", 5, periodEnd, BillingTier.Team));
        subscriptionService.GetMachineCountForTenantAsync(2, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return 5;
            });

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // Verify the error was logged for tenant 1
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Verify tenant 2 was still processed
        await billingClient.Received(1).GetSubscriptionStatusAsync("ext-2", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_TenantNotFound_LogsWarningAndSkips()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService _,
            ILogger<StripeSyncService> logger) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro);

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return (Tenant?)null;
            });

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
        await billingClient.DidNotReceive().GetSubscriptionStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_StripeStatusNone_SkipsAllSyncOperations()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro);
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return new StripeSubscriptionStatus(false, "none", string.Empty, 0, null, BillingTier.Unspecified);
            });

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // No sync operations should be called
        await subscriptionService.DidNotReceive().GetMachineCountForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await billingClient.DidNotReceive().ReportMachineUsageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_TopLevelError_LogsAndContinues()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient _,
            IBillingWebhookHandler _, ISubscriptionService _,
            ILogger<StripeSyncService> logger) = CreateSut();
        TaskCompletionSource workDone = new();

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<List<TenantSubscription>>(new InvalidOperationException("DB error")));
        dbCache.When(x => x.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>()))
            .Do(_ => workDone.TrySetResult());

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_NoPaidSubscriptions_NoSyncOperations()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService _,
            ILogger<StripeSyncService> _) = CreateSut();
        TaskCompletionSource workDone = new();

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
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

        await billingClient.DidNotReceive().GetSubscriptionStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_QuantityUpdateFails_LogsWarning()
    {
        (StripeSyncService service, ISubscriptionRepository dbCache, IBillingApiClient billingClient,
            IBillingWebhookHandler _, ISubscriptionService subscriptionService,
            ILogger<StripeSyncService> logger) = CreateSut();
        TaskCompletionSource workDone = new();

        TenantSubscription sub = CreateSubscription(1, tier: SubscriptionTier.Pro,
            currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(15));
        Tenant tenant = CreateTenant(1, "ext-1");

        dbCache.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { sub });
        ((ITenantRepository)dbCache).GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(10);

        DateTimeOffset periodEnd = sub.CurrentPeriodEnd!.Value;
        billingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(new StripeSubscriptionStatus(false, "active", "price_pro_123", 5, periodEnd, BillingTier.Pro));
        billingClient.ReportMachineUsageAsync("ext-1", 10, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return false;
            });

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
