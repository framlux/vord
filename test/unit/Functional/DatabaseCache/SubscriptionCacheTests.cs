// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Functional.DatabaseCache;

/// <summary>
/// Functional tests for subscription-related operations exercised through
/// <see cref="SubscriptionService"/> (which wraps the DatabaseContext).
/// </summary>
public class SubscriptionCacheTests
{
    private static IOptions<SubscriptionOptions> BuildOptions(int machineLimit = 3, int retentionDays = 1)
    {
        return Options.Create(new SubscriptionOptions
        {
            FreeTierMachineLimit = machineLimit,
            FreeTierRetentionDays = retentionDays,
        });
    }

    [Test]
    public async Task CreateSubscription_ValidSubscription_PersistsWithId()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1,
            tier: SubscriptionTier.Pro,
            status: SubscriptionStatus.Active,
            machineLimit: 10,
            retentionDays: 30);

        int subId = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        await Assert.That(subId).IsNotEqualTo(0);

        // Verify retrieval through SubscriptionService
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(result.MachineLimit).IsEqualTo(10);
        await Assert.That(result.RetentionDays).IsEqualTo(30);
    }

    [Test]
    public async Task GetSubscriptionByTenantId_ExistingSubscription_ReturnsSubscription()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 42);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(42, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.TenantId).IsEqualTo(42);
        await Assert.That(result.Id).IsEqualTo(sub.Id);
    }

    [Test]
    public async Task GetSubscriptionByTenantId_NonExistentTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(99999, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ProvisionFreeSubscription_CreatesWithCorrectDefaults()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        TenantSubscription result = await service.ProvisionFreeSubscriptionAsync(77, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.TenantId).IsEqualTo(77);
        await Assert.That(result.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(result.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(result.MachineLimit).IsEqualTo(3);
        await Assert.That(result.RetentionDays).IsEqualTo(1);
        await Assert.That(result.Id).IsNotEqualTo(0);
    }

    [Test]
    public async Task GetSubscriptionForTenantAsync_AfterDirectDbUpdate_ReturnsUpdatedTier()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 50,
            tier: SubscriptionTier.Free,
            machineLimit: 3,
            retentionDays: 1);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        // Update the subscription tier and limits directly through the context
        await dbFactory.Context.TenantSubscriptions
            .Where(s => s.Id == sub.Id)
            .Set(s => s.Tier, SubscriptionTier.Pro)
            .Set(s => s.MachineLimit, 25)
            .Set(s => s.RetentionDays, 30)
            .Set(s => s.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync();

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(50, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(result.MachineLimit).IsEqualTo(25);
        await Assert.That(result.RetentionDays).IsEqualTo(30);
    }

    [Test]
    public async Task GetRetentionDays_WithSubscription_ReturnsConfiguredValue()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 60, retentionDays: 90);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        int result = await service.GetRetentionDaysForTenantAsync(60, CancellationToken.None);

        await Assert.That(result).IsEqualTo(90);
    }

    [Test]
    public async Task GetRetentionDays_NoSubscription_ReturnsDefaultOfOne()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        int result = await service.GetRetentionDaysForTenantAsync(99999, CancellationToken.None);

        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task CanApproveMachine_NoSubscription_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        bool result = await service.CanApproveMachineAsync(88888, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task CanApproveMachine_UnlimitedMachines_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 70, machineLimit: null);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        bool result = await service.CanApproveMachineAsync(70, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }
}
