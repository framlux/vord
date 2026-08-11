// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for the retention-resolution repository methods added for retention-class
/// partitioning: <c>GetEffectiveRetentionDaysAsync</c> (the value the ingest path stamps a row's
/// class from) and <c>GetLongClassRetentionDaysAsync</c> (the Long-class drop window). Both run
/// against a real SQLite database through <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public sealed class EffectiveRetentionRepositoryTests
{
    private static Database.Repositories.DatabaseRepository RepoFor(TestDatabaseFactory dbFactory)
    {
        return new Database.Repositories.DatabaseRepository(
            dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
    }

    private static async Task SeedTierAsync(TestDatabaseFactory dbFactory, SubscriptionTier tier, int retentionDays)
    {
        await dbFactory.Context.InsertAsync(new TierFeatureLimit
        {
            Tier = tier,
            MachineLimit = 3,
            RetentionDays = retentionDays,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            MemberLimit = 1,
            MinimumBillableMachines = 0,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    [Test]
    public async Task GetEffectiveRetentionDays_NoSubscription_ReturnsFailSafeOne()
    {
        using TestDatabaseFactory dbFactory = new();

        int result = await RepoFor(dbFactory).GetEffectiveRetentionDaysAsync(4242, CancellationToken.None);

        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task GetEffectiveRetentionDays_TierDefault_NoOverride_ReturnsTierRetention()
    {
        using TestDatabaseFactory dbFactory = new();
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro));
        await SeedTierAsync(dbFactory, SubscriptionTier.Pro, retentionDays: 60);

        int result = await RepoFor(dbFactory).GetEffectiveRetentionDaysAsync(1, CancellationToken.None);

        await Assert.That(result).IsEqualTo(60);
    }

    [Test]
    public async Task GetEffectiveRetentionDays_OverridePresent_WinsOverTierDefault()
    {
        using TestDatabaseFactory dbFactory = new();
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free));
        await SeedTierAsync(dbFactory, SubscriptionTier.Free, retentionDays: 1);
        await dbFactory.Context.InsertAsync(new TenantSubscriptionOverride
        {
            TenantId = 1,
            RetentionDays = 30,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        int result = await RepoFor(dbFactory).GetEffectiveRetentionDaysAsync(1, CancellationToken.None);

        // A Free tenant with a 30-day override resolves to 30 — the value that places it in Medium.
        await Assert.That(result).IsEqualTo(30);
    }

    [Test]
    public async Task GetLongClassRetentionDays_NoOverrides_ReturnsFloor()
    {
        using TestDatabaseFactory dbFactory = new();

        int result = await RepoFor(dbFactory).GetLongClassRetentionDaysAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo(365);
    }

    [Test]
    public async Task GetLongClassRetentionDays_OverrideBeyondFloor_ExtendsWindow()
    {
        using TestDatabaseFactory dbFactory = new();
        await dbFactory.Context.InsertAsync(new TenantSubscriptionOverride
        {
            TenantId = 1,
            RetentionDays = 400,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        int result = await RepoFor(dbFactory).GetLongClassRetentionDaysAsync(CancellationToken.None);

        await Assert.That(result).IsEqualTo(400);
    }

    [Test]
    public async Task GetLongClassRetentionDays_OverrideBelowFloor_StaysAtFloor()
    {
        using TestDatabaseFactory dbFactory = new();
        await dbFactory.Context.InsertAsync(new TenantSubscriptionOverride
        {
            TenantId = 1,
            RetentionDays = 30,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        int result = await RepoFor(dbFactory).GetLongClassRetentionDaysAsync(CancellationToken.None);

        // A sub-floor override never shrinks the Long window below its 365-day floor.
        await Assert.That(result).IsEqualTo(365);
    }
}
