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
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="SubscriptionService"/>.
/// </summary>
public class SubscriptionServiceTests
{
    private static SubscriptionService BuildService(DatabaseRepository repo, int machineLimit = 3, int retentionDays = 1, bool useRealOverrideRepo = false)
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

        // Override repo: use real DB repo when testing override precedence, mock otherwise
        ITenantSubscriptionOverrideRepository overrideRepo = useRealOverrideRepo
            ? repo
            : Substitute.For<ITenantSubscriptionOverrideRepository>();

        IOptions<TierDefaultOptions> tierDefaults = Options.Create(new TierDefaultOptions
        {
            Free = new() { MachineLimit = 3, RetentionDays = 1, AlertRuleLimit = 0, WebhookLimit = 0 },
            Pro = new() { MachineLimit = 1000, RetentionDays = 60, AlertRuleLimit = 10, WebhookLimit = 5 },
            Team = new() { MachineLimit = 10000, RetentionDays = 365, AlertRuleLimit = 25, WebhookLimit = 15 },
        });

        return new SubscriptionService(repo, repo, repo, repo, tierLimitRepo, overrideRepo, tierDefaults, new NullLogger<SubscriptionService>());
    }

    private static (DatabaseRepository repo, TestDatabaseFactory dbFactory) BuildRepoAndFactory()
    {
        TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        return (repo, dbFactory);
    }

    [Test]
    public async Task CanApproveMachine_UnderLimit_ReturnsTrue()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            SubscriptionService service = BuildService(repo);

            bool result = await service.CanApproveMachineAsync(1, CancellationToken.None);

            await Assert.That(result).IsTrue();
        }
    }

    [Test]
    public async Task CanApproveMachine_AtLimit_ReturnsFalse()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // Insert 2 active machines to reach the limit
            Machine m1 = TestDataBuilder.BuildMachine(tenantId: 1);
            m1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m1);

            Machine m2 = TestDataBuilder.BuildMachine(tenantId: 1);
            m2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m2);

            SubscriptionService service = BuildService(repo, machineLimit: 2);

            bool result = await service.CanApproveMachineAsync(1, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    [Test]
    public async Task CanApproveMachine_UnlimitedMachines_ReturnsTrue()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            SubscriptionService service = BuildService(repo);

            bool result = await service.CanApproveMachineAsync(1, CancellationToken.None);

            await Assert.That(result).IsTrue();
        }
    }

    [Test]
    public async Task CanApproveMachine_NoSubscription_ReturnsFalse()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            SubscriptionService service = BuildService(repo);

            bool result = await service.CanApproveMachineAsync(999, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    [Test]
    public async Task ProvisionFreeSubscription_CreatesCorrectDefaults()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            SubscriptionService service = BuildService(repo);

            TenantSubscription result = await service.ProvisionFreeSubscriptionAsync(42, CancellationToken.None);

            await Assert.That(result.TenantId).IsEqualTo(42);
            await Assert.That(result.Tier).IsEqualTo(SubscriptionTier.Free);
            await Assert.That(result.Status).IsEqualTo(SubscriptionStatus.Active);
            await Assert.That(result.Id).IsNotEqualTo(0);
        }
    }

    [Test]
    public async Task ProvisionFreeSubscription_UsesConfiguredLimits()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            SubscriptionService service = BuildService(repo, machineLimit: 10000, retentionDays: 365);

            TenantSubscription result = await service.ProvisionFreeSubscriptionAsync(43, CancellationToken.None);

            await Assert.That(result.TenantId).IsEqualTo(43);
            await Assert.That(result.Tier).IsEqualTo(SubscriptionTier.Free);
            await Assert.That(result.Status).IsEqualTo(SubscriptionStatus.Active);
        }
    }

    [Test]
    public async Task GetRetentionDays_WithSubscription_ReturnsTierLimitValue()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // Subscription uses Pro tier; retention days come from the tier limit repo
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            SubscriptionService service = BuildService(repo);

            int result = await service.GetRetentionDaysForTenantAsync(1, CancellationToken.None);

            // Pro tier returns 60 days from the tier limit repo mock
            await Assert.That(result).IsEqualTo(60);
        }
    }

    [Test]
    public async Task GetRetentionDays_WithoutSubscription_ReturnsDefault()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            SubscriptionService service = BuildService(repo);

            int result = await service.GetRetentionDaysForTenantAsync(999, CancellationToken.None);

            await Assert.That(result).IsEqualTo(1);
        }
    }

    [Test]
    public async Task GetSubscriptionForTenant_Exists_ReturnsSubscription()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 5);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            SubscriptionService service = BuildService(repo);

            TenantSubscription? result = await service.GetSubscriptionForTenantAsync(5, CancellationToken.None);

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.TenantId).IsEqualTo(5);
        }
    }

    [Test]
    public async Task GetSubscriptionForTenant_NotExists_ReturnsNull()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            SubscriptionService service = BuildService(repo);

            TenantSubscription? result = await service.GetSubscriptionForTenantAsync(999, CancellationToken.None);

            await Assert.That(result).IsNull();
        }
    }

    [Test]
    public async Task GetMachineCount_NoMachines_ReturnsZero()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            SubscriptionService service = BuildService(repo);

            int result = await service.GetMachineCountForTenantAsync(1, CancellationToken.None);

            await Assert.That(result).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetMachineCount_WithActiveMachines_ReturnsCount()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            Machine m1 = TestDataBuilder.BuildMachine(tenantId: 1);
            m1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m1);

            Machine m2 = TestDataBuilder.BuildMachine(tenantId: 1);
            m2.IsDeleted = true;
            m2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m2);

            SubscriptionService service = BuildService(repo);

            int result = await service.GetMachineCountForTenantAsync(1, CancellationToken.None);

            await Assert.That(result).IsEqualTo(1);
        }
    }

    [Test]
    public async Task EnsureSubscriptionExists_NoSubscription_ProvisionsFreeTier()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            SubscriptionService service = BuildService(repo);

            await service.EnsureSubscriptionExistsAsync(100, CancellationToken.None);

            TenantSubscription? sub = await dbFactory.Context.TenantSubscriptions
                .FirstOrDefaultAsync(s => s.TenantId == 100);

            await Assert.That(sub).IsNotNull();
            await Assert.That(sub!.Tier).IsEqualTo(SubscriptionTier.Free);
            await Assert.That(sub.Status).IsEqualTo(SubscriptionStatus.Active);
        }
    }

    [Test]
    public async Task EnsureSubscriptionExists_ActiveSubscription_NoOp()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 200, tier: SubscriptionTier.Pro);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            SubscriptionService service = BuildService(repo);

            await service.EnsureSubscriptionExistsAsync(200, CancellationToken.None);

            int count = await dbFactory.Context.TenantSubscriptions
                .Where(s => s.TenantId == 200)
                .CountAsync();

            await Assert.That(count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task EnsureSubscriptionExists_InactiveFreeSubscription_Reactivates()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(
                tenantId: 300, tier: SubscriptionTier.Free, status: SubscriptionStatus.Canceled);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            SubscriptionService service = BuildService(repo);

            await service.EnsureSubscriptionExistsAsync(300, CancellationToken.None);

            TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
                .FirstOrDefaultAsync(s => s.TenantId == 300);

            await Assert.That(updated).IsNotNull();
            await Assert.That(updated!.Status).IsEqualTo(SubscriptionStatus.Active);
        }
    }

    // ========== Subscription Active Status Tests ==========

    [Test]
    public async Task GetSubscriptionForTenant_PastDue_IsNotActive()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(
                tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.PastDue);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            SubscriptionService service = BuildService(repo);

            TenantSubscription? result = await service.GetSubscriptionForTenantAsync(1, CancellationToken.None);

            await Assert.That(result).IsNotNull();
            // PastDue subscriptions should not be considered active for telemetry acceptance
            await Assert.That(result!.Status == SubscriptionStatus.Active).IsFalse();
        }
    }

    [Test]
    public async Task GetSubscriptionForTenant_Active_IsActive()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(
                tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            SubscriptionService service = BuildService(repo);

            TenantSubscription? result = await service.GetSubscriptionForTenantAsync(1, CancellationToken.None);

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.Status == SubscriptionStatus.Active).IsTrue();
        }
    }

    [Test]
    public async Task GetSubscriptionForTenant_Canceled_IsNotActive()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(
                tenantId: 1, tier: SubscriptionTier.Free, status: SubscriptionStatus.Canceled);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            SubscriptionService service = BuildService(repo);

            TenantSubscription? result = await service.GetSubscriptionForTenantAsync(1, CancellationToken.None);

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.Status == SubscriptionStatus.Active).IsFalse();
        }
    }

    // ========== Alert Rule Limit Tests ==========

    [Test]
    public async Task CanCreateAlertRule_UnderLimit_ReturnsTrue()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
            // Alert rule limit is now managed via TierFeatureLimits table
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // Seed 5 rules (under the limit of 10)
            for (int i = 0; i < 5; i++)
            {
                AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: 1);
                await dbFactory.Context.InsertWithInt32IdentityAsync(rule);
            }

            SubscriptionService service = BuildService(repo);

            bool result = await service.CanCreateAlertRuleAsync(1, CancellationToken.None);

            await Assert.That(result).IsTrue();
        }
    }

    [Test]
    public async Task CanCreateAlertRule_AtLimit_ReturnsFalse()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
            // Alert rule limit is now managed via TierFeatureLimits table
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // Seed exactly 10 rules to reach the limit
            for (int i = 0; i < 10; i++)
            {
                AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: 1);
                await dbFactory.Context.InsertWithInt32IdentityAsync(rule);
            }

            SubscriptionService service = BuildService(repo);

            bool result = await service.CanCreateAlertRuleAsync(1, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    [Test]
    public async Task CanCreateAlertRule_NullLimit_ReturnsTrue()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team);
            // Alert rule limit is null (unlimited) via TierFeatureLimits for Team tier
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            SubscriptionService service = BuildService(repo);

            bool result = await service.CanCreateAlertRuleAsync(1, CancellationToken.None);

            await Assert.That(result).IsTrue();
        }
    }

    [Test]
    public async Task CanCreateAlertRule_ZeroLimit_ReturnsFalse()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
            // Alert rule limit is 0 via TierFeatureLimits for Free tier
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // Zero rules exist, but limit is zero so no rules can be created
            SubscriptionService service = BuildService(repo);

            bool result = await service.CanCreateAlertRuleAsync(1, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    // ========== Webhook Limit Tests ==========

    [Test]
    public async Task CanCreateWebhook_AtLimit_ReturnsFalse()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
            // Webhook limit is 5 via TierFeatureLimits for Pro tier
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // Seed exactly 5 webhooks to reach the limit
            for (int i = 0; i < 5; i++)
            {
                WebhookEndpoint webhook = TestDataBuilder.BuildWebhookEndpoint(tenantId: 1);
                await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);
            }

            SubscriptionService service = BuildService(repo);

            bool result = await service.CanCreateWebhookAsync(1, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    // ========== Override Precedence Tests ==========

    [Test]
    public async Task CanApproveMachine_OverrideTakesPrecedenceOverTierDefault()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // Pro tier has unlimited machines (null MachineLimit)
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // Seed 3 machines
            for (int i = 0; i < 3; i++)
            {
                Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
                await dbFactory.Context.InsertWithInt32IdentityAsync(machine);
            }

            // Set a per-tenant override of 3 machines — should block the 4th
            await dbFactory.Context.InsertAsync(new TenantSubscriptionOverride
            {
                TenantId = 1,
                MachineLimit = 3,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            SubscriptionService service = BuildService(repo, useRealOverrideRepo: true);

            bool result = await service.CanApproveMachineAsync(1, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    [Test]
    public async Task CanApproveMachine_NoOverride_UsesTierDefault()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // Free tier has MachineLimit=3 from TierFeatureLimits
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // Seed 2 machines — under the limit
            for (int i = 0; i < 2; i++)
            {
                Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
                await dbFactory.Context.InsertWithInt32IdentityAsync(machine);
            }

            // No override inserted — tier default of 3 should allow the 3rd machine
            SubscriptionService service = BuildService(repo);

            bool result = await service.CanApproveMachineAsync(1, CancellationToken.None);

            await Assert.That(result).IsTrue();
        }
    }

    [Test]
    public async Task CanCreateAlertRule_OverrideTakesPrecedence()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // Pro tier has AlertRuleLimit=25 by default
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // Seed 5 alert rules
            for (int i = 0; i < 5; i++)
            {
                AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: 1);
                await dbFactory.Context.InsertWithInt32IdentityAsync(rule);
            }

            // Override sets alert rule limit to 5 — should block the 6th
            await dbFactory.Context.InsertAsync(new TenantSubscriptionOverride
            {
                TenantId = 1,
                AlertRuleLimit = 5,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            SubscriptionService service = BuildService(repo, useRealOverrideRepo: true);

            bool result = await service.CanCreateAlertRuleAsync(1, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    [Test]
    public async Task GetEffectiveLimits_OverrideFieldsOverrideTierDefaults()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // Override only RetentionDays — other fields should fall back to Pro tier defaults
            await dbFactory.Context.InsertAsync(new TenantSubscriptionOverride
            {
                TenantId = 1,
                RetentionDays = 90,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            SubscriptionService service = BuildService(repo, useRealOverrideRepo: true);

            EffectiveLimits limits = await service.GetEffectiveLimitsForTenantAsync(1, CancellationToken.None);

            // RetentionDays overridden to 90
            await Assert.That(limits.RetentionDays).IsEqualTo(90);
            // MachineLimit falls back to Pro tier default (1000)
            await Assert.That(limits.MachineLimit).IsEqualTo(1000);
            // AlertRuleLimit falls back to Pro tier default (10)
            await Assert.That(limits.AlertRuleLimit).IsEqualTo(10);
            // WebhookLimit falls back to Pro tier default (5)
            await Assert.That(limits.WebhookLimit).IsEqualTo(5);
        }
    }

    [Test]
    public async Task GetEffectiveLimits_NoOverride_ReturnsTierDefaults()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            SubscriptionService service = BuildService(repo);

            EffectiveLimits limits = await service.GetEffectiveLimitsForTenantAsync(1, CancellationToken.None);

            await Assert.That(limits.MachineLimit).IsEqualTo(3);
            await Assert.That(limits.RetentionDays).IsEqualTo(1);
            await Assert.That(limits.AlertRuleLimit).IsEqualTo(0);
            await Assert.That(limits.WebhookLimit).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetRetentionDays_OverrideTakesPrecedence()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // Pro tier has RetentionDays=60
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // Override retention to 180 days
            await dbFactory.Context.InsertAsync(new TenantSubscriptionOverride
            {
                TenantId = 1,
                RetentionDays = 180,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            SubscriptionService service = BuildService(repo, useRealOverrideRepo: true);

            int retentionDays = await service.GetRetentionDaysForTenantAsync(1, CancellationToken.None);

            await Assert.That(retentionDays).IsEqualTo(180);
        }
    }
}
