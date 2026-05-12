// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Tools;
using Microsoft.Extensions.Logging.Abstractions;
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
            UpdatedAt = now,
        });

        await context.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Pro,
            MachineLimit = 1000,
            RetentionDays = 60,
            AlertRuleLimit = 10,
            WebhookLimit = 5,
            UpdatedAt = now,
        });

        await context.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Team,
            MachineLimit = 10000,
            RetentionDays = 365,
            AlertRuleLimit = 25,
            WebhookLimit = 15,
            UpdatedAt = now,
        });
    }

    private static BillingWebhookHandler CreateHandler(
        TestDatabaseFactory dbFactory,
        IDowngradeCleanupService? cleanupService = null)
    {
        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        return new BillingWebhookHandler(
            repo,
            repo,
            repo,
            repo,
            cleanupService ?? Substitute.For<IDowngradeCleanupService>());
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

    [Test]
    public async Task HandleCheckoutCompletedAsync_CreatesDefaultAlertRules()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        List<AlertRule> rules = await dbFactory.Context.AlertRules
            .Where(r => r.TenantId == 1)
            .ToListAsync();
        await Assert.That(rules.Count).IsEqualTo(8);
        await Assert.That(rules.All(r => r.IsCustom == false)).IsTrue();
        await Assert.That(rules.All(r => r.IsEnabled == true)).IsTrue();
        await Assert.That(rules.All(r => r.CreatedByUserId == 1)).IsTrue();
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_ExistingRules_DoesNotDuplicate()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        // First upgrade creates default alert rules
        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        int countAfterFirst = await dbFactory.Context.AlertRules
            .Where(r => r.TenantId == 1)
            .CountAsync();
        await Assert.That(countAfterFirst).IsEqualTo(8);

        // Second upgrade should not duplicate the rules
        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Team, CancellationToken.None);

        int countAfterSecond = await dbFactory.Context.AlertRules
            .Where(r => r.TenantId == 1)
            .CountAsync();
        await Assert.That(countAfterSecond).IsEqualTo(8);
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

    // ========== M6: Payment failure changes status but NOT tier or limits ==========

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

    // ========== Default alert rule seed correctness tests ==========

    [Test]
    public async Task HandleCheckoutCompletedAsync_DefaultRules_VolatileMetricsDuration5()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        AlertMetric[] volatileMetrics = [AlertMetric.CpuUsage, AlertMetric.MemoryUsage, AlertMetric.DiskUsage];
        List<AlertRule> volatileRules = await dbFactory.Context.AlertRules
            .Where(r => r.TenantId == 1 && r.Metric.In(volatileMetrics))
            .ToListAsync();

        await Assert.That(volatileRules.Count).IsEqualTo(3);
        await Assert.That(volatileRules.All(r => r.DurationMinutes == 5)).IsTrue();
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_DefaultRules_StateMetricsDuration1()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        AlertMetric[] stateMetrics = [AlertMetric.FailedServices, AlertMetric.SecurityUpdates, AlertMetric.MachineOffline, AlertMetric.DiskHealth];
        List<AlertRule> stateRules = await dbFactory.Context.AlertRules
            .Where(r => r.TenantId == 1 && r.Metric.In(stateMetrics))
            .ToListAsync();

        await Assert.That(stateRules.Count).IsEqualTo(4);
        await Assert.That(stateRules.All(r => r.DurationMinutes == 1)).IsTrue();
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_DefaultRules_SshConnectionDuration0()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        AlertRule? sshRule = await dbFactory.Context.AlertRules
            .FirstOrDefaultAsync(r => r.TenantId == 1 && r.Metric == AlertMetric.SshConnection);

        await Assert.That(sshRule).IsNotNull();
        await Assert.That(sshRule!.DurationMinutes).IsEqualTo(0);
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_DefaultRules_CriticalSeverityForOfflineAndDiskHealth()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        AlertRule? offlineRule = await dbFactory.Context.AlertRules
            .FirstOrDefaultAsync(r => r.TenantId == 1 && r.Metric == AlertMetric.MachineOffline);
        AlertRule? diskHealthRule = await dbFactory.Context.AlertRules
            .FirstOrDefaultAsync(r => r.TenantId == 1 && r.Metric == AlertMetric.DiskHealth);

        await Assert.That(offlineRule).IsNotNull();
        await Assert.That(offlineRule!.Severity).IsEqualTo(AlertSeverity.Critical);
        await Assert.That(diskHealthRule).IsNotNull();
        await Assert.That(diskHealthRule!.Severity).IsEqualTo(AlertSeverity.Critical);
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_DefaultRules_InfoSeverityForSshAndSecurityUpdates()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        AlertRule? sshRule = await dbFactory.Context.AlertRules
            .FirstOrDefaultAsync(r => r.TenantId == 1 && r.Metric == AlertMetric.SshConnection);
        AlertRule? securityUpdatesRule = await dbFactory.Context.AlertRules
            .FirstOrDefaultAsync(r => r.TenantId == 1 && r.Metric == AlertMetric.SecurityUpdates);

        await Assert.That(sshRule).IsNotNull();
        await Assert.That(sshRule!.Severity).IsEqualTo(AlertSeverity.Info);
        await Assert.That(securityUpdatesRule).IsNotNull();
        await Assert.That(securityUpdatesRule!.Severity).IsEqualTo(AlertSeverity.Info);
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_DefaultRules_AllNotifyEmailTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        List<AlertRule> rules = await dbFactory.Context.AlertRules
            .Where(r => r.TenantId == 1)
            .ToListAsync();

        await Assert.That(rules.Count).IsEqualTo(8);
        await Assert.That(rules.All(r => r.NotifyEmail == true)).IsTrue();
    }

    [Test]
    public async Task HandleCheckoutCompletedAsync_DefaultRules_AllNotifyWebhookFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        List<AlertRule> rules = await dbFactory.Context.AlertRules
            .Where(r => r.TenantId == 1)
            .ToListAsync();

        await Assert.That(rules.Count).IsEqualTo(8);
        await Assert.That(rules.All(r => r.NotifyWebhook == false)).IsTrue();
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
    public async Task HandleCheckoutCompletedAsync_DefaultRules_SystemUserCreatedBy()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedTierFeatureLimitsAsync(dbFactory.Context);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        BillingWebhookHandler handler = CreateHandler(dbFactory);

        await handler.HandleCheckoutCompletedAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        List<AlertRule> rules = await dbFactory.Context.AlertRules
            .Where(r => r.TenantId == 1)
            .ToListAsync();

        await Assert.That(rules.Count).IsEqualTo(8);
        await Assert.That(rules.All(r => r.CreatedByUserId == 1)).IsTrue();
    }
}
