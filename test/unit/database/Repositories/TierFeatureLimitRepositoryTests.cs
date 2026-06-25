// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Repositories;

/// <summary>
/// Tests for <see cref="DatabaseRepository.UpdateLimitsForTierAsync"/>, verifying that the
/// per-tier MemberLimit is persisted alongside the other feature limits. The seed values
/// themselves (Free=1, Pro=5, Team=int.MaxValue) are asserted against the real Postgres
/// fixture in the integration migration tests, since the in-memory test database copies
/// only the schema and not the seeded rows.
/// </summary>
public class TierFeatureLimitRepositoryTests
{
    [Test]
    public async Task UpdateLimitsForTierAsync_PersistsMemberLimit()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await dbFactory.Context.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Free,
            MachineLimit = 3,
            RetentionDays = 1,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            MemberLimit = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        int updated = await repo.UpdateLimitsForTierAsync(SubscriptionTier.Free, machineLimit: 3, retentionDays: 1, alertRuleLimit: 0, webhookLimit: 0, memberLimit: 2, CancellationToken.None);

        await Assert.That(updated).IsEqualTo(1);

        TierFeatureLimit? limits = await repo.GetLimitsForTierAsync(SubscriptionTier.Free, CancellationToken.None);
        await Assert.That(limits).IsNotNull();
        await Assert.That(limits!.MemberLimit).IsEqualTo(2);
    }

    [Test]
    public async Task UpdateLimitsForTierAsync_PersistsUnlimitedSentinel()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await dbFactory.Context.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Team,
            MachineLimit = 10000,
            RetentionDays = 365,
            AlertRuleLimit = 25,
            WebhookLimit = 15,
            MemberLimit = 5,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        int updated = await repo.UpdateLimitsForTierAsync(SubscriptionTier.Team, machineLimit: 10000, retentionDays: 365, alertRuleLimit: 25, webhookLimit: 15, memberLimit: int.MaxValue, CancellationToken.None);

        await Assert.That(updated).IsEqualTo(1);

        TierFeatureLimit? limits = await repo.GetLimitsForTierAsync(SubscriptionTier.Team, CancellationToken.None);
        await Assert.That(limits).IsNotNull();
        await Assert.That(limits!.MemberLimit).IsEqualTo(int.MaxValue);
    }
}
