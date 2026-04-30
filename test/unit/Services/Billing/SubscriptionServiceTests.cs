// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="SubscriptionService"/>.
/// </summary>
public class SubscriptionServiceTests
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
    public async Task CanApproveMachine_UnderLimit_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, machineLimit: 5);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(), logger);

        bool result = await service.CanApproveMachineAsync(1, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task CanApproveMachine_AtLimit_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, machineLimit: 2);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        // Insert 2 active machines to reach the limit
        Machine m1 = TestDataBuilder.BuildMachine(tenantId: 1);
        m1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m1);

        Machine m2 = TestDataBuilder.BuildMachine(tenantId: 1);
        m2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m2);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(), logger);

        bool result = await service.CanApproveMachineAsync(1, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task CanApproveMachine_UnlimitedMachines_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, machineLimit: null);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(), logger);

        bool result = await service.CanApproveMachineAsync(1, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task CanApproveMachine_NoSubscription_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(), logger);

        bool result = await service.CanApproveMachineAsync(999, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ProvisionFreeSubscription_CreatesCorrectDefaults()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(), logger);

        TenantSubscription result = await service.ProvisionFreeSubscriptionAsync(42, CancellationToken.None);

        await Assert.That(result.TenantId).IsEqualTo(42);
        await Assert.That(result.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(result.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(result.MachineLimit).IsEqualTo(3);
        await Assert.That(result.RetentionDays).IsEqualTo(1);
        await Assert.That(result.Id).IsNotEqualTo(0);
    }

    [Test]
    public async Task ProvisionFreeSubscription_UsesConfiguredLimits()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(machineLimit: 10000, retentionDays: 365), logger);

        TenantSubscription result = await service.ProvisionFreeSubscriptionAsync(43, CancellationToken.None);

        await Assert.That(result.TenantId).IsEqualTo(43);
        await Assert.That(result.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(result.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(result.MachineLimit).IsEqualTo(10000);
        await Assert.That(result.RetentionDays).IsEqualTo(365);
    }

    [Test]
    public async Task GetRetentionDays_WithSubscription_ReturnsSubscriptionValue()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, retentionDays: 30);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(), logger);

        int result = await service.GetRetentionDaysForTenantAsync(1, CancellationToken.None);

        await Assert.That(result).IsEqualTo(30);
    }

    [Test]
    public async Task GetRetentionDays_WithoutSubscription_ReturnsDefault()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(), logger);

        int result = await service.GetRetentionDaysForTenantAsync(999, CancellationToken.None);

        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task GetSubscriptionForTenant_Exists_ReturnsSubscription()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 5);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(), logger);

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(5, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.TenantId).IsEqualTo(5);
    }

    [Test]
    public async Task GetSubscriptionForTenant_NotExists_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(), logger);

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(999, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMachineCount_NoMachines_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(), logger);

        int result = await service.GetMachineCountForTenantAsync(1, CancellationToken.None);

        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task GetMachineCount_WithActiveMachines_ReturnsCount()
    {
        using TestDatabaseFactory dbFactory = new();

        Machine m1 = TestDataBuilder.BuildMachine(tenantId: 1);
        m1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m1);

        Machine m2 = TestDataBuilder.BuildMachine(tenantId: 1);
        m2.IsDeleted = true;
        m2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m2);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(), logger);

        int result = await service.GetMachineCountForTenantAsync(1, CancellationToken.None);

        await Assert.That(result).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureSubscriptionExists_NoSubscription_ProvisionsFreeTier()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(), logger);

        await service.EnsureSubscriptionExistsAsync(100, CancellationToken.None);

        TenantSubscription? sub = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 100);

        await Assert.That(sub).IsNotNull();
        await Assert.That(sub!.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(sub.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(sub.MachineLimit).IsEqualTo(3);
        await Assert.That(sub.RetentionDays).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureSubscriptionExists_ActiveSubscription_NoOp()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 200, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(), logger);

        await service.EnsureSubscriptionExistsAsync(200, CancellationToken.None);

        int count = await dbFactory.Context.TenantSubscriptions
            .Where(s => s.TenantId == 200)
            .CountAsync();

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureSubscriptionExists_InactiveFreeSubscription_Reactivates()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 300, tier: SubscriptionTier.Free, status: SubscriptionStatus.Canceled);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<SubscriptionService> logger = new NullLogger<SubscriptionService>();
        SubscriptionService service = new(scopeFactory, BuildOptions(), logger);

        await service.EnsureSubscriptionExistsAsync(300, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 300);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    // ========== Subscription Active Status Tests ==========

    [Test]
    public async Task GetSubscriptionForTenant_PastDue_IsNotActive()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.PastDue);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        // PastDue subscriptions should not be considered active for telemetry acceptance
        await Assert.That(result!.Status == SubscriptionStatus.Active).IsFalse();
    }

    [Test]
    public async Task GetSubscriptionForTenant_Active_IsActive()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Status == SubscriptionStatus.Active).IsTrue();
    }

    [Test]
    public async Task GetSubscriptionForTenant_Canceled_IsNotActive()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Free, status: SubscriptionStatus.Canceled);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(1, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Status == SubscriptionStatus.Active).IsFalse();
    }

    // ========== Alert Rule Limit Tests ==========

    [Test]
    public async Task CanCreateAlertRule_UnderLimit_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.AlertRuleLimit = 25;
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        // Seed 10 rules (well under the limit of 25)
        for (int i = 0; i < 10; i++)
        {
            AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: 1);
            await dbFactory.Context.InsertWithInt32IdentityAsync(rule);
        }

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        bool result = await service.CanCreateAlertRuleAsync(1, null, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task CanCreateAlertRule_AtLimit_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.AlertRuleLimit = 25;
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        // Seed exactly 25 rules to reach the limit
        for (int i = 0; i < 25; i++)
        {
            AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: 1);
            await dbFactory.Context.InsertWithInt32IdentityAsync(rule);
        }

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        bool result = await service.CanCreateAlertRuleAsync(1, null, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task CanCreateAlertRule_NullLimit_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team);
        sub.AlertRuleLimit = null; // Unlimited
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        bool result = await service.CanCreateAlertRuleAsync(1, null, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task CanCreateAlertRule_ZeroLimit_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.AlertRuleLimit = 0;
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        // Zero rules exist, but limit is zero so no rules can be created
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        bool result = await service.CanCreateAlertRuleAsync(1, null, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    // ========== Webhook Limit Tests ==========

    [Test]
    public async Task CanCreateWebhook_AtLimit_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.WebhookLimit = 5;
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        // Seed exactly 5 webhooks to reach the limit
        for (int i = 0; i < 5; i++)
        {
            WebhookEndpoint webhook = TestDataBuilder.BuildWebhookEndpoint(tenantId: 1);
            await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);
        }

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        SubscriptionService service = new(scopeFactory, BuildOptions(), new NullLogger<SubscriptionService>());

        bool result = await service.CanCreateWebhookAsync(1, null, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }
}
