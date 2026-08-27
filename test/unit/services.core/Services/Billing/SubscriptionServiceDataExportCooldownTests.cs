// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for the per-tier data export cooldown. The window is the gap a tenant must wait between
/// generating exports; it says nothing about downloading an export that already exists.
/// </summary>
public sealed class SubscriptionServiceDataExportCooldownTests
{
    /// <summary>
    /// The free tier waits a full day between exports.
    /// </summary>
    [Test]
    public async Task GetDataExportCooldown_FreeTier_Is24Hours()
    {
        SubscriptionService service = BuildService(SubscriptionTier.Free);

        TimeSpan cooldown = await service.GetDataExportCooldownAsync(1, CancellationToken.None);

        await Assert.That(cooldown).IsEqualTo(TimeSpan.FromHours(24));
    }

    /// <summary>
    /// The pro tier waits half a day.
    /// </summary>
    [Test]
    public async Task GetDataExportCooldown_ProTier_Is12Hours()
    {
        SubscriptionService service = BuildService(SubscriptionTier.Pro);

        TimeSpan cooldown = await service.GetDataExportCooldownAsync(1, CancellationToken.None);

        await Assert.That(cooldown).IsEqualTo(TimeSpan.FromHours(12));
    }

    /// <summary>
    /// The team tier waits the shortest window of the three.
    /// </summary>
    [Test]
    public async Task GetDataExportCooldown_TeamTier_Is8Hours()
    {
        SubscriptionService service = BuildService(SubscriptionTier.Team);

        TimeSpan cooldown = await service.GetDataExportCooldownAsync(1, CancellationToken.None);

        await Assert.That(cooldown).IsEqualTo(TimeSpan.FromHours(8));
    }

    /// <summary>
    /// A tenant with no subscription row falls back to the free window rather than to no window,
    /// matching how every other effective limit resolves a missing subscription.
    /// </summary>
    [Test]
    public async Task GetDataExportCooldown_NoSubscription_FallsBackToFreeWindow()
    {
        SubscriptionService service = BuildService(tier: null);

        TimeSpan cooldown = await service.GetDataExportCooldownAsync(1, CancellationToken.None);

        await Assert.That(cooldown).IsEqualTo(TimeSpan.FromHours(24));
    }

    private static SubscriptionService BuildService(SubscriptionTier? tier)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tier is null
                ? null
                : new TenantSubscription
                {
                    TenantId = 1,
                    Tier = tier.Value,
                    Status = SubscriptionStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now,
                }));

        IOptions<TierDefaultOptions> tierDefaults = Options.Create(new TierDefaultOptions
        {
            Free = new() { MachineLimit = 3, RetentionDays = 1, AlertRuleLimit = 0, WebhookLimit = 0, MemberLimit = 1, DataExportCooldownHours = 24 },
            Pro = new() { MachineLimit = 1000, RetentionDays = 60, AlertRuleLimit = 10, WebhookLimit = 5, MemberLimit = 5, DataExportCooldownHours = 12 },
            Team = new() { MachineLimit = 10000, RetentionDays = 365, AlertRuleLimit = 25, WebhookLimit = 15, MemberLimit = int.MaxValue, DataExportCooldownHours = 8 },
        });

        return new SubscriptionService(
            subscriptionRepo,
            Substitute.For<IMachineRepository>(),
            Substitute.For<IAlertRuleRepository>(),
            Substitute.For<IIntegrationRepository>(),
            Substitute.For<ITierFeatureLimitRepository>(),
            Substitute.For<ITenantSubscriptionOverrideRepository>(),
            Substitute.For<ITenantRepository>(),
            Substitute.For<IInvitationRepository>(),
            tierDefaults,
            TimeProvider.System,
            new NullLogger<SubscriptionService>());
    }
}
