// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.Vord.BillingGrpc;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="StripeSyncJob"/>. Covers the four reconciliation operations
/// (machine quantity, tier, status, period end), tenant-not-found and stripe-status-none skips,
/// per-tenant error containment, top-level error propagation, and constructor null guards.
/// </summary>
public sealed class StripeSyncJobTests
{
    private const string ProPriceId = "price_pro_123";
    private const string TeamPriceId = "price_team_456";

    [Test]
    public async Task RunAsync_NoPaidSubscriptions_NoSyncOperations()
    {
        TestSut sut = new();
        sut.SubscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription>());

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.BillingClient.DidNotReceive().GetSubscriptionStatusAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_TenantNotFound_LogsAndSkipsWithoutBillingCall()
    {
        TestSut sut = new();
        sut.SubscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { BuildSub(99) });
        sut.TenantRepo.GetTenantByIdAsync(99, Arg.Any<CancellationToken>()).Returns((Tenant?)null);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.BillingClient.DidNotReceive().GetSubscriptionStatusAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_StripeStatusNone_SkipsAllInnerSyncOperations()
    {
        TestSut sut = new();
        sut.SubscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { BuildSub(1) });
        sut.TenantRepo.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(BuildTenant(1, "ext-1"));
        sut.BillingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(DefaultStripeStatus with { StripeStatus = "none" });

        await sut.Job.RunAsync(CancellationToken.None);

        // None of the four sync sub-operations should fire.
        await sut.BillingClient.DidNotReceive().ReportMachineUsageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await sut.WebhookHandler.DidNotReceive().HandleTierCorrectionAsync(
            Arg.Any<int>(), Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_MachineQuantityDiffers_ReportsUsage()
    {
        TestSut sut = new();
        sut.SeedOneSubscription(localTier: SubscriptionTier.Pro, stripeStatus: DefaultStripeStatus with
        {
            StripeStatus = "active",
            Tier = BillingTier.Pro,
            Quantity = 5,
        });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(8);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.BillingClient.Received(1).ReportMachineUsageAsync("ext-1", 8, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_MachineQuantityMatches_NoReport()
    {
        TestSut sut = new();
        sut.SeedOneSubscription(localTier: SubscriptionTier.Pro, stripeStatus: DefaultStripeStatus with
        {
            StripeStatus = "active",
            Tier = BillingTier.Pro,
            Quantity = 5,
        });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.BillingClient.DidNotReceive().ReportMachineUsageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_TierDrift_CorrectsViaWebhookHandler()
    {
        TestSut sut = new();
        sut.SeedOneSubscription(localTier: SubscriptionTier.Pro, stripeStatus: DefaultStripeStatus with
        {
            StripeStatus = "active",
            Tier = BillingTier.Team, // drift: local is Pro, Stripe says Team
            Quantity = 5,
        });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.WebhookHandler.Received(1).HandleTierCorrectionAsync(
            1, SubscriptionTier.Team, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_TierMatches_NoCorrection()
    {
        TestSut sut = new();
        sut.SeedOneSubscription(localTier: SubscriptionTier.Pro, stripeStatus: DefaultStripeStatus with
        {
            StripeStatus = "active",
            Tier = BillingTier.Pro,
            Quantity = 5,
        });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.WebhookHandler.DidNotReceive().HandleTierCorrectionAsync(
            Arg.Any<int>(), Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_StatusActiveToPastDue_UpdatesStatus()
    {
        TestSut sut = new();
        sut.SeedOneSubscription(localTier: SubscriptionTier.Pro, localStatus: SubscriptionStatus.Active,
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "past_due",
                Tier = BillingTier.Pro,
                Quantity = 5,
            });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.SubscriptionRepo.Received(1).SetSubscriptionPastDueAsync(1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_StatusActiveToCanceled_DeactivatesSubscription()
    {
        TestSut sut = new();
        sut.SeedOneSubscription(localTier: SubscriptionTier.Pro, localStatus: SubscriptionStatus.Active,
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "canceled",
                Tier = BillingTier.Pro,
                Quantity = 5,
            });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.SubscriptionRepo.Received(1).DeactivateSubscriptionAsync(1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_PeriodEndStale_UpdatesPeriodEnd()
    {
        DateTimeOffset stripePeriodEnd = DateTimeOffset.UtcNow.AddDays(30);
        TestSut sut = new();
        sut.SeedOneSubscription(localTier: SubscriptionTier.Pro,
            localCurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(15),
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "active",
                Tier = BillingTier.Pro,
                Quantity = 5,
                CurrentPeriodEnd = stripePeriodEnd,
            });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.SubscriptionRepo.Received(1).UpdateSubscriptionPeriodEndAsync(
            1, stripePeriodEnd, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_PeriodEndCurrent_NoUpdate()
    {
        DateTimeOffset shared = DateTimeOffset.UtcNow.AddDays(30);
        TestSut sut = new();
        sut.SeedOneSubscription(localTier: SubscriptionTier.Pro,
            localCurrentPeriodEnd: shared,
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "active",
                Tier = BillingTier.Pro,
                Quantity = 5,
                CurrentPeriodEnd = shared,
            });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.SubscriptionRepo.DidNotReceive().UpdateSubscriptionPeriodEndAsync(
            Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncTierAsync_StripeReturnsFreeTier_DoesNotDowngradePaidSubscription()
    {
        // Intent: a billing-service bug or stale cache returning Free for a paying customer
        // must not call HandleTierCorrectionAsync. The old service excluded Free from the
        // correction path because Pro->Free transitions are owned by the webhook pipeline,
        // which is the authoritative source for downgrades. Without this guard, the sync
        // job would silently drop a paying tenant to Free on a transient Stripe response.
        TestSut sut = new();
        sut.SeedOneSubscription(localTier: SubscriptionTier.Pro, stripeStatus: DefaultStripeStatus with
        {
            StripeStatus = "active",
            Tier = BillingTier.Free,
            Quantity = 5,
        });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.WebhookHandler.DidNotReceive().HandleTierCorrectionAsync(
            Arg.Any<int>(), SubscriptionTier.Free, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncCancelAtPeriodEnd_StripeFlipsToTrue_PropagatesToLocal()
    {
        // Intent: when Stripe reports cancel_at_period_end=true (customer clicked "Cancel at
        // period end" in the customer portal), that state must be reflected in the local
        // subscription immediately so the UI and renewal-prompt flow see it, not at period
        // end when the subscription transitions to canceled.
        TestSut sut = new();
        sut.SeedOneSubscription(
            localTier: SubscriptionTier.Pro,
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "active",
                Tier = BillingTier.Pro,
                Quantity = 5,
                CancelAtPeriodEnd = true,
            },
            localCancelAtPeriodEnd: false,
            tenantId: 7);
        sut.SubscriptionService.GetMachineCountForTenantAsync(7, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.SubscriptionRepo.Received(1).SetCancelAtPeriodEndAsync(
            7, true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncCancelAtPeriodEnd_AlreadyMatches_DoesNotWrite()
    {
        // Intent: idempotency. The sync job runs on a recurring schedule, so when the local
        // state already matches Stripe we must not write to the database. Without this guard
        // every sync cycle would touch UpdatedAt for every paying tenant.
        TestSut sut = new();
        sut.SeedOneSubscription(
            localTier: SubscriptionTier.Pro,
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "active",
                Tier = BillingTier.Pro,
                Quantity = 5,
                CancelAtPeriodEnd = true,
            },
            localCancelAtPeriodEnd: true);
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.SubscriptionRepo.DidNotReceive().SetCancelAtPeriodEndAsync(
            Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_PerTenantBillingApiThrows_ContinuesToNextTenant()
    {
        TestSut sut = new();
        List<TenantSubscription> subs = new() { BuildSub(1), BuildSub(2) };
        sut.SubscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>()).Returns(subs);
        sut.TenantRepo.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(BuildTenant(1, "ext-1"));
        sut.TenantRepo.GetTenantByIdAsync(2, Arg.Any<CancellationToken>()).Returns(BuildTenant(2, "ext-2"));
        sut.BillingClient.GetSubscriptionStatusAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns<Task<StripeSubscriptionStatus>>(_ => throw new HttpRequestException("Stripe API down"));
        sut.BillingClient.GetSubscriptionStatusAsync("ext-2", Arg.Any<CancellationToken>())
            .Returns(DefaultStripeStatus with { StripeStatus = "none" });

        await Assert.That(async () => await sut.Job.RunAsync(CancellationToken.None)).ThrowsNothing();

        await sut.BillingClient.Received(1).GetSubscriptionStatusAsync(
            "ext-2", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_SubscriptionRepositoryThrows_ExceptionPropagates()
    {
        // Top-level exception (outside the per-tenant try/catch) must propagate so Hangfire
        // records the failure. Distinguishes from the per-tenant swallow.
        TestSut sut = new();
        sut.SubscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<List<TenantSubscription>>>(_ => throw new InvalidOperationException("DB down"));

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.Job.RunAsync(CancellationToken.None));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).IsEqualTo("DB down");
    }

    // ========== Constructor null guards ==========

    [Test]
    public async Task Constructor_NullSubscriptionRepository_Throws()
        => await AssertConstructorThrows("subscriptionRepository", b => b with { SubscriptionRepo = null! });

    [Test]
    public async Task Constructor_NullTenantRepository_Throws()
        => await AssertConstructorThrows("tenantRepository", b => b with { TenantRepo = null! });

    [Test]
    public async Task Constructor_NullSubscriptionService_Throws()
        => await AssertConstructorThrows("subscriptionService", b => b with { SubscriptionService = null! });

    [Test]
    public async Task Constructor_NullBillingApiClient_Throws()
        => await AssertConstructorThrows("billingApiClient", b => b with { BillingClient = null! });

    [Test]
    public async Task Constructor_NullWebhookHandler_Throws()
        => await AssertConstructorThrows("webhookHandler", b => b with { WebhookHandler = null! });

    [Test]
    public async Task Constructor_NullBillingOptions_Throws()
        => await AssertConstructorThrows("billingOptions", b => b with { BillingOptions = null! });

    [Test]
    public async Task Constructor_NullLogger_Throws()
        => await AssertConstructorThrows("logger", b => b with { Logger = null! });

    // ========== Test helpers ==========

    private static async Task AssertConstructorThrows(string expectedParam, Func<ConstructorBundle, ConstructorBundle> mutate)
    {
        ConstructorBundle bundle = mutate(ConstructorBundle.AllNonNull());

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            StripeSyncJob _ = new(
                bundle.SubscriptionRepo, bundle.TenantRepo, bundle.SubscriptionService,
                bundle.BillingClient, bundle.WebhookHandler, bundle.BillingOptions, bundle.Logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo(expectedParam);
    }

    private static readonly StripeSubscriptionStatus DefaultStripeStatus =
        new(CancelAtPeriodEnd: false, StripeStatus: "", PriceId: "",
            Quantity: 0, CurrentPeriodEnd: null, Tier: BillingTier.Unspecified);

    private static TenantSubscription BuildSub(
        int tenantId,
        SubscriptionTier tier = SubscriptionTier.Pro,
        SubscriptionStatus status = SubscriptionStatus.Active,
        DateTimeOffset? currentPeriodEnd = null,
        bool cancelAtPeriodEnd = false) => new()
    {
        Id = tenantId,
        TenantId = tenantId,
        Tier = tier,
        Status = status,
        CurrentPeriodEnd = currentPeriodEnd,
        CancelAtPeriodEnd = cancelAtPeriodEnd,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

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

    private sealed class TestSut
    {
        public ISubscriptionRepository SubscriptionRepo { get; }
        public ITenantRepository TenantRepo { get; }
        public ISubscriptionService SubscriptionService { get; }
        public IBillingApiClient BillingClient { get; }
        public IBillingWebhookHandler WebhookHandler { get; }
        public ILogger<StripeSyncJob> Logger { get; }
        public StripeSyncJob Job { get; }

        public TestSut(BillingOptions? options = null)
        {
            SubscriptionRepo = Substitute.For<ISubscriptionRepository>();
            TenantRepo = Substitute.For<ITenantRepository>();
            SubscriptionService = Substitute.For<ISubscriptionService>();
            BillingClient = Substitute.For<IBillingApiClient>();
            WebhookHandler = Substitute.For<IBillingWebhookHandler>();
            Logger = Substitute.For<ILogger<StripeSyncJob>>();
            IOptions<BillingOptions> billingOptions = Options.Create(options ?? new BillingOptions
            {
                StripeProPriceId = ProPriceId,
                StripeTeamPriceId = TeamPriceId,
            });
            Job = new StripeSyncJob(SubscriptionRepo, TenantRepo, SubscriptionService,
                BillingClient, WebhookHandler, billingOptions, Logger);
        }

        public void SeedOneSubscription(
            SubscriptionTier localTier,
            StripeSubscriptionStatus stripeStatus,
            SubscriptionStatus localStatus = SubscriptionStatus.Active,
            DateTimeOffset? localCurrentPeriodEnd = null,
            bool localCancelAtPeriodEnd = false,
            int tenantId = 1)
        {
            string externalId = $"ext-{tenantId}";
            SubscriptionRepo.GetPaidSubscriptionsAsync(Arg.Any<CancellationToken>())
                .Returns(new List<TenantSubscription>
                {
                    BuildSub(tenantId, localTier, localStatus, localCurrentPeriodEnd, localCancelAtPeriodEnd),
                });
            TenantRepo.GetTenantByIdAsync(tenantId, Arg.Any<CancellationToken>())
                .Returns(BuildTenant(tenantId, externalId));
            BillingClient.GetSubscriptionStatusAsync(externalId, Arg.Any<CancellationToken>())
                .Returns(stripeStatus);
        }
    }

    private sealed record ConstructorBundle(
        ISubscriptionRepository SubscriptionRepo,
        ITenantRepository TenantRepo,
        ISubscriptionService SubscriptionService,
        IBillingApiClient BillingClient,
        IBillingWebhookHandler WebhookHandler,
        IOptions<BillingOptions> BillingOptions,
        ILogger<StripeSyncJob> Logger)
    {
        public static ConstructorBundle AllNonNull() => new(
            Substitute.For<ISubscriptionRepository>(),
            Substitute.For<ITenantRepository>(),
            Substitute.For<ISubscriptionService>(),
            Substitute.For<IBillingApiClient>(),
            Substitute.For<IBillingWebhookHandler>(),
            Options.Create(new BillingOptions
            {
                StripeProPriceId = ProPriceId,
                StripeTeamPriceId = TeamPriceId,
            }),
            Substitute.For<ILogger<StripeSyncJob>>());
    }

    [Test]
    public async Task MapPriceIdToTier_KnownProPriceId_ReturnsPro()
    {
        // Intent: pin the price-id-to-tier mapping so a regression to the tier resolution would
        // be caught immediately. This was a documented business invariant in the predecessor.
        StripeSyncJob job = new TestSut().Job;

        SubscriptionTier? tier = job.MapPriceIdToTier(ProPriceId, ProPriceId, TeamPriceId);

        await Assert.That(tier).IsEqualTo(SubscriptionTier.Pro);
    }

    [Test]
    public async Task MapPriceIdToTier_UnknownPriceId_ReturnsNull_NoTierCorrection()
    {
        // Intent: when Stripe returns a price id the system does not recognize (a new SKU rolled
        // out on the Stripe side before the deploy lands, or a corrupted webhook), the mapping
        // returns null and the surrounding code must NOT downgrade the customer's tier.
        // Without this guard, every customer on an unknown SKU would silently drop to Free.
        // This was a regression test in the predecessor's suite; preserving it here.
        StripeSyncJob job = new TestSut().Job;

        SubscriptionTier? tier = job.MapPriceIdToTier("price_unknown_xyz", ProPriceId, TeamPriceId);

        await Assert.That(tier).IsNull();
    }

    [Test]
    public async Task MapPriceIdToTier_EmptyPriceId_ReturnsNull()
    {
        // Intent: a missing price id (e.g., Stripe API returned a record without a subscription
        // item line) must be treated as unknown, not as Free or Pro by default.
        StripeSyncJob job = new TestSut().Job;

        SubscriptionTier? tier1 = job.MapPriceIdToTier("", ProPriceId, TeamPriceId);
        SubscriptionTier? tier2 = job.MapPriceIdToTier(null!, ProPriceId, TeamPriceId);

        await Assert.That(tier1).IsNull();
        await Assert.That(tier2).IsNull();
    }

    [Test]
    public async Task MapStripeStatusToLocal_Active_ReturnsActive()
    {
        // Intent: pin the canonical Active mapping. A regression here would silently break the
        // status drift correction path that flips local subscriptions to Active.
        SubscriptionStatus? result = StripeSyncJob.MapStripeStatusToLocal("active");

        await Assert.That(result).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task MapStripeStatusToLocal_PastDue_ReturnsPastDue()
    {
        // Intent: pin the canonical PastDue mapping so the dunning path remains wired.
        SubscriptionStatus? result = StripeSyncJob.MapStripeStatusToLocal("past_due");

        await Assert.That(result).IsEqualTo(SubscriptionStatus.PastDue);
    }

    [Test]
    public async Task MapStripeStatusToLocal_Canceled_ReturnsCanceled()
    {
        // Intent: pin the canonical Canceled mapping so the deactivation path remains wired.
        SubscriptionStatus? result = StripeSyncJob.MapStripeStatusToLocal("canceled");

        await Assert.That(result).IsEqualTo(SubscriptionStatus.Canceled);
    }

    [Test]
    public async Task MapStripeStatusToLocal_Trialing_ReturnsActive()
    {
        // Intent: pin "trialing" -> Active. The local SubscriptionStatus enum has no Trialing
        // member, so trial periods are treated as Active. Without this mapping the status drift
        // path would no-op while a customer is on trial, leaving stale Status values uncorrected.
        SubscriptionStatus? result = StripeSyncJob.MapStripeStatusToLocal("trialing");

        await Assert.That(result).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task MapStripeStatusToLocal_Unpaid_ReturnsPastDue()
    {
        // Intent: pin "unpaid" -> PastDue. Stripe distinguishes "unpaid" (terminal dunning state)
        // from "past_due" (in-flight retry state). The local enum collapses both into PastDue so
        // tenants on unpaid subscriptions still surface as PastDue in the UI and feature gates.
        SubscriptionStatus? result = StripeSyncJob.MapStripeStatusToLocal("unpaid");

        await Assert.That(result).IsEqualTo(SubscriptionStatus.PastDue);
    }

    [Test]
    public async Task MapStripeStatusToLocal_Incomplete_ReturnsNull()
    {
        // Intent: "incomplete" is an in-flight state Stripe uses while a checkout completes
        // (e.g., 3DS authentication pending). Pin the no-op so the sync job does not mis-correct
        // a local subscription mid-checkout.
        SubscriptionStatus? result = StripeSyncJob.MapStripeStatusToLocal("incomplete");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task MapStripeStatusToLocal_PausedOrUnknown_ReturnsNull()
    {
        // Intent: unknown or unmapped Stripe statuses must not trigger silent local state changes.
        // "paused" is a Stripe state we deliberately do not map; arbitrary garbage values must
        // also fall through to null rather than being mis-interpreted as Active.
        SubscriptionStatus? paused = StripeSyncJob.MapStripeStatusToLocal("paused");
        SubscriptionStatus? incompleteExpired = StripeSyncJob.MapStripeStatusToLocal("incomplete_expired");
        SubscriptionStatus? garbage = StripeSyncJob.MapStripeStatusToLocal("garbage_value");
        SubscriptionStatus? empty = StripeSyncJob.MapStripeStatusToLocal("");

        await Assert.That(paused).IsNull();
        await Assert.That(incompleteExpired).IsNull();
        await Assert.That(garbage).IsNull();
        await Assert.That(empty).IsNull();
    }

    [Test]
    public async Task RunAsync_AutomaticRetryAttribute_AllowsOneTransientRetry()
    {
        // A single retry with a 30 s delay absorbs the common case of one transient Stripe
        // gRPC hiccup without waiting the full 5 minutes for the next recurring tick. The job
        // body is idempotent (diff/upsert sync), so a retry is safe.
        MethodInfo method = typeof(StripeSyncJob).GetMethod(nameof(StripeSyncJob.RunAsync))
            ?? throw new InvalidOperationException("RunAsync not found");
        AutomaticRetryAttribute? attr = method.GetCustomAttribute<AutomaticRetryAttribute>();

        await Assert.That(attr).IsNotNull();
        await Assert.That(attr!.Attempts).IsEqualTo(1);
        await Assert.That(attr.DelaysInSeconds[0]).IsEqualTo(30);
    }

    [Test]
    public async Task RunAsync_DisableConcurrentExecution_TimeoutMatchesContract()
    {
        // Intent: pin the lock timeout. Use CustomAttributeData since DisableConcurrentExecutionAttribute
        // does not expose timeout via a public property.
        MethodInfo method = typeof(StripeSyncJob).GetMethod(nameof(StripeSyncJob.RunAsync))
            ?? throw new InvalidOperationException("RunAsync not found");
        CustomAttributeData? attrData = method.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(DisableConcurrentExecutionAttribute));

        await Assert.That(attrData).IsNotNull();
        await Assert.That(attrData!.ConstructorArguments.Count).IsEqualTo(1);
        int timeoutSeconds = (int)attrData.ConstructorArguments[0].Value!;
        await Assert.That(timeoutSeconds).IsEqualTo(480);
    }

    // ========== Fallback monthly/annual price ID mapping ==========

    [Test]
    public async Task SyncTier_MonthlyProPriceId_DetectsProTier()
    {
        // Intent: pin the fallback Pro monthly price ID mapping. When Stripe reports the price
        // id alone (BillingTier.Unspecified) the job must still resolve it to Pro via the monthly
        // SKU so a tier-drift correction fires. Without this, a paying tenant on the monthly SKU
        // could silently keep a stale local tier when their Stripe subscription tier changes.
        const string proMonthly = "price_pro_monthly_111";
        TestSut sut = new(new BillingOptions
        {
            StripeProPriceId = ProPriceId,
            StripeTeamPriceId = TeamPriceId,
            StripeProMonthlyPriceId = proMonthly,
        });
        sut.SeedOneSubscription(
            localTier: SubscriptionTier.Team,
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "active",
                PriceId = proMonthly,
                Quantity = 5,
                Tier = BillingTier.Unspecified,
            });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.WebhookHandler.Received(1).HandleTierCorrectionAsync(
            1, SubscriptionTier.Pro, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncTier_AnnualProPriceId_DetectsProTier()
    {
        // Intent: pin the fallback Pro annual price ID mapping. The annual SKU lives on a separate
        // option from the monthly SKU; both must resolve to Pro.
        const string proAnnual = "price_pro_annual_333";
        TestSut sut = new(new BillingOptions
        {
            StripeProPriceId = ProPriceId,
            StripeTeamPriceId = TeamPriceId,
            StripeProAnnualPriceId = proAnnual,
        });
        sut.SeedOneSubscription(
            localTier: SubscriptionTier.Team,
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "active",
                PriceId = proAnnual,
                Quantity = 5,
                Tier = BillingTier.Unspecified,
            });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.WebhookHandler.Received(1).HandleTierCorrectionAsync(
            1, SubscriptionTier.Pro, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncTier_MonthlyTeamPriceId_DetectsTeamTier()
    {
        // Intent: pin the fallback Team monthly price ID mapping.
        const string teamMonthly = "price_team_monthly_444";
        TestSut sut = new(new BillingOptions
        {
            StripeProPriceId = ProPriceId,
            StripeTeamPriceId = TeamPriceId,
            StripeTeamMonthlyPriceId = teamMonthly,
        });
        sut.SeedOneSubscription(
            localTier: SubscriptionTier.Pro,
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "active",
                PriceId = teamMonthly,
                Quantity = 5,
                Tier = BillingTier.Unspecified,
            });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.WebhookHandler.Received(1).HandleTierCorrectionAsync(
            1, SubscriptionTier.Team, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncTier_AnnualTeamPriceId_DetectsTeamTier()
    {
        // Intent: pin the fallback Team annual price ID mapping.
        const string teamAnnual = "price_team_annual_222";
        TestSut sut = new(new BillingOptions
        {
            StripeProPriceId = ProPriceId,
            StripeTeamPriceId = TeamPriceId,
            StripeTeamAnnualPriceId = teamAnnual,
        });
        sut.SeedOneSubscription(
            localTier: SubscriptionTier.Pro,
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "active",
                PriceId = teamAnnual,
                Quantity = 5,
                Tier = BillingTier.Unspecified,
            });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.WebhookHandler.Received(1).HandleTierCorrectionAsync(
            1, SubscriptionTier.Team, Arg.Any<CancellationToken>());
    }

    // ========== MapBillingTierToSubscriptionTier ==========

    [Test]
    public async Task MapBillingTierToSubscriptionTier_Pro_ReturnsPro()
    {
        // Intent: pin the gRPC BillingTier->local SubscriptionTier mapping for Pro. This is the
        // primary path the sync job uses when Stripe explicitly tags the subscription's tier.
        SubscriptionTier? result = StripeSyncJob.MapBillingTierToSubscriptionTier(BillingTier.Pro);

        await Assert.That(result).IsEqualTo(SubscriptionTier.Pro);
    }

    [Test]
    public async Task MapBillingTierToSubscriptionTier_Team_ReturnsTeam()
    {
        // Intent: pin Team mapping.
        SubscriptionTier? result = StripeSyncJob.MapBillingTierToSubscriptionTier(BillingTier.Team);

        await Assert.That(result).IsEqualTo(SubscriptionTier.Team);
    }

    [Test]
    public async Task MapBillingTierToSubscriptionTier_Free_ReturnsFree()
    {
        // Intent: Free must round-trip through the mapper as Free. The downstream code applies a
        // separate safety guard to prevent silent downgrades; that guard depends on this mapper
        // returning Free (not null) so the warning path fires instead of the silent no-op.
        SubscriptionTier? result = StripeSyncJob.MapBillingTierToSubscriptionTier(BillingTier.Free);

        await Assert.That(result).IsEqualTo(SubscriptionTier.Free);
    }

    [Test]
    public async Task MapBillingTierToSubscriptionTier_Unknown_ReturnsNull()
    {
        // Intent: an unknown gRPC enum value (out-of-range from a forward-incompatible billing
        // service) must return null so the surrounding code falls through to price-id resolution
        // rather than misinterpreting the tier.
        SubscriptionTier? result = StripeSyncJob.MapBillingTierToSubscriptionTier((BillingTier)999);

        await Assert.That(result).IsNull();
    }

    // ========== Status drift: PastDue->Active, Canceled->Active ==========

    [Test]
    public async Task SyncPaidSubscriptions_StatusDrift_PastDueToActive_CorrectsStatus()
    {
        // Intent: local says PastDue, Stripe now says active (the customer paid). The sync job
        // must transition local back to Active. Without this path the customer would stay locked
        // out of paid features after successful payment retry until the next webhook.
        TestSut sut = new();
        sut.SeedOneSubscription(
            localTier: SubscriptionTier.Pro,
            localStatus: SubscriptionStatus.PastDue,
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "active",
                Tier = BillingTier.Pro,
                Quantity = 5,
            });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.SubscriptionRepo.Received(1).SetSubscriptionActiveAsync(1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPaidSubscriptions_StatusDrift_CanceledToActive_CorrectsStatus()
    {
        // Intent: a previously canceled local subscription must transition to Active when Stripe
        // reports active (e.g., the customer reactivated via the Stripe portal). Without this,
        // a reactivated customer would stay locked at canceled until the next webhook lands.
        TestSut sut = new();
        sut.SeedOneSubscription(
            localTier: SubscriptionTier.Pro,
            localStatus: SubscriptionStatus.Canceled,
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "active",
                Tier = BillingTier.Pro,
                Quantity = 5,
            });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.SubscriptionRepo.Received(1).SetSubscriptionActiveAsync(1, Arg.Any<CancellationToken>());
    }

    // ========== Period end anti-thrash ==========

    [Test]
    public async Task SyncPeriodEndAsync_PeriodEndWithin1Minute_IsNotStale_NoUpdate()
    {
        // Intent: anti-thrash invariant. Local and Stripe can disagree by up to one minute due to
        // clock skew or rounding; the sync job must not write on this tiny drift every cycle.
        // Without this guard every hourly run would touch UpdatedAt on every paying tenant.
        DateTimeOffset localEnd = DateTimeOffset.UtcNow.AddDays(30);
        DateTimeOffset stripeEnd = localEnd.AddSeconds(30); // 30s drift — within the 1-minute window
        TestSut sut = new();
        sut.SeedOneSubscription(
            localTier: SubscriptionTier.Pro,
            localCurrentPeriodEnd: localEnd,
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "active",
                Tier = BillingTier.Pro,
                Quantity = 5,
                CurrentPeriodEnd = stripeEnd,
            });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.SubscriptionRepo.DidNotReceive().UpdateSubscriptionPeriodEndAsync(
            Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SyncPeriodEndAsync_LocalNull_StripeHasValue_Updates()
    {
        // Intent: when the local record has never been populated with a period end (e.g., a
        // legacy subscription created before this field existed, or one that lost the value),
        // the staleness check must treat null as stale and write the Stripe value. Without this,
        // billing dashboards and renewal banners stay blank indefinitely for affected tenants.
        DateTimeOffset stripeEnd = DateTimeOffset.UtcNow.AddDays(30);
        TestSut sut = new();
        sut.SeedOneSubscription(
            localTier: SubscriptionTier.Pro,
            localCurrentPeriodEnd: null,
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "active",
                Tier = BillingTier.Pro,
                Quantity = 5,
                CurrentPeriodEnd = stripeEnd,
            });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(5);

        await sut.Job.RunAsync(CancellationToken.None);

        await sut.SubscriptionRepo.Received(1).UpdateSubscriptionPeriodEndAsync(
            1, stripeEnd, Arg.Any<CancellationToken>());
    }

    // ========== Per-step failure containment ==========

    [Test]
    public async Task SyncPaidSubscriptions_QuantityUpdateFails_LogsWarningAndContinues()
    {
        // Intent: when the billing API returns success=false for the usage report (e.g., transient
        // billing-side failure), the job must log a warning and continue to the remaining sync
        // sub-operations (tier, status, period end). Without this containment, a failed quantity
        // report would prevent tier-drift correction on the same cycle for the same tenant.
        DateTimeOffset periodEnd = DateTimeOffset.UtcNow.AddDays(15);
        TestSut sut = new();
        sut.SeedOneSubscription(
            localTier: SubscriptionTier.Pro,
            localCurrentPeriodEnd: periodEnd,
            stripeStatus: DefaultStripeStatus with
            {
                StripeStatus = "active",
                Tier = BillingTier.Pro,
                Quantity = 5,
                CurrentPeriodEnd = periodEnd,
            });
        sut.SubscriptionService.GetMachineCountForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(10);
        sut.BillingClient.ReportMachineUsageAsync("ext-1", 10, Arg.Any<CancellationToken>())
            .Returns(false);

        await Assert.That(async () => await sut.Job.RunAsync(CancellationToken.None)).ThrowsNothing();

        await sut.BillingClient.Received(1).ReportMachineUsageAsync("ext-1", 10, Arg.Any<CancellationToken>());
        sut.Logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
