// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for subscription-related operations exercised through
/// <see cref="SubscriptionService"/> (which wraps the DatabaseContext).
/// </summary>
public class SubscriptionCacheTests
{
    private static SubscriptionService BuildService(Database.Repositories.DatabaseRepository repo, int machineLimit = 3, int retentionDays = 1)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ITierFeatureLimitRepository tierLimitRepo = Substitute.For<ITierFeatureLimitRepository>();
        tierLimitRepo.GetLimitsForTierAsync(SubscriptionTier.Free, Arg.Any<CancellationToken>()).Returns(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Free,
            MachineLimit = machineLimit,
            RetentionDays = retentionDays,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            UpdatedAt = now,
        });
        tierLimitRepo.GetLimitsForTierAsync(SubscriptionTier.Pro, Arg.Any<CancellationToken>()).Returns(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Pro,
            MachineLimit = 1000,
            RetentionDays = 60,
            AlertRuleLimit = 10,
            WebhookLimit = 5,
            UpdatedAt = now,
        });
        tierLimitRepo.GetLimitsForTierAsync(SubscriptionTier.Team, Arg.Any<CancellationToken>()).Returns(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Team,
            MachineLimit = 10000,
            RetentionDays = 365,
            AlertRuleLimit = 25,
            WebhookLimit = 15,
            UpdatedAt = now,
        });
        ITenantSubscriptionOverrideRepository overrideRepo = Substitute.For<ITenantSubscriptionOverrideRepository>();

        IOptions<TierDefaultOptions> tierDefaults = Options.Create(new TierDefaultOptions
        {
            Free = new() { MachineLimit = 3, RetentionDays = 1, AlertRuleLimit = 0, WebhookLimit = 0 },
            Pro = new() { MachineLimit = 1000, RetentionDays = 60, AlertRuleLimit = 10, WebhookLimit = 5 },
            Team = new() { MachineLimit = 10000, RetentionDays = 365, AlertRuleLimit = 25, WebhookLimit = 15 },
        });

        return new SubscriptionService(repo, repo, repo, repo, tierLimitRepo, overrideRepo, tierDefaults, new NullLogger<SubscriptionService>());
    }

    [Test]
    public async Task CreateSubscription_ValidSubscription_PersistsWithId()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1,
            tier: SubscriptionTier.Pro,
            status: SubscriptionStatus.Active);

        int subId = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        await Assert.That(subId).IsNotEqualTo(0);

        // Verify retrieval through SubscriptionService
        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        SubscriptionService service = BuildService(repo);

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Tier).IsEqualTo(SubscriptionTier.Pro);
    }

    [Test]
    public async Task GetSubscriptionByTenantId_ExistingSubscription_ReturnsSubscription()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 42);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        SubscriptionService service = BuildService(repo);

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(42, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.TenantId).IsEqualTo(42);
        await Assert.That(result.Id).IsEqualTo(sub.Id);
    }

    [Test]
    public async Task GetSubscriptionByTenantId_NonExistentTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        SubscriptionService service = BuildService(repo);

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(99999, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ProvisionFreeSubscription_CreatesWithCorrectDefaults()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        SubscriptionService service = BuildService(repo);

        TenantSubscription result = await service.ProvisionFreeSubscriptionAsync(77, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.TenantId).IsEqualTo(77);
        await Assert.That(result.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(result.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(result.Id).IsNotEqualTo(0);
    }

    [Test]
    public async Task GetSubscriptionForTenantAsync_AfterDirectDbUpdate_ReturnsUpdatedTier()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 50,
            tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        // Update the subscription tier directly through the context
        await dbFactory.Context.TenantSubscriptions
            .Where(s => s.Id == sub.Id)
            .Set(s => s.Tier, SubscriptionTier.Pro)
            .Set(s => s.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync();

        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        SubscriptionService service = BuildService(repo);

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(50, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Tier).IsEqualTo(SubscriptionTier.Pro);
    }

    [Test]
    public async Task GetRetentionDays_WithSubscription_ReturnsTierLimitValue()
    {
        using TestDatabaseFactory dbFactory = new();

        // Subscription uses Pro tier; retention days are now resolved from tier limits
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 60, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        SubscriptionService service = BuildService(repo);

        int result = await service.GetRetentionDaysForTenantAsync(60, CancellationToken.None);

        // Pro tier returns 60 days from the tier limit repo mock
        await Assert.That(result).IsEqualTo(60);
    }

    [Test]
    public async Task GetRetentionDays_NoSubscription_ReturnsDefaultOfOne()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        SubscriptionService service = BuildService(repo);

        int result = await service.GetRetentionDaysForTenantAsync(99999, CancellationToken.None);

        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task CanApproveMachine_NoSubscription_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        SubscriptionService service = BuildService(repo);

        bool result = await service.CanApproveMachineAsync(88888, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task CanApproveMachine_UnlimitedMachines_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 70);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        Database.Repositories.DatabaseRepository repo = new(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        SubscriptionService service = BuildService(repo);

        bool result = await service.CanApproveMachineAsync(70, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }
}
