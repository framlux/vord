// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Test.Infrastructure;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="BillingWebhookHandler"/>.
/// </summary>
public class BillingWebhookHandlerTests
{
    /// <summary>
    /// Seeds the TierFeatureLimits table with the standard tier configurations used by tests.
    /// </summary>
    private static async Task SeedTierFeatureLimitsAsync(DatabaseContext context)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await context.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Free,
            MachineLimit = 3,
            RetentionDays = 1,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            MemberLimit = 1,
            MinimumBillableMachines = 0,
            UpdatedAt = now,
        });

        await context.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Pro,
            MachineLimit = 1000,
            RetentionDays = 60,
            AlertRuleLimit = 10,
            WebhookLimit = 5,
            MemberLimit = 5,
            MinimumBillableMachines = 1,
            UpdatedAt = now,
        });

        await context.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Team,
            MachineLimit = 10000,
            RetentionDays = 365,
            AlertRuleLimit = 25,
            WebhookLimit = 15,
            MemberLimit = int.MaxValue,
            MinimumBillableMachines = 3,
            UpdatedAt = now,
        });
    }

    /// <summary>
    /// Whether the prior subscription handed to the provisioner is the row the handler replaced. The
    /// null test lives here rather than in the matcher because an expression tree cannot hold a
    /// pattern.
    /// </summary>
    private static bool PriorIs(TenantSubscription? prior, SubscriptionTier tier, SubscriptionStatus status)
    {
        return (prior is not null) && (prior.Tier == tier) && (prior.Status == status);
    }

    /// <summary>
    /// The end of the agreement term the Enterprise rows in these tests are seeded with.
    /// </summary>
    private static readonly DateTimeOffset EnterpriseTermEnd = new(2027, 10, 4, 23, 59, 59, TimeSpan.Zero);

    /// <summary>
    /// Seeds an Enterprise subscription for the tenant, as applying an agreement leaves it.
    /// </summary>
    private static async Task SeedEnterpriseSubscriptionAsync(TestDatabaseFactory dbFactory, int tenantId = 1)
    {
        TenantSubscription enterprise = TestDataBuilder.BuildSubscription(tenantId: tenantId, tier: SubscriptionTier.Enterprise);
        enterprise.AppliedAgreementRevision = 1;
        enterprise.CurrentPeriodEnd = EnterpriseTermEnd;
        enterprise.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(enterprise);
    }

    private static async Task<int> CountAuditRowsAsync(TestDatabaseFactory dbFactory, AuditAction action, int tenantId = 1)
    {
        int count = await dbFactory.Context.AuditLog
            .Where(a => (a.TenantId == tenantId) && (a.Action == action))
            .CountAsync();

        return count;
    }

    /// <summary>
    /// A transaction provider whose transaction is a substitute, so a test can see where the commit
    /// falls among the handler's other calls. The writes still reach the database, outside any
    /// transaction, which is all these ordering tests need.
    /// </summary>
    private static (IDatabaseTransaction Transaction, IDatabaseTransactionProvider Provider) BuildObservableTransaction()
    {
        IDatabaseTransaction transaction = Substitute.For<IDatabaseTransaction>();
        IDatabaseTransactionProvider provider = Substitute.For<IDatabaseTransactionProvider>();
        provider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(transaction));

        return (transaction, provider);
    }

    private static BillingWebhookHandler CreateHandler(
        TestDatabaseFactory dbFactory,
        IDowngradeCleanupService? cleanupService = null,
        IBuiltInAlertRuleProvisioner? provisioner = null,
        IDatabaseTransactionProvider? transactionProvider = null,
        ILogger<BillingWebhookHandler>? logger = null)
    {
        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        return new BillingWebhookHandler(
            transactionProvider ?? repo,
            repo,
            repo,
            provisioner ?? Substitute.For<IBuiltInAlertRuleProvisioner>(),
            cleanupService ?? Substitute.For<IDowngradeCleanupService>(),
            new RetentionReclassifyDispatcher(
                Substitute.For<IBackgroundJobClient>(), NullLogger<RetentionReclassifyDispatcher>.Instance),
            logger ?? NullLogger<BillingWebhookHandler>.Instance);
    }

    /// <summary>
    /// Pins the ordering the retention reclassification depends on: the tier change is observed by the
    /// subscription seam mid-transaction, but nothing may reach Hangfire until the handler has
    /// committed. Enqueuing inside the transaction would let the job read pre-change state, compute the
    /// old retention class, move nothing, and never be re-enqueued.
    /// </summary>
    [Test]
    public async Task HandleCheckoutCompletedAsync_EnqueuesReclassifyStrictlyAfterTheCommit()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription seeded = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        seeded.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(seeded);

        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());
        IDatabaseTransaction transaction = Substitute.For<IDatabaseTransaction>();
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(transaction));

        IBackgroundJobClient backgroundJobs = Substitute.For<IBackgroundJobClient>();
        RetentionReclassifyDispatcher dispatcher = new(
            backgroundJobs, NullLogger<RetentionReclassifyDispatcher>.Instance);

        // The production graph: the handler writes through the caching subscription decorator, which is
        // where a tier change is detected.
        CachingSubscriptionRepository subscriptions = new(
            repo,
            FakeRedisConnection.Create(),
            Options.Create(new RedisOptions { ConnectionString = "localhost", SubscriptionCacheTtlSeconds = 30 }),
            dispatcher);

        BillingWebhookHandler handler = new(
            transactionProvider,
            repo,
            subscriptions,
            Substitute.For<IBuiltInAlertRuleProvisioner>(),
            Substitute.For<IDowngradeCleanupService>(),
            dispatcher,
            NullLogger<BillingWebhookHandler>.Instance);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        Received.InOrder(() =>
        {
            transaction.CommitAsync(Arg.Any<CancellationToken>());
            backgroundJobs.Create(Arg.Any<Job>(), Arg.Any<IState>());
        });

        backgroundJobs.Received(1).Create(
            Arg.Is<Job>(j => (j.Method.Name == nameof(RetentionReclassifyJob.RunAsync))
                && ((int)j.Args[0] == 1)),
            Arg.Any<IState>());
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_UpgradesToPro()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task HandleSubscriptionUpdatedAsync_UpdatesPeriodEnd()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);
        DateTimeOffset newPeriodEnd = DateTimeOffset.UtcNow.AddDays(30);

        await handler.HandleSubscriptionUpdatedAsync(1, newPeriodEnd, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        // SQLite stores DateTimeOffset as TEXT, compare by rough equality
        await Assert.That(updated!.CurrentPeriodEnd.HasValue).IsTrue();
        TimeSpan difference = (updated.CurrentPeriodEnd!.Value - newPeriodEnd).Duration();
        await Assert.That(difference.TotalSeconds).IsLessThan(2);
    }

    [Test]
    public async Task HandleSubscriptionDeletedAsync_RevertsToFreeTierAndCleansUp()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDowngradeCleanupService cleanupService = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanupService);

        await handler.HandleSubscriptionDeletedAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        await cleanupService.Received(1).CleanupForFreeTierAsync(1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleSubscriptionDeletedAsync_TeamTier_RevertsToFreeAndCleansUp()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDowngradeCleanupService cleanupService = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanupService);

        await handler.HandleSubscriptionDeletedAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        await cleanupService.Received(1).CleanupForFreeTierAsync(1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleSubscriptionDeletedAsync_EndToEnd_KeepsEveryMachine()
    {
        // The full subscription.deleted flow with a real cleanup service must leave the tenant Free
        // with every machine still registered and still able to authenticate, however far over the
        // Free limit it is. Over the limit, registration refuses new machines instead.
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro));

        const string newestKey = "key-of-the-newest-machine";
        for (int i = 0; i < 4; i++)
        {
            await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 1));
        }
        string newestKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(newestKey)));
        await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 1, apiKeyHash: newestKeyHash));

        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());
        DowngradeCleanupService cleanup = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanup);

        await handler.HandleSubscriptionDeletedAsync(1, CancellationToken.None);

        TenantSubscription reverted = await dbFactory.Context.TenantSubscriptions.FirstAsync(s => s.TenantId == 1);
        await Assert.That(reverted.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(await dbFactory.Context.Machines.CountAsync(m => (m.TenantId == 1) && (m.IsDeleted == false))).IsEqualTo(5);
        await Assert.That(await repo.GetMachineByApiKeyAsync(newestKey, CancellationToken.None)).IsNotNull();
    }

    [Test]
    public async Task HandlePaymentFailedAsync_SetsStatusToPastDue()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandlePaymentFailedAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(SubscriptionStatus.PastDue);
    }

    [Test]
    public async Task HandlePaymentFailedAsync_NoMatchingSubscription_NoOp()
    {
        using TestDatabaseFactory dbFactory = new();
        BillingWebhookHandler handler = CreateHandler(dbFactory);

        // Should not throw
        await handler.HandlePaymentFailedAsync(999, CancellationToken.None);

        int count = await dbFactory.Context.TenantSubscriptions.CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task HandleSubscriptionDeletedAsync_NoMatchingSubscription_NoOp()
    {
        using TestDatabaseFactory dbFactory = new();
        BillingWebhookHandler handler = CreateHandler(dbFactory);

        // Should not throw
        await handler.HandleSubscriptionDeletedAsync(999, CancellationToken.None);

        int count = await dbFactory.Context.TenantSubscriptions.CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_UpgradesToTeam_SetsRetention365()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Team, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Team);
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_NoExistingSubscription_NoOp()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        BillingWebhookHandler handler = CreateHandler(dbFactory);

        // No subscription exists for tenant 999 — should not throw
        await handler.HandleCheckoutCompletedAsync(999, SubscriptionTier.Pro, CancellationToken.None);

        int count = await dbFactory.Context.TenantSubscriptions.CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }

    /// <summary>
    /// What a transition may restore depends on the state it replaced, so the handler has to report
    /// the row as it stood before the write rather than the one it just made. Reading it afterwards
    /// would report Pro/Active for every checkout and lose the distinction entirely.
    /// </summary>
    [Test]
    public async Task HandleCheckoutCompletedAsync_ReportsTheStateItReplaced()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        await provisioner.Received(1).RestoreForTierAsync(
            1,
            Arg.Is<TenantSubscription?>(s => PriorIs(s, SubscriptionTier.Free, SubscriptionStatus.Active)),
            SubscriptionTier.Pro,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A Pro downgrade freezes the custom rules, and the journey back to Team is the one customers
    /// actually make, so the handler has to report both ends of it.
    /// </summary>
    [Test]
    public async Task HandleCheckoutCompletedAsync_Team_ReportsArrivalAtTeamFromPro()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Team, CancellationToken.None);

        await provisioner.Received(1).RestoreForTierAsync(
            1,
            Arg.Is<TenantSubscription?>(s => PriorIs(s, SubscriptionTier.Pro, SubscriptionStatus.Active)),
            SubscriptionTier.Team,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A Pro checkout is not an arrival at Team, and reporting the tier it actually wrote is what
    /// keeps a downgraded tenant from recovering Team's rules by paying for Pro.
    /// </summary>
    [Test]
    public async Task HandleCheckoutCompletedAsync_Pro_ReportsProNotTeam()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        await provisioner.Received(1).RestoreForTierAsync(
            1,
            Arg.Any<TenantSubscription?>(),
            SubscriptionTier.Pro,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_AlreadyOnPro_StaysOnPro()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_ProToTeam_ChangesToTeam()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Team, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Team);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task HandleDowngradeToProAsync_SetsCorrectValues()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleDowngradeToProAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    /// <summary>
    /// The evaluator is authorship-blind, so a Team-authored rule left enabled keeps firing on a Pro
    /// plan. The billing-initiated downgrade must freeze the Team-only resources itself rather than
    /// relying on the in-product endpoint, which is a different caller entirely.
    /// </summary>
    [Test]
    public async Task HandleDowngradeToProAsync_FreezesTeamOnlyResources()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDowngradeCleanupService cleanupService = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanupService);

        await handler.HandleDowngradeToProAsync(1, CancellationToken.None);

        await cleanupService.Received(1).CleanupForProTierAsync(1, Arg.Any<CancellationToken>());
        await cleanupService.DidNotReceive().CleanupForFreeTierAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The freeze must run inside the handler's transaction. The tier write holds the subscription
    /// row's lock until the commit, so an agreement being applied waits for the freeze; run after the
    /// commit, the freeze could land on a tenant that became Enterprise in between.
    /// </summary>
    [Test]
    public async Task HandleDowngradeToProAsync_FreezesInsideTheTransactionBeforeTheCommit()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);
        (IDatabaseTransaction transaction, IDatabaseTransactionProvider transactionProvider) = BuildObservableTransaction();
        IDowngradeCleanupService cleanupService = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanupService, transactionProvider: transactionProvider);

        await handler.HandleDowngradeToProAsync(1, CancellationToken.None);

        Received.InOrder(() =>
        {
            cleanupService.CleanupForProTierAsync(1, Arg.Any<CancellationToken>());
            transaction.CommitAsync(Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task HandleDowngradeToProAsync_WhenTheFreezeFails_LeavesTheTierUntouched()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);
        IDowngradeCleanupService cleanupService = Substitute.For<IDowngradeCleanupService>();
        cleanupService.CleanupForProTierAsync(1, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("freeze failed")));
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanupService);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleDowngradeToProAsync(1, CancellationToken.None));

        TenantSubscription? row = await dbFactory.Context.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Team);
        await Assert.That(await CountAuditRowsAsync(dbFactory, AuditAction.SubscriptionDowngraded)).IsEqualTo(0);
    }

    /// <summary>
    /// The Free cleanup runs before the commit, so the tier write's row lock makes a concurrent
    /// agreement apply wait for it instead of landing between a committed Free tier and a cleanup that
    /// would then hit an Enterprise tenant.
    /// </summary>
    [Test]
    public async Task HandleSubscriptionDeletedAsync_CleansUpBeforeTheCommit()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);
        (IDatabaseTransaction transaction, IDatabaseTransactionProvider transactionProvider) = BuildObservableTransaction();
        IDowngradeCleanupService cleanupService = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanupService, transactionProvider: transactionProvider);

        await handler.HandleSubscriptionDeletedAsync(1, CancellationToken.None);

        Received.InOrder(() =>
        {
            cleanupService.CleanupForFreeTierAsync(1, Arg.Any<CancellationToken>());
            transaction.CommitAsync(Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task HandleAccountCanceledAsync_CleansUpBeforeTheCommit()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);
        (IDatabaseTransaction transaction, IDatabaseTransactionProvider transactionProvider) = BuildObservableTransaction();
        IDowngradeCleanupService cleanupService = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanupService, transactionProvider: transactionProvider);

        await handler.HandleAccountCanceledAsync(1, CancellationToken.None);

        Received.InOrder(() =>
        {
            cleanupService.CleanupForFreeTierAsync(1, Arg.Any<CancellationToken>());
            transaction.CommitAsync(Arg.Any<CancellationToken>());
        });
    }

    /// <summary>
    /// The tier change and the cleanup are one unit. If the cleanup fails the tier must not have
    /// changed, so the redelivered webhook runs the whole thing again instead of finding a tier that
    /// is already Free and a tenant whose alerting and integrations were never disabled.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task HandleFreeTierReversion_WhenTheCleanupFails_LeavesTheTierAndAuditUntouched(bool accountCanceled)
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);
        IDowngradeCleanupService cleanupService = Substitute.For<IDowngradeCleanupService>();
        cleanupService.CleanupForFreeTierAsync(1, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("cleanup failed")));
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanupService);

        await Assert.ThrowsAsync<InvalidOperationException>(() => accountCanceled
            ? handler.HandleAccountCanceledAsync(1, CancellationToken.None)
            : handler.HandleSubscriptionDeletedAsync(1, CancellationToken.None));

        TenantSubscription? row = await dbFactory.Context.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Team);
        await Assert.That(row.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(await CountAuditRowsAsync(dbFactory, AuditAction.SubscriptionDowngraded)).IsEqualTo(0);
    }

    /// <summary>
    /// Drift repair reaches Pro by the same route a downgrade does, so it owes the same freeze. The
    /// sync job never corrects downwards to Free, so Pro is the only correction that loses an
    /// entitlement.
    /// </summary>
    [Test]
    public async Task HandleTierCorrectionAsync_ToPro_FreezesTeamOnlyResources()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDowngradeCleanupService cleanupService = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanupService);

        await handler.HandleTierCorrectionAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        await cleanupService.Received(1).CleanupForProTierAsync(1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleTierCorrectionAsync_ToPro_FreezesBeforeTheCommitAndRestoresAfterIt()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);
        (IDatabaseTransaction transaction, IDatabaseTransactionProvider transactionProvider) = BuildObservableTransaction();
        IDowngradeCleanupService cleanupService = Substitute.For<IDowngradeCleanupService>();
        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(
            dbFactory, cleanupService: cleanupService, provisioner: provisioner, transactionProvider: transactionProvider);

        await handler.HandleTierCorrectionAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        Received.InOrder(() =>
        {
            cleanupService.CleanupForProTierAsync(1, Arg.Any<CancellationToken>());
            transaction.CommitAsync(Arg.Any<CancellationToken>());
            provisioner.RestoreForTierAsync(
                1, Arg.Any<TenantSubscription?>(), SubscriptionTier.Pro, SubscriptionStatus.Active, Arg.Any<CancellationToken>());
        });
    }

    /// <summary>
    /// A correction upwards to Team gains the entitlement rather than losing it, and the thaw is the
    /// provisioner's. Freezing here would undo it.
    /// </summary>
    [Test]
    public async Task HandleTierCorrectionAsync_ToTeam_DoesNotFreeze()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDowngradeCleanupService cleanupService = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanupService);

        await handler.HandleTierCorrectionAsync(1, SubscriptionTier.Team, CancellationToken.None);

        await cleanupService.DidNotReceive().CleanupForProTierAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandlePaymentSucceededAsync_SetsStatusToActive()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.PastDue);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandlePaymentSucceededAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task HandlePaymentSucceededAsync_NoSubscription_NoOp()
    {
        using TestDatabaseFactory dbFactory = new();
        BillingWebhookHandler handler = CreateHandler(dbFactory);

        // Should not throw when no subscription exists
        await handler.HandlePaymentSucceededAsync(999, CancellationToken.None);

        int count = await dbFactory.Context.TenantSubscriptions.CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }

    // ========== Payment failure changes status but NOT tier or limits ==========

    [Test]
    public async Task HandlePaymentFailedAsync_ChangesPastDue_PreservesTierAndLimits()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        // Capture the original tier and its limits before the payment failure
        TierFeatureLimit? proLimits = await dbFactory.Context.TierFeatureLimits
            .FirstOrDefaultAsync(l => l.Tier == SubscriptionTier.Pro);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandlePaymentFailedAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();

        // Status changed to PastDue
        await Assert.That(updated!.Status).IsEqualTo(SubscriptionStatus.PastDue);

        // Tier remains Pro (not downgraded to Free or any other tier)
        await Assert.That(updated.Tier).IsEqualTo(SubscriptionTier.Pro);

        // The tier feature limits are still intact (Pro tier limits still apply)
        TierFeatureLimit? proLimitsAfter = await dbFactory.Context.TierFeatureLimits
            .FirstOrDefaultAsync(l => l.Tier == SubscriptionTier.Pro);
        await Assert.That(proLimitsAfter).IsNotNull();
        await Assert.That(proLimitsAfter!.MachineLimit).IsEqualTo(proLimits!.MachineLimit);
        await Assert.That(proLimitsAfter.RetentionDays).IsEqualTo(proLimits.RetentionDays);
        await Assert.That(proLimitsAfter.AlertRuleLimit).IsEqualTo(proLimits.AlertRuleLimit);
        await Assert.That(proLimitsAfter.WebhookLimit).IsEqualTo(proLimits.WebhookLimit);
    }

    [Test]
    public async Task HandlePaymentFailedAsync_TeamTier_PreservesTierAtTeam()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team, status: SubscriptionStatus.Active);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandlePaymentFailedAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(SubscriptionStatus.PastDue);
        await Assert.That(updated.Tier).IsEqualTo(SubscriptionTier.Team);
    }

    [Test]
    public async Task HandleTierCorrectionAsync_UpdatesTierAndCreatesAuditLog()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleTierCorrectionAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);

        // Verify audit log was created
        int auditCount = await dbFactory.Context.AuditLog
            .Where(a => a.TenantId == 1 && a.Action == AuditAction.SubscriptionUpgraded)
            .CountAsync();
        await Assert.That(auditCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task HandleTierCorrectionAsync_TeamToFree_CorrectsTier()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 2, tier: SubscriptionTier.Team);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleTierCorrectionAsync(2, SubscriptionTier.Free, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 2);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Free);
    }

    /// <summary>
    /// Tier correction is the drift repair the sync job runs when a checkout webhook never arrived,
    /// so it is precisely the path where a tenant reaches a paid tier having never been provisioned.
    /// </summary>
    [Test]
    public async Task HandleTierCorrectionAsync_ToPro_ReportsTheStateItReplaced()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner);

        await handler.HandleTierCorrectionAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        await provisioner.Received(1).RestoreForTierAsync(
            1,
            Arg.Is<TenantSubscription?>(s => PriorIs(s, SubscriptionTier.Free, SubscriptionStatus.Active)),
            SubscriptionTier.Pro,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A correction downwards to Free is not an entitlement. The handler still reports it rather than
    /// deciding for itself — Free is refused inside the provisioner, where the rule is stated once.
    /// </summary>
    [Test]
    public async Task HandleTierCorrectionAsync_ToFree_ReportsFree()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner);

        await handler.HandleTierCorrectionAsync(1, SubscriptionTier.Free, CancellationToken.None);

        await provisioner.Received(1).RestoreForTierAsync(
            1,
            Arg.Any<TenantSubscription?>(),
            SubscriptionTier.Free,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
        await provisioner.DidNotReceive().EnsureProvisionedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Team to Pro and back: the drift repair reports arrival at Team from Pro, which is the pair
    /// that thaws the custom rules the downgrade froze.
    /// </summary>
    [Test]
    public async Task HandleTierCorrectionAsync_ToTeam_ReportsArrivalAtTeamFromPro()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner);

        await handler.HandleTierCorrectionAsync(1, SubscriptionTier.Team, CancellationToken.None);

        await provisioner.Received(1).RestoreForTierAsync(
            1,
            Arg.Is<TenantSubscription?>(s => PriorIs(s, SubscriptionTier.Pro, SubscriptionStatus.Active)),
            SubscriptionTier.Team,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Cancel then reactivate: the cancellation disabled every rule the tenant had while leaving the
    /// tier alone, so the prior status is the whole of what makes this a recovery.
    /// </summary>
    [Test]
    public async Task HandlePaymentSucceededAsync_Canceled_ReportsTheCanceledPrior()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Canceled);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner);

        await handler.HandlePaymentSucceededAsync(1, CancellationToken.None);

        await provisioner.Received(1).RestoreForTierAsync(
            1,
            Arg.Is<TenantSubscription?>(s => PriorIs(s, SubscriptionTier.Pro, SubscriptionStatus.Canceled)),
            SubscriptionTier.Pro,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A declined card writes PastDue and disables nothing, so the retry that succeeds must be
    /// reported as coming from PastDue. Reading the row after the write would report Active and make
    /// every dunning cycle indistinguishable from a cancellation recovery.
    /// </summary>
    [Test]
    public async Task HandlePaymentSucceededAsync_PastDue_ReportsPastDueNotTheStatusItJustWrote()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.PastDue);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner);

        await handler.HandlePaymentSucceededAsync(1, CancellationToken.None);

        await provisioner.Received(1).RestoreForTierAsync(
            1,
            Arg.Is<TenantSubscription?>(s => PriorIs(s, SubscriptionTier.Pro, SubscriptionStatus.PastDue)),
            SubscriptionTier.Pro,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An invoice carries no tier, so the tier reported is whatever the tenant already held. A Free
    /// tenant recovering a payment is still a Free tenant and nothing is seeded for it.
    /// </summary>
    [Test]
    public async Task HandlePaymentSucceededAsync_FreeTier_ReportsFreeAndProvisionsNothing()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Free, status: SubscriptionStatus.PastDue);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner);

        await handler.HandlePaymentSucceededAsync(1, CancellationToken.None);

        await provisioner.Received(1).RestoreForTierAsync(
            1,
            Arg.Any<TenantSubscription?>(),
            SubscriptionTier.Free,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
        await provisioner.DidNotReceive().EnsureProvisionedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandlePaymentSucceededAsync_NoSubscription_RestoresNothing()
    {
        using TestDatabaseFactory dbFactory = new();
        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner);

        await handler.HandlePaymentSucceededAsync(999, CancellationToken.None);

        await provisioner.DidNotReceive().RestoreForTierAsync(
            Arg.Any<int>(),
            Arg.Any<TenantSubscription?>(),
            Arg.Any<SubscriptionTier>(),
            Arg.Any<SubscriptionStatus>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A canceled Team tenant had its custom rules disabled along with everything else, so both the
    /// tier and the canceled prior have to reach the provisioner — the built-ins alone would be a
    /// silent demotion to Pro.
    /// </summary>
    [Test]
    public async Task HandlePaymentSucceededAsync_TeamTier_ReportsTeamAndTheCanceledPrior()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Team, status: SubscriptionStatus.Canceled);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner);

        await handler.HandlePaymentSucceededAsync(1, CancellationToken.None);

        await provisioner.Received(1).RestoreForTierAsync(
            1,
            Arg.Is<TenantSubscription?>(s => PriorIs(s, SubscriptionTier.Team, SubscriptionStatus.Canceled)),
            SubscriptionTier.Team,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The billing side sends this action for every paid invoice, including each ordinary monthly
    /// renewal, so a subscription that was already active is not recovering from anything and must be
    /// reported as such.
    /// </summary>
    [Test]
    public async Task HandlePaymentSucceededAsync_AlreadyActive_ReportsAnActivePrior()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner);

        await handler.HandlePaymentSucceededAsync(1, CancellationToken.None);

        await provisioner.Received(1).RestoreForTierAsync(
            1,
            Arg.Is<TenantSubscription?>(s => PriorIs(s, SubscriptionTier.Pro, SubscriptionStatus.Active)),
            SubscriptionTier.Pro,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAccountCanceledAsync_DeactivatesSubscriptionAndCleansUp()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDowngradeCleanupService cleanupService = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanupService);

        await handler.HandleAccountCanceledAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(SubscriptionStatus.Canceled);

        // Verify cleanup was called
        await cleanupService.Received(1).CleanupForFreeTierAsync(1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAccountCanceledAsync_CreatesAuditLog()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 3, tier: SubscriptionTier.Team);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleAccountCanceledAsync(3, CancellationToken.None);

        int auditCount = await dbFactory.Context.AuditLog
            .Where(a => a.TenantId == 3 && a.Action == AuditAction.SubscriptionDowngraded)
            .CountAsync();
        await Assert.That(auditCount).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task HandleSubscriptionDeletedAsync_EnterpriseTenant_ChangesNothingAndRunsNoCleanup()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        await SeedEnterpriseSubscriptionAsync(dbFactory);
        IDowngradeCleanupService cleanup = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanup);

        await handler.HandleSubscriptionDeletedAsync(1, CancellationToken.None);

        TenantSubscription? row = await dbFactory.Context.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await Assert.That(row.CurrentPeriodEnd.HasValue).IsTrue();
        await cleanup.DidNotReceive().CleanupForFreeTierAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await Assert.That(await CountAuditRowsAsync(dbFactory, AuditAction.SubscriptionDowngraded)).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAccountCanceledAsync_EnterpriseTenant_ChangesNothingAndRunsNoCleanup()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        await SeedEnterpriseSubscriptionAsync(dbFactory);
        IDowngradeCleanupService cleanup = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanup);

        await handler.HandleAccountCanceledAsync(1, CancellationToken.None);

        TenantSubscription? row = await dbFactory.Context.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await Assert.That(row.Status).IsEqualTo(SubscriptionStatus.Active);
        await cleanup.DidNotReceive().CleanupForFreeTierAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await Assert.That(await CountAuditRowsAsync(dbFactory, AuditAction.SubscriptionDowngraded)).IsEqualTo(0);
    }

    [Test]
    public async Task HandleDowngradeToProAsync_EnterpriseTenant_RunsNoProCleanup()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        await SeedEnterpriseSubscriptionAsync(dbFactory);
        IDowngradeCleanupService cleanup = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanup);

        await handler.HandleDowngradeToProAsync(1, CancellationToken.None);

        TenantSubscription? row = await dbFactory.Context.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await Assert.That(row.CurrentPeriodEnd.HasValue).IsTrue();
        await cleanup.DidNotReceive().CleanupForProTierAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await Assert.That(await CountAuditRowsAsync(dbFactory, AuditAction.SubscriptionDowngraded)).IsEqualTo(0);
    }

    /// <summary>
    /// A completed checkout for a tenant with no subscription row is a customer who paid and received
    /// nothing, so it must be loud enough to be acted on.
    /// </summary>
    [Test]
    public async Task HandleCheckoutCompletedAsync_NoSubscriptionRow_LogsTheLostUpgradeAsAnErrorAndRecordsNothing()
    {
        using TestDatabaseFactory dbFactory = new();
        FakeLogger<BillingWebhookHandler> logger = new();
        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner, logger: logger);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Team, CancellationToken.None);

        IReadOnlyList<FakeLogRecord> errors = [.. logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Error)];
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("tenant 1 (tier Team) found no subscription row");
        await Assert.That(await dbFactory.Context.TenantSubscriptions.AnyAsync(s => s.TenantId == 1)).IsFalse();
        await provisioner.DidNotReceive().RestoreForTierAsync(
            Arg.Any<int>(), Arg.Any<TenantSubscription?>(), Arg.Any<SubscriptionTier>(), Arg.Any<SubscriptionStatus>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An Enterprise tenant skipping a checkout is the expected outcome, not a lost payment, so it
    /// must not raise an error.
    /// </summary>
    [Test]
    public async Task HandleCheckoutCompletedAsync_EnterpriseTenant_LogsInformationAndNoError()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedEnterpriseSubscriptionAsync(dbFactory);
        FakeLogger<BillingWebhookHandler> logger = new();
        BillingWebhookHandler handler = CreateHandler(dbFactory, logger: logger);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Team, CancellationToken.None);

        IReadOnlyList<FakeLogRecord> records = logger.Collector.GetSnapshot();
        await Assert.That(records.Count(r => r.Level == LogLevel.Error)).IsEqualTo(0);
        await Assert.That(records.Count(r => r.Level == LogLevel.Information)).IsEqualTo(1);
        await Assert.That(records.Single(r => r.Level == LogLevel.Information).Message).Contains("Enterprise");
    }

    [Test]
    [Arguments(SubscriptionTier.Pro)]
    [Arguments(SubscriptionTier.Team)]
    public async Task HandleCheckoutCompletedAsync_EnterpriseTenant_DoesNotRestoreOrAudit(SubscriptionTier checkoutTier)
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        await SeedEnterpriseSubscriptionAsync(dbFactory);
        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner);

        await handler.HandleCheckoutCompletedAsync(1, checkoutTier, CancellationToken.None);

        TenantSubscription? row = await dbFactory.Context.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await provisioner.DidNotReceive().RestoreForTierAsync(
            Arg.Any<int>(), Arg.Any<TenantSubscription?>(), Arg.Any<SubscriptionTier>(), Arg.Any<SubscriptionStatus>(), Arg.Any<CancellationToken>());
        await Assert.That(await CountAuditRowsAsync(dbFactory, AuditAction.SubscriptionUpgraded)).IsEqualTo(0);
    }

    [Test]
    public async Task HandleTierCorrectionAsync_EnterpriseTenant_DoesNotRestoreFreezeOrAudit()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        await SeedEnterpriseSubscriptionAsync(dbFactory);
        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        IDowngradeCleanupService cleanup = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, cleanupService: cleanup, provisioner: provisioner);

        await handler.HandleTierCorrectionAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        TenantSubscription? row = await dbFactory.Context.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await provisioner.DidNotReceive().RestoreForTierAsync(
            Arg.Any<int>(), Arg.Any<TenantSubscription?>(), Arg.Any<SubscriptionTier>(), Arg.Any<SubscriptionStatus>(), Arg.Any<CancellationToken>());
        await cleanup.DidNotReceive().CleanupForProTierAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await Assert.That(await CountAuditRowsAsync(dbFactory, AuditAction.SubscriptionUpgraded)).IsEqualTo(0);
    }

    [Test]
    public async Task HandlePaymentFailedAsync_EnterpriseTenant_KeepsTheSubscriptionActive()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedEnterpriseSubscriptionAsync(dbFactory);
        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandlePaymentFailedAsync(1, CancellationToken.None);

        TenantSubscription? row = await dbFactory.Context.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await Assert.That(row.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task HandlePaymentSucceededAsync_EnterpriseTenant_DoesNotRestoreOrAudit()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedEnterpriseSubscriptionAsync(dbFactory);
        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        BillingWebhookHandler handler = CreateHandler(dbFactory, provisioner: provisioner);

        await handler.HandlePaymentSucceededAsync(1, CancellationToken.None);

        await provisioner.DidNotReceive().RestoreForTierAsync(
            Arg.Any<int>(), Arg.Any<TenantSubscription?>(), Arg.Any<SubscriptionTier>(), Arg.Any<SubscriptionStatus>(), Arg.Any<CancellationToken>());
        await Assert.That(await CountAuditRowsAsync(dbFactory, AuditAction.SubscriptionUpgraded)).IsEqualTo(0);
    }

    [Test]
    public async Task HandleSubscriptionUpdatedAsync_EnterpriseTenant_KeepsTheAgreementTermEnd()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedEnterpriseSubscriptionAsync(dbFactory);
        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleSubscriptionUpdatedAsync(1, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        TenantSubscription? row = await dbFactory.Context.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(row!.CurrentPeriodEnd.HasValue).IsTrue();
        TimeSpan difference = (row.CurrentPeriodEnd!.Value - EnterpriseTermEnd).Duration();
        await Assert.That(difference.TotalSeconds).IsLessThan(2);
    }
}
