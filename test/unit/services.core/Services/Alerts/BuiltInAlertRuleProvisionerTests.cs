// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Alerts;

public sealed class BuiltInAlertRuleProvisionerTests
{
    private static readonly DateTimeOffset Now = new(2026, 08, 28, 12, 00, 00, TimeSpan.Zero);

    [Test]
    public async Task EnsureProvisionedAsync_FreeTenant_SeedsEveryDefinitionDisabled()
    {
        IAlertRuleRepository repo = Substitute.For<IAlertRuleRepository>();
        repo.GetBuiltInMetricsForTenantAsync(7, Arg.Any<CancellationToken>()).Returns([]);
        ISubscriptionService subscriptions = SubscriptionsReturning(7, SubscriptionTier.Free);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, subscriptions);

        await provisioner.EnsureProvisionedAsync(7, CancellationToken.None);

        List<AlertRule> inserted = CapturedRules(repo);

        await Assert.That(inserted.Count).IsEqualTo(BuiltInAlertRuleDefinitions.All.Count);
        await Assert.That(inserted.Count).IsEqualTo(8);
        await Assert.That(inserted.TrueForAll(r => r.IsEnabled == false)).IsTrue();
        await Assert.That(inserted.TrueForAll(r => r.IsCustom == false)).IsTrue();
        await Assert.That(inserted.TrueForAll(r => r.TenantId == 7)).IsTrue();
        await Assert.That(inserted.TrueForAll(r => r.CreatedByUserId == BuiltInAlertRuleDefinitions.SystemUserId)).IsTrue();
        await Assert.That(inserted.TrueForAll(r => r.NotifyEmail)).IsTrue();
        await Assert.That(inserted.TrueForAll(r => r.NotifyWebhook == false)).IsTrue();
    }

    [Test]
    public async Task EnsureProvisionedAsync_ProTenant_SeedsEveryDefinitionEnabled()
    {
        IAlertRuleRepository repo = Substitute.For<IAlertRuleRepository>();
        repo.GetBuiltInMetricsForTenantAsync(7, Arg.Any<CancellationToken>()).Returns([]);
        ISubscriptionService subscriptions = SubscriptionsReturning(7, SubscriptionTier.Pro);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, subscriptions);

        await provisioner.EnsureProvisionedAsync(7, CancellationToken.None);

        List<AlertRule> inserted = CapturedRules(repo);

        await Assert.That(inserted.Count).IsEqualTo(8);
        await Assert.That(inserted.TrueForAll(r => r.IsEnabled)).IsTrue();
    }

    [Test]
    public async Task EnsureProvisionedAsync_NoSubscriptionRow_SeedsDisabled()
    {
        IAlertRuleRepository repo = Substitute.For<IAlertRuleRepository>();
        repo.GetBuiltInMetricsForTenantAsync(7, Arg.Any<CancellationToken>()).Returns([]);
        ISubscriptionService subscriptions = Substitute.For<ISubscriptionService>();
        subscriptions.GetSubscriptionForTenantAsync(7, Arg.Any<CancellationToken>()).Returns((TenantSubscription?)null);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, subscriptions);

        await provisioner.EnsureProvisionedAsync(7, CancellationToken.None);

        List<AlertRule> inserted = CapturedRules(repo);

        await Assert.That(inserted.Count).IsEqualTo(8);
        await Assert.That(inserted.TrueForAll(r => r.IsEnabled == false)).IsTrue();
    }

    [Test]
    public async Task EnsureProvisionedAsync_SelfHostedDeployment_SeedsEnabled()
    {
        // The stored row is Free forever in a self-hosted deployment and no billing event ever
        // arrives, so reading the row instead of asking the service would seed everything off
        // permanently. This is the case that decides where entitlement is read from.
        IAlertRuleRepository repo = Substitute.For<IAlertRuleRepository>();
        repo.GetBuiltInMetricsForTenantAsync(7, Arg.Any<CancellationToken>()).Returns([]);

        ISubscriptionService inner = SubscriptionsReturning(7, SubscriptionTier.Free);
        SelfHostedSubscriptionService selfHosted = new(
            inner, Substitute.For<ITenantRepository>(), new FakeTimeProvider(Now));

        BuiltInAlertRuleProvisioner provisioner = Build(repo, selfHosted);

        await provisioner.EnsureProvisionedAsync(7, CancellationToken.None);

        List<AlertRule> inserted = CapturedRules(repo);

        await Assert.That(inserted.Count).IsEqualTo(8);
        await Assert.That(inserted.TrueForAll(r => r.IsEnabled)).IsTrue();
    }

    [Test]
    public async Task EnsureProvisionedAsync_SomeMetricsPresent_SeedsOnlyTheMissingOnes()
    {
        IAlertRuleRepository repo = Substitute.For<IAlertRuleRepository>();
        repo.GetBuiltInMetricsForTenantAsync(7, Arg.Any<CancellationToken>())
            .Returns([AlertMetric.CpuUsage, AlertMetric.DiskHealth]);
        ISubscriptionService subscriptions = SubscriptionsReturning(7, SubscriptionTier.Pro);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, subscriptions);

        await provisioner.EnsureProvisionedAsync(7, CancellationToken.None);

        List<AlertRule> inserted = CapturedRules(repo);

        await Assert.That(inserted.Count).IsEqualTo(6);
        await Assert.That(inserted.Exists(r => r.Metric == AlertMetric.CpuUsage)).IsFalse();
        await Assert.That(inserted.Exists(r => r.Metric == AlertMetric.DiskHealth)).IsFalse();
    }

    [Test]
    public async Task EnsureProvisionedAsync_EveryMetricPresent_InsertsNothing()
    {
        IAlertRuleRepository repo = Substitute.For<IAlertRuleRepository>();
        repo.GetBuiltInMetricsForTenantAsync(7, Arg.Any<CancellationToken>())
            .Returns(Enum.GetValues<AlertMetric>().ToList());
        ISubscriptionService subscriptions = SubscriptionsReturning(7, SubscriptionTier.Pro);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, subscriptions);

        await provisioner.EnsureProvisionedAsync(7, CancellationToken.None);

        await repo.DidNotReceive().InsertAlertRulesAsync(Arg.Any<IEnumerable<AlertRule>>(), Arg.Any<CancellationToken>());
        await subscriptions.DidNotReceive().GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureProvisionedAsync_StampsTimestampsFromTheInjectedClock()
    {
        IAlertRuleRepository repo = Substitute.For<IAlertRuleRepository>();
        repo.GetBuiltInMetricsForTenantAsync(7, Arg.Any<CancellationToken>()).Returns([]);
        ISubscriptionService subscriptions = SubscriptionsReturning(7, SubscriptionTier.Pro);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, subscriptions);

        await provisioner.EnsureProvisionedAsync(7, CancellationToken.None);

        List<AlertRule> inserted = CapturedRules(repo);

        await Assert.That(inserted.TrueForAll(r => r.CreatedAt == Now)).IsTrue();
        await Assert.That(inserted.TrueForAll(r => r.UpdatedAt == Now)).IsTrue();
    }

    /// <summary>
    /// The first checkout: the tenant sat on Free, the sweep left every built-in off, and paying is
    /// what turns them on.
    /// </summary>
    [Test]
    public async Task RestoreForTierAsync_FreeToPro_EnablesBuiltIns()
    {
        IAlertRuleRepository repo = FullyProvisionedRepo(7);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, Substitute.For<ISubscriptionService>());

        await provisioner.RestoreForTierAsync(
            7,
            Subscription(SubscriptionTier.Free, SubscriptionStatus.Active),
            SubscriptionTier.Pro,
            SubscriptionStatus.Active,
            CancellationToken.None);

        await repo.Received(1).EnableBuiltInAlertRulesAsync(7, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Cancellation runs the same sweep a Free downgrade does, so reactivation has to undo it.
    /// </summary>
    [Test]
    public async Task RestoreForTierAsync_CanceledToActive_EnablesBuiltIns()
    {
        IAlertRuleRepository repo = FullyProvisionedRepo(7);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, Substitute.For<ISubscriptionService>());

        await provisioner.RestoreForTierAsync(
            7,
            Subscription(SubscriptionTier.Pro, SubscriptionStatus.Canceled),
            SubscriptionTier.Pro,
            SubscriptionStatus.Active,
            CancellationToken.None);

        await repo.Received(1).EnableBuiltInAlertRulesAsync(7, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A declined card writes PastDue and disables nothing, so the retry that succeeds has nothing to
    /// restore. Enabling here would revive every built-in the tenant had silenced, and would do it
    /// again on every dunning cycle.
    /// </summary>
    [Test]
    public async Task RestoreForTierAsync_PastDueToActive_LeavesBuiltInsAlone()
    {
        IAlertRuleRepository repo = FullyProvisionedRepo(7);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, Substitute.For<ISubscriptionService>());

        await provisioner.RestoreForTierAsync(
            7,
            Subscription(SubscriptionTier.Pro, SubscriptionStatus.PastDue),
            SubscriptionTier.Pro,
            SubscriptionStatus.Active,
            CancellationToken.None);

        await repo.DidNotReceive().EnableBuiltInAlertRulesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An upgrade between two paid tiers passes through no sweep, so the built-ins the tenant chose
    /// to silence stay silent. The custom rules a Pro downgrade froze are a different matter: they
    /// thaw on arrival at Team.
    /// </summary>
    [Test]
    public async Task RestoreForTierAsync_ProToTeam_ThawsCustomRulesWithoutRevivingBuiltIns()
    {
        IAlertRuleRepository repo = FullyProvisionedRepo(7);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, Substitute.For<ISubscriptionService>());

        await provisioner.RestoreForTierAsync(
            7,
            Subscription(SubscriptionTier.Pro, SubscriptionStatus.Active),
            SubscriptionTier.Team,
            SubscriptionStatus.Active,
            CancellationToken.None);

        await repo.DidNotReceive().EnableBuiltInAlertRulesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await repo.Received(1).EnableCustomAlertRulesAsync(7, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A monthly renewal arrives on the same path a recovery does and is not a transition at all.
    /// </summary>
    [Test]
    public async Task RestoreForTierAsync_ActiveRenewal_ChangesNothing()
    {
        IAlertRuleRepository repo = FullyProvisionedRepo(7);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, Substitute.For<ISubscriptionService>());

        await provisioner.RestoreForTierAsync(
            7,
            Subscription(SubscriptionTier.Team, SubscriptionStatus.Active),
            SubscriptionTier.Team,
            SubscriptionStatus.Active,
            CancellationToken.None);

        await repo.DidNotReceive().EnableBuiltInAlertRulesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().EnableCustomAlertRulesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A canceled Team tenant lost its custom rules to the sweep as well, so restoring only the
    /// built-ins would leave it paying for Team and running Pro's rule set.
    /// </summary>
    [Test]
    public async Task RestoreForTierAsync_CanceledTeamToActiveTeam_RestoresBoth()
    {
        IAlertRuleRepository repo = FullyProvisionedRepo(7);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, Substitute.For<ISubscriptionService>());

        await provisioner.RestoreForTierAsync(
            7,
            Subscription(SubscriptionTier.Team, SubscriptionStatus.Canceled),
            SubscriptionTier.Team,
            SubscriptionStatus.Active,
            CancellationToken.None);

        await repo.Received(1).EnableBuiltInAlertRulesAsync(7, Arg.Any<CancellationToken>());
        await repo.Received(1).EnableCustomAlertRulesAsync(7, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A tenant with no subscription row has never been through anything the sweep spared, and the
    /// caller has just written it into a paid tier, so this is a first entitlement.
    /// </summary>
    [Test]
    public async Task RestoreForTierAsync_NoPriorSubscription_EnablesBuiltIns()
    {
        IAlertRuleRepository repo = FullyProvisionedRepo(7);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, Substitute.For<ISubscriptionService>());

        await provisioner.RestoreForTierAsync(
            7, null, SubscriptionTier.Pro, SubscriptionStatus.Active, CancellationToken.None);

        await repo.Received(1).EnableBuiltInAlertRulesAsync(7, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Free is not an entitlement, so nothing is seeded and nothing is enabled.
    /// </summary>
    [Test]
    public async Task RestoreForTierAsync_ToFree_DoesNothing()
    {
        IAlertRuleRepository repo = FullyProvisionedRepo(7);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, Substitute.For<ISubscriptionService>());

        await provisioner.RestoreForTierAsync(
            7,
            Subscription(SubscriptionTier.Team, SubscriptionStatus.Active),
            SubscriptionTier.Free,
            SubscriptionStatus.Active,
            CancellationToken.None);

        await repo.DidNotReceive().GetBuiltInMetricsForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().EnableBuiltInAlertRulesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().EnableCustomAlertRulesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A paid tier that is not Active is refused every gated feature, so landing on one restores
    /// nothing. The administrative grant RPC accepts exactly this combination.
    /// </summary>
    [Test]
    public async Task RestoreForTierAsync_PaidTierButInactiveStatus_DoesNothing()
    {
        IAlertRuleRepository repo = FullyProvisionedRepo(7);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, Substitute.For<ISubscriptionService>());

        await provisioner.RestoreForTierAsync(
            7,
            Subscription(SubscriptionTier.Free, SubscriptionStatus.Active),
            SubscriptionTier.Pro,
            SubscriptionStatus.Canceled,
            CancellationToken.None);

        await repo.DidNotReceive().GetBuiltInMetricsForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().EnableBuiltInAlertRulesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().EnableCustomAlertRulesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Seeding is a backstop on every entitled transition, including the ones that restore nothing —
    /// a tenant that reached a paid tier without rules needs them whatever the prior state was.
    /// </summary>
    [Test]
    public async Task RestoreForTierAsync_EntitledWithoutRestore_StillSeedsMissingRules()
    {
        IAlertRuleRepository repo = Substitute.For<IAlertRuleRepository>();
        repo.GetBuiltInMetricsForTenantAsync(7, Arg.Any<CancellationToken>()).Returns([]);
        ISubscriptionService subscriptions = SubscriptionsReturning(7, SubscriptionTier.Pro);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, subscriptions);

        await provisioner.RestoreForTierAsync(
            7,
            Subscription(SubscriptionTier.Pro, SubscriptionStatus.PastDue),
            SubscriptionTier.Pro,
            SubscriptionStatus.Active,
            CancellationToken.None);

        await Assert.That(CapturedRules(repo).Count).IsEqualTo(8);
        await repo.DidNotReceive().EnableBuiltInAlertRulesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Definitions_CoverEveryAlertMetric()
    {
        AlertMetric[] metrics = Enum.GetValues<AlertMetric>();

        await Assert.That(BuiltInAlertRuleDefinitions.All.Count).IsEqualTo(metrics.Length);

        foreach (AlertMetric metric in metrics)
        {
            await Assert.That(BuiltInAlertRuleDefinitions.All.Count(d => d.Metric == metric)).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Definitions_RespectTheMinimumDurationForTheirMetric()
    {
        foreach (BuiltInAlertRuleDefinition definition in BuiltInAlertRuleDefinitions.All)
        {
            await Assert.That(definition.DurationMinutes)
                .IsGreaterThanOrEqualTo(AlertConstants.GetMinimumDurationMinutes(definition.Metric));
            await Assert.That(definition.DurationMinutes)
                .IsLessThanOrEqualTo(AlertConstants.MaxRuleDurationMinutes);
        }
    }

    private static BuiltInAlertRuleProvisioner Build(IAlertRuleRepository repo, ISubscriptionService subscriptions)
    {
        return new BuiltInAlertRuleProvisioner(
            repo,
            subscriptions,
            new FakeTimeProvider(Now),
            new NullLogger<BuiltInAlertRuleProvisioner>());
    }

    /// <summary>
    /// A repository whose tenant already holds every built-in metric, so seeding is a no-op and the
    /// restore decision is the only thing under test.
    /// </summary>
    private static IAlertRuleRepository FullyProvisionedRepo(int tenantId)
    {
        IAlertRuleRepository repo = Substitute.For<IAlertRuleRepository>();
        repo.GetBuiltInMetricsForTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns([.. BuiltInAlertRuleDefinitions.All.Select(d => d.Metric)]);

        return repo;
    }

    private static TenantSubscription Subscription(SubscriptionTier tier, SubscriptionStatus status)
    {
        return new TenantSubscription
        {
            TenantId = 7,
            Tier = tier,
            Status = status,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
    }

    private static ISubscriptionService SubscriptionsReturning(int tenantId, SubscriptionTier tier)
    {
        ISubscriptionService subscriptions = Substitute.For<ISubscriptionService>();
        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        subscriptions.GetSubscriptionForTenantAsync(tenantId, Arg.Any<CancellationToken>()).Returns(subscription);

        return subscriptions;
    }

    private static List<AlertRule> CapturedRules(IAlertRuleRepository repo)
    {
        IEnumerable<AlertRule> captured = (IEnumerable<AlertRule>)repo.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IAlertRuleRepository.InsertAlertRulesAsync))
            .GetArguments()[0]!;

        return [.. captured];
    }
}
