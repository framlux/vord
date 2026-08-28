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

    [Test]
    public async Task EnableBuiltInsAsync_DelegatesToTheRepository()
    {
        IAlertRuleRepository repo = Substitute.For<IAlertRuleRepository>();
        repo.EnableBuiltInAlertRulesAsync(7, Arg.Any<CancellationToken>()).Returns(8);

        BuiltInAlertRuleProvisioner provisioner = Build(repo, Substitute.For<ISubscriptionService>());

        await provisioner.EnableBuiltInsAsync(7, CancellationToken.None);

        await repo.Received(1).EnableBuiltInAlertRulesAsync(7, Arg.Any<CancellationToken>());
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
