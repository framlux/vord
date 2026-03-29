// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="BillingWebhookHandler"/>.
/// </summary>
public class BillingWebhookHandlerTests
{
    private static IDatabaseCache CreateCache(TestDatabaseFactory dbFactory)
    {
        return new DatabaseCache(dbFactory.Context, new NullLogger<DatabaseCache>());
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_UpgradesToPro()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free, machineLimit: 3, retentionDays: 1);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(updated.MachineLimit).IsNull();
        await Assert.That(updated.RetentionDays).IsEqualTo(30);
    }

    [Test]
    public async Task HandleSubscriptionUpdatedAsync_UpdatesPeriodEnd()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));
        DateTimeOffset newPeriodEnd = DateTimeOffset.UtcNow.AddDays(30);

        await handler.HandleSubscriptionUpdatedAsync(1, newPeriodEnd, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        // SQLite stores DateTimeOffset as TEXT, compare by rough equality
        await Assert.That(updated!.CurrentPeriodEnd.HasValue).IsEqualTo(true);
        TimeSpan difference = (updated.CurrentPeriodEnd!.Value - newPeriodEnd).Duration();
        await Assert.That(difference.TotalSeconds).IsLessThan(2);
    }

    [Test]
    public async Task HandleSubscriptionDeletedAsync_NoPendingAction_DeactivatesSubscription()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro, machineLimit: null, retentionDays: 30);
        sub.PendingAction = PendingSubscriptionAction.None;
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        await handler.HandleSubscriptionDeletedAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(SubscriptionStatus.Canceled);
        await Assert.That(updated.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(updated.CancelAtPeriodEnd).IsEqualTo(false);
        await Assert.That(updated.PendingAction).IsEqualTo(PendingSubscriptionAction.None);
    }

    [Test]
    public async Task HandleSubscriptionDeletedAsync_CancelAccount_DeactivatesSubscription()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team, machineLimit: null, retentionDays: 365);
        sub.PendingAction = PendingSubscriptionAction.CancelAccount;
        sub.CancelAtPeriodEnd = true;
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        await handler.HandleSubscriptionDeletedAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(SubscriptionStatus.Canceled);
        await Assert.That(updated.CancelAtPeriodEnd).IsEqualTo(false);
        await Assert.That(updated.PendingAction).IsEqualTo(PendingSubscriptionAction.None);
    }

    [Test]
    public async Task HandleSubscriptionDeletedAsync_DowngradeToFree_RevertsToFreeAndCleansUp()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro, machineLimit: null, retentionDays: 30);
        sub.PendingAction = PendingSubscriptionAction.DowngradeToFree;
        sub.CancelAtPeriodEnd = true;
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDatabaseCache cache = CreateCache(dbFactory);
        IDowngradeCleanupService cleanupService = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = new(cache, dbFactory.Context, cleanupService, Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        await handler.HandleSubscriptionDeletedAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(updated.MachineLimit).IsEqualTo(3);
        await Assert.That(updated.RetentionDays).IsEqualTo(1);
        await Assert.That(updated.PendingAction).IsEqualTo(PendingSubscriptionAction.None);
        await cleanupService.Received(1).CleanupForFreeTierAsync(1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleSubscriptionDeletedAsync_DowngradeToPro_DowngradesAndCleansUp()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team, machineLimit: null, retentionDays: 365);
        sub.PendingAction = PendingSubscriptionAction.DowngradeToPro;
        sub.CancelAtPeriodEnd = true;
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDatabaseCache cache = CreateCache(dbFactory);
        IDowngradeCleanupService cleanupService = Substitute.For<IDowngradeCleanupService>();
        BillingWebhookHandler handler = new(cache, dbFactory.Context, cleanupService, Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        await handler.HandleSubscriptionDeletedAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(updated.MachineLimit).IsNull();
        await Assert.That(updated.RetentionDays).IsEqualTo(30);
        await Assert.That(updated.PendingAction).IsEqualTo(PendingSubscriptionAction.None);
        await cleanupService.Received(1).CleanupForProTierAsync(1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandlePaymentFailedAsync_SetsStatusToPastDue()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

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
        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        // Should not throw
        await handler.HandlePaymentFailedAsync(999, CancellationToken.None);

        int count = await dbFactory.Context.TenantSubscriptions.CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task HandleSubscriptionDeletedAsync_NoMatchingSubscription_NoOp()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        // Should not throw
        await handler.HandleSubscriptionDeletedAsync(999, CancellationToken.None);

        int count = await dbFactory.Context.TenantSubscriptions.CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_UpgradesToTeam_SetsRetention365()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free, machineLimit: 3, retentionDays: 1);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Team, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Team);
        await Assert.That(updated.RetentionDays).IsEqualTo(365);
        await Assert.That(updated.MachineLimit).IsNull();
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_NoExistingSubscription_NoOp()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        // No subscription exists for tenant 999 — should not throw
        await handler.HandleCheckoutCompletedAsync(999, SubscriptionTier.Pro, CancellationToken.None);

        int count = await dbFactory.Context.TenantSubscriptions.CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_CreatesDefaultAlertRules()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free, machineLimit: 3, retentionDays: 1);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        List<AlertRule> rules = await dbFactory.Context.AlertRules
            .Where(r => r.TenantId == 1)
            .ToListAsync();
        await Assert.That(rules.Count).IsEqualTo(3);
        await Assert.That(rules.All(r => r.IsCustom == false)).IsEqualTo(true);
        await Assert.That(rules.All(r => r.IsEnabled == true)).IsEqualTo(true);
        await Assert.That(rules.All(r => r.CreatedByUserId == 1)).IsEqualTo(true);
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_ExistingRules_DoesNotDuplicate()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free, machineLimit: 3, retentionDays: 1);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        // First upgrade creates default alert rules
        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        int countAfterFirst = await dbFactory.Context.AlertRules
            .Where(r => r.TenantId == 1)
            .CountAsync();
        await Assert.That(countAfterFirst).IsEqualTo(3);

        // Second upgrade should not duplicate the rules
        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Team, CancellationToken.None);

        int countAfterSecond = await dbFactory.Context.AlertRules
            .Where(r => r.TenantId == 1)
            .CountAsync();
        await Assert.That(countAfterSecond).IsEqualTo(3);
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_AlreadyOnPro_StaysOnPro()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro, machineLimit: null, retentionDays: 30);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(updated.MachineLimit).IsNull();
        await Assert.That(updated.RetentionDays).IsEqualTo(30);
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_ProToTeam_ChangesToTeam()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro, machineLimit: null, retentionDays: 30);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Team, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Team);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(updated.MachineLimit).IsNull();
        await Assert.That(updated.RetentionDays).IsEqualTo(365);
    }

    [Test]
    public async Task HandleDowngradeToProAsync_SetsCorrectValues()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team, machineLimit: null, retentionDays: 365);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        await handler.HandleDowngradeToProAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(updated.MachineLimit).IsNull();
        await Assert.That(updated.RetentionDays).IsEqualTo(30);
    }

    [Test]
    public async Task HandlePaymentSucceededAsync_SetsStatusToActive()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.PastDue);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

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
        IDatabaseCache cache = CreateCache(dbFactory);
        BillingWebhookHandler handler = new(cache, dbFactory.Context, Substitute.For<IDowngradeCleanupService>(), Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 3, FreeTierRetentionDays = 1 }));

        // Should not throw when no subscription exists
        await handler.HandlePaymentSucceededAsync(999, CancellationToken.None);

        int count = await dbFactory.Context.TenantSubscriptions.CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }
}
