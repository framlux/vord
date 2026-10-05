// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="EnterpriseAgreementHandler"/>.
/// </summary>
public sealed class EnterpriseAgreementHandlerTests
{
    private const int TenantId = 1;

    private static readonly DateTimeOffset TermEnd = new(2027, 10, 4, 23, 59, 59, TimeSpan.Zero);

    private static EnterpriseAgreementTerms Terms(
        int revision,
        int machineLimit = 500,
        int retentionDays = 180,
        int memberLimit = int.MaxValue,
        int alertRuleLimit = 40,
        int webhookLimit = 20,
        DateTimeOffset? termEnd = null)
    {
        return new EnterpriseAgreementTerms(
            TenantId, 42, revision, machineLimit, retentionDays, memberLimit, alertRuleLimit, webhookLimit, termEnd ?? TermEnd);
    }

    /// <summary>
    /// The handler with the production graph: real repositories over an in-memory database, a
    /// substitute provisioner, and a real dispatcher over a substitute Hangfire client.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly TestDatabaseFactory _dbFactory = new();

        public Harness()
        {
            Repo = new DatabaseRepository(_dbFactory.Context, new NullLogger<DatabaseRepository>());
            Provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
            BackgroundJobs = Substitute.For<IBackgroundJobClient>();
            Handler = BuildHandler();
        }

        public DatabaseRepository Repo { get; }

        public IBuiltInAlertRuleProvisioner Provisioner { get; }

        public IBackgroundJobClient BackgroundJobs { get; }

        public EnterpriseAgreementHandler Handler { get; }

        /// <summary>
        /// Builds a handler over the same database whose transaction provider, subscription
        /// repository or audit log can be replaced, to observe or break one collaborator while the
        /// rest stay real.
        /// </summary>
        public EnterpriseAgreementHandler BuildHandler(
            IDatabaseTransactionProvider? transactionProvider = null,
            ISubscriptionRepository? subscriptionRepo = null,
            IAuditLogRepository? auditLog = null)
        {
            return new EnterpriseAgreementHandler(
                transactionProvider ?? Repo,
                subscriptionRepo ?? Repo,
                Repo,
                auditLog ?? Repo,
                Provisioner,
                new RetentionReclassifyDispatcher(BackgroundJobs, NullLogger<RetentionReclassifyDispatcher>.Instance),
                NullLogger<EnterpriseAgreementHandler>.Instance);
        }

        /// <summary>
        /// A subscription repository that reads and applies through the real one but records every
        /// call, so a test can see whether the cache was invalidated and in what order.
        /// </summary>
        public ISubscriptionRepository SpyOnSubscriptions()
        {
            ISubscriptionRepository spy = Substitute.For<ISubscriptionRepository>();
            spy.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call => Repo.GetSubscriptionForTenantAsync(call.ArgAt<int>(0), call.ArgAt<CancellationToken>(1)));
            spy.GetSubscriptionForUpdateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call => Repo.GetSubscriptionForUpdateAsync(call.ArgAt<int>(0), call.ArgAt<CancellationToken>(1)));
            spy.ApplyEnterpriseSubscriptionAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(call => Repo.ApplyEnterpriseSubscriptionAsync(
                    call.ArgAt<int>(0), call.ArgAt<int>(1), call.ArgAt<DateTimeOffset>(2), call.ArgAt<CancellationToken>(3)));

            return spy;
        }

        public async Task SeedSubscriptionAsync(SubscriptionTier tier)
        {
            TenantSubscription subscription = TestDataBuilder.BuildSubscription(tenantId: TenantId, tier: tier);
            subscription.Id = await _dbFactory.Context.InsertWithInt32IdentityAsync(subscription);
        }

        public async Task SeedOverrideAsync(int? machineLimit, int? memberLimit)
        {
            await _dbFactory.Context.InsertAsync(new TenantSubscriptionOverride
            {
                TenantId = TenantId,
                MachineLimit = machineLimit,
                MemberLimit = memberLimit,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        public async Task<int> CountAuditAsync(AuditAction action)
        {
            int count = await _dbFactory.Context.AuditLog
                .Where(a => (a.TenantId == TenantId) && (a.Action == action))
                .CountAsync();

            return count;
        }

        public void Dispose()
        {
            _dbFactory.Dispose();
        }
    }

    [Test]
    public async Task Apply_WritesTierEveryOverrideAndOneAuditRow()
    {
        using Harness h = new();

        EnterpriseApplyOutcome outcome = await h.Handler.ApplyAsync(Terms(revision: 1), CancellationToken.None);

        TenantSubscription? sub = await h.Repo.GetSubscriptionForTenantAsync(TenantId, CancellationToken.None);
        TenantSubscriptionOverride? ov = await h.Repo.GetOverrideForTenantAsync(TenantId, CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(EnterpriseApplyOutcome.Applied);
        await Assert.That(sub!.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await Assert.That(sub.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(sub.AppliedAgreementRevision).IsEqualTo(1);
        await Assert.That(sub.CurrentPeriodEnd).IsEqualTo(TermEnd);
        await Assert.That(ov!.MachineLimit).IsEqualTo(500);
        await Assert.That(ov.RetentionDays).IsEqualTo(180);
        await Assert.That(ov.MemberLimit).IsEqualTo(int.MaxValue);
        await Assert.That(ov.AlertRuleLimit).IsEqualTo(40);
        await Assert.That(ov.WebhookLimit).IsEqualTo(20);
        await Assert.That(await h.CountAuditAsync(AuditAction.EnterpriseAgreementApplied)).IsEqualTo(1);
    }

    [Test]
    public async Task Apply_ReplacesAnExistingOverrideWholesale()
    {
        using Harness h = new();
        await h.SeedSubscriptionAsync(SubscriptionTier.Team);
        await h.SeedOverrideAsync(machineLimit: 50, memberLimit: null);

        await h.Handler.ApplyAsync(Terms(revision: 1, memberLimit: 75), CancellationToken.None);

        TenantSubscriptionOverride? ov = await h.Repo.GetOverrideForTenantAsync(TenantId, CancellationToken.None);
        await Assert.That(ov!.MachineLimit).IsEqualTo(500);
        await Assert.That(ov.MemberLimit).IsEqualTo(75);
    }

    [Test]
    public async Task Apply_FromFree_RestoresTierGatedResources()
    {
        using Harness h = new();
        await h.SeedSubscriptionAsync(SubscriptionTier.Free);

        await h.Handler.ApplyAsync(Terms(revision: 1), CancellationToken.None);

        await h.Provisioner.Received(1).RestoreForTierAsync(
            TenantId,
            Arg.Is<TenantSubscription?>(p => (p != null) && (p.Tier == SubscriptionTier.Free)),
            SubscriptionTier.Enterprise,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_Applied_EnqueuesOneRetentionReclassifyForTheTenant()
    {
        using Harness h = new();

        await h.Handler.ApplyAsync(Terms(revision: 1), CancellationToken.None);

        h.BackgroundJobs.Received(1).Create(
            Arg.Is<Job>(j => (j.Method.Name == nameof(RetentionReclassifyJob.RunAsync))
                && ((int)j.Args[0] == TenantId)),
            Arg.Any<IState>());
    }

    [Test]
    public async Task Apply_SameRevisionTwice_WritesNothingTheSecondTime()
    {
        using Harness h = new();
        await h.Handler.ApplyAsync(Terms(revision: 1), CancellationToken.None);
        h.BackgroundJobs.ClearReceivedCalls();
        h.Provisioner.ClearReceivedCalls();

        EnterpriseApplyOutcome second = await h.Handler.ApplyAsync(Terms(revision: 1, machineLimit: 9), CancellationToken.None);

        TenantSubscriptionOverride? ov = await h.Repo.GetOverrideForTenantAsync(TenantId, CancellationToken.None);
        await Assert.That(second).IsEqualTo(EnterpriseApplyOutcome.AlreadyApplied);
        await Assert.That(ov!.MachineLimit).IsEqualTo(500);
        await Assert.That(await h.CountAuditAsync(AuditAction.EnterpriseAgreementApplied)).IsEqualTo(1);
        h.BackgroundJobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
        await h.Provisioner.DidNotReceive().RestoreForTierAsync(
            Arg.Any<int>(),
            Arg.Any<TenantSubscription?>(),
            Arg.Any<SubscriptionTier>(),
            Arg.Any<SubscriptionStatus>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_OlderRevision_LeavesNewerLimitsInPlace()
    {
        using Harness h = new();
        await h.Handler.ApplyAsync(Terms(revision: 2, machineLimit: 800), CancellationToken.None);

        EnterpriseApplyOutcome outcome = await h.Handler.ApplyAsync(Terms(revision: 1, machineLimit: 100), CancellationToken.None);

        TenantSubscriptionOverride? ov = await h.Repo.GetOverrideForTenantAsync(TenantId, CancellationToken.None);
        TenantSubscription? sub = await h.Repo.GetSubscriptionForTenantAsync(TenantId, CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(EnterpriseApplyOutcome.StaleRevision);
        await Assert.That(ov!.MachineLimit).IsEqualTo(800);
        await Assert.That(sub!.AppliedAgreementRevision).IsEqualTo(2);
        await Assert.That(await h.CountAuditAsync(AuditAction.EnterpriseAgreementApplied)).IsEqualTo(1);
    }

    [Test]
    public async Task Apply_NewerRevision_ReplacesTheLimits()
    {
        using Harness h = new();
        await h.Handler.ApplyAsync(Terms(revision: 1, machineLimit: 500), CancellationToken.None);

        EnterpriseApplyOutcome outcome = await h.Handler.ApplyAsync(Terms(revision: 2, machineLimit: 800), CancellationToken.None);

        TenantSubscriptionOverride? ov = await h.Repo.GetOverrideForTenantAsync(TenantId, CancellationToken.None);
        TenantSubscription? sub = await h.Repo.GetSubscriptionForTenantAsync(TenantId, CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(EnterpriseApplyOutcome.Applied);
        await Assert.That(ov!.MachineLimit).IsEqualTo(800);
        await Assert.That(sub!.AppliedAgreementRevision).IsEqualTo(2);
        await Assert.That(await h.CountAuditAsync(AuditAction.EnterpriseAgreementApplied)).IsEqualTo(2);
    }

    [Test]
    [Arguments(0, 180, 10, 10, 10)]      // machines must be positive
    [Arguments(500, 0, 10, 10, 10)]      // retention 1..365
    [Arguments(500, 366, 10, 10, 10)]
    [Arguments(500, 180, 0, 10, 10)]     // members must be positive
    [Arguments(500, 180, 10, -1, 10)]    // -1 is "tier default", never valid for Enterprise
    [Arguments(500, 180, 10, 10, -1)]
    public async Task Apply_InvalidLimits_AreRefusedBeforeAnyWrite(int machines, int retention, int members, int alerts, int webhooks)
    {
        using Harness h = new();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Handler.ApplyAsync(
            Terms(revision: 1, machineLimit: machines, retentionDays: retention, memberLimit: members, alertRuleLimit: alerts, webhookLimit: webhooks),
            CancellationToken.None));

        await Assert.That(await h.Repo.GetSubscriptionForTenantAsync(TenantId, CancellationToken.None)).IsNull();
        await Assert.That(await h.Repo.GetOverrideForTenantAsync(TenantId, CancellationToken.None)).IsNull();
    }

    /// <summary>
    /// An unset protobuf timestamp arrives as the Unix epoch, and the agreement would then be recorded
    /// as having ended in 1970, so every date before the floor is refused before anything is written.
    /// </summary>
    [Test]
    [Arguments("1970-01-01T00:00:00+00:00")]
    [Arguments("0001-01-01T00:00:00+00:00")]
    [Arguments("1999-12-31T23:59:59+00:00")]
    public async Task Apply_TermEndBeforeTheFloor_IsRefusedBeforeAnyWrite(string termEnd)
    {
        using Harness h = new();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Handler.ApplyAsync(
            Terms(revision: 1, termEnd: DateTimeOffset.Parse(termEnd, System.Globalization.CultureInfo.InvariantCulture)),
            CancellationToken.None));

        await Assert.That(await h.Repo.GetSubscriptionForTenantAsync(TenantId, CancellationToken.None)).IsNull();
        await Assert.That(await h.Repo.GetOverrideForTenantAsync(TenantId, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task Apply_TermEndExactlyAtTheFloor_IsAccepted()
    {
        using Harness h = new();

        EnterpriseApplyOutcome outcome = await h.Handler.ApplyAsync(
            Terms(revision: 1, termEnd: EnterpriseAgreementHandler.EarliestTermEnd), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(EnterpriseApplyOutcome.Applied);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Apply_NonPositiveRevision_IsRefusedBeforeAnyWrite(int revision)
    {
        using Harness h = new();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Handler.ApplyAsync(Terms(revision: revision), CancellationToken.None));

        await Assert.That(await h.Repo.GetSubscriptionForTenantAsync(TenantId, CancellationToken.None)).IsNull();
    }

    [Test]
    [Arguments(1, 1, 0, 0)]
    [Arguments(1, 365, 1, 1)]
    public async Task Apply_LimitsAtTheirBoundaries_AreAccepted(int machines, int retention, int alerts, int webhooks)
    {
        using Harness h = new();

        EnterpriseApplyOutcome outcome = await h.Handler.ApplyAsync(
            Terms(revision: 1, machineLimit: machines, retentionDays: retention, memberLimit: 1, alertRuleLimit: alerts, webhookLimit: webhooks),
            CancellationToken.None);

        TenantSubscriptionOverride? ov = await h.Repo.GetOverrideForTenantAsync(TenantId, CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(EnterpriseApplyOutcome.Applied);
        await Assert.That(ov!.MachineLimit).IsEqualTo(machines);
        await Assert.That(ov.RetentionDays).IsEqualTo(retention);
        await Assert.That(ov.MemberLimit).IsEqualTo(1);
        await Assert.That(ov.AlertRuleLimit).IsEqualTo(alerts);
        await Assert.That(ov.WebhookLimit).IsEqualTo(webhooks);
    }

    /// <summary>
    /// The tier, the override and the audit row are one unit: a failure part-way through must leave
    /// the tenant exactly as it was. Were the writes outside the transaction, the subscription and
    /// override rows would survive the failed audit insert and the tenant would be Enterprise.
    /// </summary>
    [Test]
    public async Task Apply_WhenTheAuditInsertFails_RollsBackTheTierAndTheOverride()
    {
        using Harness h = new();
        await h.SeedSubscriptionAsync(SubscriptionTier.Team);
        IAuditLogRepository failingAudit = Substitute.For<IAuditLogRepository>();
        failingAudit.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("audit store unavailable")));
        EnterpriseAgreementHandler handler = h.BuildHandler(auditLog: failingAudit);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.ApplyAsync(Terms(revision: 1), CancellationToken.None));

        TenantSubscription? sub = await h.Repo.GetSubscriptionForTenantAsync(TenantId, CancellationToken.None);
        TenantSubscriptionOverride? ov = await h.Repo.GetOverrideForTenantAsync(TenantId, CancellationToken.None);
        await Assert.That(sub!.Tier).IsEqualTo(SubscriptionTier.Team);
        await Assert.That(sub.AppliedAgreementRevision).IsNull();
        await Assert.That(ov).IsNull();
    }

    [Test]
    public async Task Apply_WhenTheAuditInsertFails_EnqueuesNothingAndLeavesNoRow()
    {
        using Harness h = new();
        IAuditLogRepository failingAudit = Substitute.For<IAuditLogRepository>();
        failingAudit.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("audit store unavailable")));
        EnterpriseAgreementHandler handler = h.BuildHandler(auditLog: failingAudit);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.ApplyAsync(Terms(revision: 1), CancellationToken.None));

        await Assert.That(await h.Repo.GetSubscriptionForTenantAsync(TenantId, CancellationToken.None)).IsNull();
        h.BackgroundJobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    /// <summary>
    /// The restore is what billing-api's retry would never reach if it ran after the commit: the retry
    /// finds the revision already applied and returns. Inside the transaction a failed restore takes
    /// the tier write with it, so the retry applies the revision afresh and restores.
    /// </summary>
    [Test]
    public async Task Apply_WhenTheRestoreFails_RollsEverythingBackAndARetryRestores()
    {
        using Harness h = new();
        await h.SeedSubscriptionAsync(SubscriptionTier.Free);
        h.Provisioner.RestoreForTierAsync(
                Arg.Any<int>(), Arg.Any<TenantSubscription?>(), Arg.Any<SubscriptionTier>(), Arg.Any<SubscriptionStatus>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("rule store unavailable")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Handler.ApplyAsync(Terms(revision: 1), CancellationToken.None));

        TenantSubscription? afterFailure = await h.Repo.GetSubscriptionForTenantAsync(TenantId, CancellationToken.None);
        await Assert.That(afterFailure!.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(afterFailure.AppliedAgreementRevision).IsNull();
        await Assert.That(await h.Repo.GetOverrideForTenantAsync(TenantId, CancellationToken.None)).IsNull();
        await Assert.That(await h.CountAuditAsync(AuditAction.EnterpriseAgreementApplied)).IsEqualTo(0);

        h.Provisioner.ClearSubstitute(ClearOptions.All);
        EnterpriseApplyOutcome retry = await h.Handler.ApplyAsync(Terms(revision: 1), CancellationToken.None);

        await Assert.That(retry).IsEqualTo(EnterpriseApplyOutcome.Applied);
        await h.Provisioner.Received(1).RestoreForTierAsync(
            TenantId,
            Arg.Is<TenantSubscription?>(p => (p != null) && (p.Tier == SubscriptionTier.Free)),
            SubscriptionTier.Enterprise,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_WhenTheRestoreFails_StillInvalidatesTheSubscriptionCache()
    {
        using Harness h = new();
        h.Provisioner.RestoreForTierAsync(
                Arg.Any<int>(), Arg.Any<TenantSubscription?>(), Arg.Any<SubscriptionTier>(), Arg.Any<SubscriptionStatus>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("rule store unavailable")));
        ISubscriptionRepository subscriptions = h.SpyOnSubscriptions();
        EnterpriseAgreementHandler handler = h.BuildHandler(subscriptionRepo: subscriptions);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.ApplyAsync(Terms(revision: 1), CancellationToken.None));

        await subscriptions.Received(1).InvalidateSubscriptionCacheAsync(TenantId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_Applied_RestoresInsideTheTransactionAndWritesTheAuditRowLast()
    {
        using Harness h = new();
        IDatabaseTransaction transaction = Substitute.For<IDatabaseTransaction>();
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(transaction));
        IAuditLogRepository audit = Substitute.For<IAuditLogRepository>();
        EnterpriseAgreementHandler handler = h.BuildHandler(transactionProvider, auditLog: audit);

        await handler.ApplyAsync(Terms(revision: 1), CancellationToken.None);

        Received.InOrder(() =>
        {
            transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>());
            h.Provisioner.RestoreForTierAsync(
                TenantId, Arg.Any<TenantSubscription?>(), SubscriptionTier.Enterprise, SubscriptionStatus.Active, Arg.Any<CancellationToken>());
            audit.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
            transaction.CommitAsync(Arg.Any<CancellationToken>());
        });
    }

    /// <summary>
    /// What the restore may do depends on the tier the tenant held a moment ago. A cached read can
    /// still say Team after a deletion committed Free, so the row is locked and read from the database
    /// inside the transaction, ahead of the write that destroys it.
    /// </summary>
    [Test]
    public async Task Apply_ReadsThePriorRowLockedAndUncachedInsideTheTransaction()
    {
        using Harness h = new();
        await h.SeedSubscriptionAsync(SubscriptionTier.Free);
        ISubscriptionRepository subscriptions = h.SpyOnSubscriptions();
        TenantSubscription staleCachedTeam = TestDataBuilder.BuildSubscription(tenantId: TenantId, tier: SubscriptionTier.Team);
        subscriptions.GetSubscriptionForTenantAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantSubscription?>(staleCachedTeam));
        IDatabaseTransaction transaction = Substitute.For<IDatabaseTransaction>();
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(transaction));
        EnterpriseAgreementHandler handler = h.BuildHandler(transactionProvider, subscriptions);

        await handler.ApplyAsync(Terms(revision: 1), CancellationToken.None);

        await h.Provisioner.Received(1).RestoreForTierAsync(
            TenantId,
            Arg.Is<TenantSubscription?>(p => (p != null) && (p.Tier == SubscriptionTier.Free)),
            SubscriptionTier.Enterprise,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
        await subscriptions.DidNotReceive().GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>());
            subscriptions.GetSubscriptionForUpdateAsync(TenantId, Arg.Any<CancellationToken>());
            subscriptions.ApplyEnterpriseSubscriptionAsync(
                TenantId, 1, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task Apply_NoSubscriptionRow_HandsTheProvisionerNoPriorRow()
    {
        using Harness h = new();

        await h.Handler.ApplyAsync(Terms(revision: 1), CancellationToken.None);

        await h.Provisioner.Received(1).RestoreForTierAsync(
            TenantId,
            null,
            SubscriptionTier.Enterprise,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The override write bypasses the caching repository's mutators, so the cached entry (which also
    /// holds effective retention) is only evicted by the handler's own call. That call has to follow
    /// the commit: evicting earlier lets a concurrent reader re-cache the pre-apply state.
    /// </summary>
    [Test]
    public async Task Apply_Applied_InvalidatesTheSubscriptionCacheAfterTheCommit()
    {
        using Harness h = new();
        ISubscriptionRepository subscriptions = h.SpyOnSubscriptions();
        IDatabaseTransaction transaction = Substitute.For<IDatabaseTransaction>();
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(transaction));
        EnterpriseAgreementHandler handler = h.BuildHandler(transactionProvider, subscriptions);

        EnterpriseApplyOutcome outcome = await handler.ApplyAsync(Terms(revision: 1), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(EnterpriseApplyOutcome.Applied);
        await subscriptions.Received(1).InvalidateSubscriptionCacheAsync(TenantId, Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            transaction.CommitAsync(Arg.Any<CancellationToken>());
            subscriptions.InvalidateSubscriptionCacheAsync(TenantId, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    [Arguments(2, EnterpriseApplyOutcome.AlreadyApplied)]
    [Arguments(1, EnterpriseApplyOutcome.StaleRevision)]
    public async Task Apply_NotApplied_LeavesTheSubscriptionCacheAlone(int revision, EnterpriseApplyOutcome expected)
    {
        using Harness h = new();
        await h.Handler.ApplyAsync(Terms(revision: 2), CancellationToken.None);
        ISubscriptionRepository subscriptions = h.SpyOnSubscriptions();
        EnterpriseAgreementHandler handler = h.BuildHandler(subscriptionRepo: subscriptions);

        EnterpriseApplyOutcome outcome = await handler.ApplyAsync(Terms(revision: revision), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(expected);
        await subscriptions.DidNotReceive().InvalidateSubscriptionCacheAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_NullTerms_Throws()
    {
        using Harness h = new();

        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Handler.ApplyAsync(null!, CancellationToken.None));
    }
}
