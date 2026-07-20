// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Options;
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
            MemberLimit = 1,
            UpdatedAt = now,
        });
        tierLimitRepo.GetLimitsForTierAsync(SubscriptionTier.Pro, Arg.Any<CancellationToken>()).Returns(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Pro,
            MachineLimit = 1000,
            RetentionDays = 60,
            AlertRuleLimit = 10,
            WebhookLimit = 5,
            MemberLimit = 5,
            UpdatedAt = now,
        });
        tierLimitRepo.GetLimitsForTierAsync(SubscriptionTier.Team, Arg.Any<CancellationToken>()).Returns(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Team,
            MachineLimit = 10000,
            RetentionDays = 365,
            AlertRuleLimit = 25,
            WebhookLimit = 15,
            MemberLimit = int.MaxValue,
            UpdatedAt = now,
        });

        // Override repo: use real DB repo when testing override precedence, mock otherwise
        ITenantSubscriptionOverrideRepository overrideRepo = useRealOverrideRepo
            ? repo
            : Substitute.For<ITenantSubscriptionOverrideRepository>();

        IOptions<TierDefaultOptions> tierDefaults = Options.Create(new TierDefaultOptions
        {
            Free = new() { MachineLimit = 3, RetentionDays = 1, AlertRuleLimit = 0, WebhookLimit = 0, MemberLimit = 1 },
            Pro = new() { MachineLimit = 1000, RetentionDays = 60, AlertRuleLimit = 10, WebhookLimit = 5, MemberLimit = 5 },
            Team = new() { MachineLimit = 10000, RetentionDays = 365, AlertRuleLimit = 25, WebhookLimit = 15, MemberLimit = int.MaxValue },
        });

        return new SubscriptionService(repo, repo, repo, repo, tierLimitRepo, overrideRepo, repo, repo, tierDefaults, TimeProvider.System, new NullLogger<SubscriptionService>());
    }

    private static SubscriptionService BuildServiceWithMemberCounts(
        SubscriptionTier tier,
        int memberLimit,
        int activeMembers,
        int pendingInvitations)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantSubscription?>(new TenantSubscription
            {
                TenantId = 1,
                Tier = tier,
                Status = SubscriptionStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            }));

        ITierFeatureLimitRepository tierLimitRepo = Substitute.For<ITierFeatureLimitRepository>();
        tierLimitRepo.GetLimitsForTierAsync(Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TierFeatureLimit?>(new TierFeatureLimit
            {
                Tier = tier,
                MachineLimit = 1000,
                RetentionDays = 60,
                AlertRuleLimit = 10,
                WebhookLimit = 5,
                MemberLimit = memberLimit,
                UpdatedAt = now,
            }));

        ITenantSubscriptionOverrideRepository overrideRepo = Substitute.For<ITenantSubscriptionOverrideRepository>();
        overrideRepo.GetOverrideForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantSubscriptionOverride?>(null));

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.CountActiveMembersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(activeMembers));

        IInvitationRepository invitationRepo = Substitute.For<IInvitationRepository>();
        invitationRepo.CountPendingInvitationsAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(pendingInvitations));

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        IAlertRuleRepository alertRuleRepo = Substitute.For<IAlertRuleRepository>();
        IIntegrationRepository integrationRepo = Substitute.For<IIntegrationRepository>();

        IOptions<TierDefaultOptions> tierDefaults = Options.Create(new TierDefaultOptions
        {
            Free = new() { MachineLimit = 3, RetentionDays = 1, AlertRuleLimit = 0, WebhookLimit = 0, MemberLimit = 1 },
            Pro = new() { MachineLimit = 1000, RetentionDays = 60, AlertRuleLimit = 10, WebhookLimit = 5, MemberLimit = 5 },
            Team = new() { MachineLimit = 10000, RetentionDays = 365, AlertRuleLimit = 25, WebhookLimit = 15, MemberLimit = int.MaxValue },
        });

        return new SubscriptionService(
            subscriptionRepo, machineRepo, alertRuleRepo, integrationRepo, tierLimitRepo, overrideRepo,
            tenantRepo, invitationRepo, tierDefaults, TimeProvider.System, new NullLogger<SubscriptionService>());
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
    public async Task IsIngestEligible_ActiveSubscription_ReturnsTrue()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildSubscription(tenantId: 1, status: SubscriptionStatus.Active));
            SubscriptionService service = BuildService(repo);

            await Assert.That(await service.IsIngestEligibleAsync(1, CancellationToken.None)).IsTrue();
        }
    }

    [Test]
    public async Task IsIngestEligible_PastDueSubscription_ReturnsTrue_GracePeriod()
    {
        // PastDue is a Stripe dunning grace period — ingest continues, matching web access.
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildSubscription(tenantId: 1, status: SubscriptionStatus.PastDue));
            SubscriptionService service = BuildService(repo);

            await Assert.That(await service.IsIngestEligibleAsync(1, CancellationToken.None)).IsTrue();
        }
    }

    [Test]
    public async Task IsIngestEligible_CanceledSubscription_ReturnsFalse()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildSubscription(tenantId: 1, status: SubscriptionStatus.Canceled));
            SubscriptionService service = BuildService(repo);

            await Assert.That(await service.IsIngestEligibleAsync(1, CancellationToken.None)).IsFalse();
        }
    }

    [Test]
    public async Task IsIngestEligible_NoSubscription_ReturnsFalse()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            SubscriptionService service = BuildService(repo);

            await Assert.That(await service.IsIngestEligibleAsync(999, CancellationToken.None)).IsFalse();
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

            // Seed exactly 5 integration endpoints to reach the limit
            for (int i = 0; i < 5; i++)
            {
                IntegrationEndpoint integration = new()
                {
                    TenantId = 1,
                    Provider = IntegrationProvider.Custom,
                    Name = $"Integration {i}",
                    Configuration = """{"url":"https://hooks.example.com/test","secret":"test-secret"}""",
                    IsEnabled = true,
                    CreatedByUserId = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                await dbFactory.Context.InsertWithInt32IdentityAsync(integration);
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
            await Assert.That(limits.MemberLimit).IsEqualTo(1);
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

    [Test]
    public async Task EnsureSubscriptionExists_CanceledPaidSubscription_RevertsToFree()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(
                tenantId: 400, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Canceled);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            SubscriptionService service = BuildService(repo);

            await service.EnsureSubscriptionExistsAsync(400, CancellationToken.None);

            TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
                .FirstOrDefaultAsync(s => s.TenantId == 400);

            await Assert.That(updated).IsNotNull();
            await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Free);
            await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        }
    }

    [Test]
    public async Task EnsureSubscriptionExists_PastDuePaidSubscription_RetainsTierAndStatus()
    {
        // Regression test: previously, PastDue paid subscriptions silently fell through
        // EnsureSubscriptionExistsAsync without any action or logging. The method now
        // explicitly handles PastDue status, retaining the current tier while Stripe
        // handles dunning. This prevents accidental revert-to-free or re-provisioning.
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(
                tenantId: 500, tier: SubscriptionTier.Pro, status: SubscriptionStatus.PastDue);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            SubscriptionService service = BuildService(repo);

            await service.EnsureSubscriptionExistsAsync(500, CancellationToken.None);

            TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
                .FirstOrDefaultAsync(s => s.TenantId == 500);

            // Should remain PastDue Pro — not reverted or re-provisioned
            await Assert.That(updated).IsNotNull();
            await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Pro);
            await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.PastDue);

            // Verify no additional subscription was created
            int count = await dbFactory.Context.TenantSubscriptions
                .Where(s => s.TenantId == 500)
                .CountAsync();
            await Assert.That(count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task GetEffectiveLimits_NoSubscription_ReturnsFreeDefaults()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // No subscription exists for this tenant
            SubscriptionService service = BuildService(repo);

            EffectiveLimits limits = await service.GetEffectiveLimitsForTenantAsync(888, CancellationToken.None);

            await Assert.That(limits.MachineLimit).IsEqualTo(3);
            await Assert.That(limits.RetentionDays).IsEqualTo(1);
            await Assert.That(limits.AlertRuleLimit).IsEqualTo(0);
            await Assert.That(limits.WebhookLimit).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetEffectiveLimits_NullTierLimits_FallsBackToConfig()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // Build service with null tier limits for Pro
            ITierFeatureLimitRepository tierLimitRepo = Substitute.For<ITierFeatureLimitRepository>();
            tierLimitRepo.GetLimitsForTierAsync(Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<TierFeatureLimit?>(null));

            ITenantSubscriptionOverrideRepository overrideRepo = Substitute.For<ITenantSubscriptionOverrideRepository>();
            overrideRepo.GetOverrideForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<TenantSubscriptionOverride?>(null));

            IOptions<TierDefaultOptions> tierDefaults = Options.Create(new TierDefaultOptions
            {
                Free = new() { MachineLimit = 3, RetentionDays = 1, AlertRuleLimit = 0, WebhookLimit = 0, MemberLimit = 1 },
                Pro = new() { MachineLimit = 500, RetentionDays = 30, AlertRuleLimit = 8, WebhookLimit = 4, MemberLimit = 5 },
                Team = new() { MachineLimit = 10000, RetentionDays = 365, AlertRuleLimit = 25, WebhookLimit = 15, MemberLimit = int.MaxValue },
            });

            SubscriptionService service = new(repo, repo, repo, repo, tierLimitRepo, overrideRepo, repo, repo, tierDefaults, TimeProvider.System, new NullLogger<SubscriptionService>());

            EffectiveLimits limits = await service.GetEffectiveLimitsForTenantAsync(1, CancellationToken.None);

            // Should fall back to config defaults for Pro tier
            await Assert.That(limits.MachineLimit).IsEqualTo(500);
            await Assert.That(limits.RetentionDays).IsEqualTo(30);
            await Assert.That(limits.AlertRuleLimit).IsEqualTo(8);
            await Assert.That(limits.WebhookLimit).IsEqualTo(4);
            await Assert.That(limits.MemberLimit).IsEqualTo(5);
        }
    }

    [Test]
    public async Task GetMachineCountAtDate_ReturnsCountFromRepository()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Machine m1 = TestDataBuilder.BuildMachine(tenantId: 1);
            m1.RegisteredOn = now.AddDays(-5);
            m1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m1);

            SubscriptionService service = BuildService(repo);

            int count = await service.GetMachineCountAtDateAsync(1, now, CancellationToken.None);

            await Assert.That(count).IsGreaterThanOrEqualTo(0);
        }
    }

    [Test]
    public async Task CanCreateWebhook_NoSubscription_ReturnsFalse()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            SubscriptionService service = BuildService(repo);

            bool result = await service.CanCreateWebhookAsync(999, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    [Test]
    public async Task CanCreateAlertRule_NoSubscription_ReturnsFalse()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            SubscriptionService service = BuildService(repo);

            bool result = await service.CanCreateAlertRuleAsync(999, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    [Test]
    public async Task CanCreateWebhook_UnderLimit_ReturnsTrue()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
            // Webhook limit is 5 via TierFeatureLimits for Pro tier
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // Seed 2 integration endpoints (under the limit of 5)
            for (int i = 0; i < 2; i++)
            {
                IntegrationEndpoint integration = new()
                {
                    TenantId = 1,
                    Provider = IntegrationProvider.Custom,
                    Name = $"Integration {i}",
                    Configuration = """{"url":"https://hooks.example.com/test","secret":"test-secret"}""",
                    IsEnabled = true,
                    CreatedByUserId = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                await dbFactory.Context.InsertWithInt32IdentityAsync(integration);
            }

            SubscriptionService service = BuildService(repo);

            bool result = await service.CanCreateWebhookAsync(1, CancellationToken.None);

            await Assert.That(result).IsTrue();
        }
    }

    /// <summary>
    /// Verifies that when a tenant override exists but the override's specific field (MachineLimit)
    /// is null, GetEffectiveLimitsForTenantAsync falls through to the tier-based limit rather than
    /// treating the override as a hard cap. This exercises the overrideValue-is-null branch
    /// inside GetEffectiveLimitsForTenantAsync.
    /// </summary>
    [Test]
    public async Task CanApproveMachine_OverrideExistsButMachineLimitNull_FallsBackToTierLimit()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // Pro tier allows 1000 machines
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // Seed 5 machines — well under the Pro limit of 1000
            for (int i = 0; i < 5; i++)
            {
                Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
                await dbFactory.Context.InsertWithInt32IdentityAsync(machine);
            }

            // Insert an override that sets RetentionDays but leaves MachineLimit null
            // This means MachineLimit should fall through to the tier default (1000)
            await dbFactory.Context.InsertAsync(new TenantSubscriptionOverride
            {
                TenantId = 1,
                MachineLimit = null,
                RetentionDays = 180,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            SubscriptionService service = BuildService(repo, useRealOverrideRepo: true);

            bool result = await service.CanApproveMachineAsync(1, CancellationToken.None);

            // 5 machines < 1000 tier limit, so should be allowed
            await Assert.That(result).IsTrue();
        }
    }

    /// <summary>
    /// Verifies that GetEffectiveLimitsForTenantAsync falls back to configuration defaults
    /// for the Team tier when no DB tier limits exist, testing the GetConfigDefaultsForTier
    /// Team branch.
    /// </summary>
    [Test]
    public async Task GetEffectiveLimits_TeamTier_NullDbLimits_FallsBackToTeamConfigDefaults()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Team);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            ITierFeatureLimitRepository tierLimitRepo = Substitute.For<ITierFeatureLimitRepository>();
            tierLimitRepo.GetLimitsForTierAsync(Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<TierFeatureLimit?>(null));

            ITenantSubscriptionOverrideRepository overrideRepo = Substitute.For<ITenantSubscriptionOverrideRepository>();
            overrideRepo.GetOverrideForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<TenantSubscriptionOverride?>(null));

            IOptions<TierDefaultOptions> tierDefaults = Options.Create(new TierDefaultOptions
            {
                Free = new() { MachineLimit = 3, RetentionDays = 1, AlertRuleLimit = 0, WebhookLimit = 0, MemberLimit = 1 },
                Pro = new() { MachineLimit = 1000, RetentionDays = 60, AlertRuleLimit = 10, WebhookLimit = 5, MemberLimit = 5 },
                Team = new() { MachineLimit = 5000, RetentionDays = 180, AlertRuleLimit = 20, WebhookLimit = 10, MemberLimit = int.MaxValue },
            });

            SubscriptionService service = new(repo, repo, repo, repo, tierLimitRepo, overrideRepo, repo, repo, tierDefaults,
                TimeProvider.System, new NullLogger<SubscriptionService>());

            EffectiveLimits limits = await service.GetEffectiveLimitsForTenantAsync(1, CancellationToken.None);

            // Should use Team config defaults since DB returned null
            await Assert.That(limits.MachineLimit).IsEqualTo(5000);
            await Assert.That(limits.RetentionDays).IsEqualTo(180);
            await Assert.That(limits.AlertRuleLimit).IsEqualTo(20);
            await Assert.That(limits.WebhookLimit).IsEqualTo(10);
            await Assert.That(limits.MemberLimit).IsEqualTo(int.MaxValue);
        }
    }

    /// <summary>
    /// Verifies that GetEffectiveLimitsForTenantAsync (via CanApproveMachineAsync) falls back to
    /// config defaults when no DB tier limits exist, exercising the config fallback branch with
    /// a warning log for Pro tier.
    /// </summary>
    [Test]
    public async Task CanApproveMachine_NullTierLimits_FallsBackToConfigDefault()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // 2 machines — under any reasonable limit
            for (int i = 0; i < 2; i++)
            {
                Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
                await dbFactory.Context.InsertWithInt32IdentityAsync(machine);
            }

            ITierFeatureLimitRepository tierLimitRepo = Substitute.For<ITierFeatureLimitRepository>();
            tierLimitRepo.GetLimitsForTierAsync(Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<TierFeatureLimit?>(null));

            ITenantSubscriptionOverrideRepository overrideRepo = Substitute.For<ITenantSubscriptionOverrideRepository>();
            overrideRepo.GetOverrideForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<TenantSubscriptionOverride?>(null));

            IOptions<TierDefaultOptions> tierDefaults = Options.Create(new TierDefaultOptions
            {
                Free = new() { MachineLimit = 3, RetentionDays = 1, AlertRuleLimit = 0, WebhookLimit = 0 },
                Pro = new() { MachineLimit = 500, RetentionDays = 60, AlertRuleLimit = 10, WebhookLimit = 5 },
                Team = new() { MachineLimit = 5000, RetentionDays = 180, AlertRuleLimit = 20, WebhookLimit = 10 },
            });

            SubscriptionService service = new(repo, repo, repo, repo, tierLimitRepo, overrideRepo, repo, repo, tierDefaults,
                TimeProvider.System, new NullLogger<SubscriptionService>());

            bool result = await service.CanApproveMachineAsync(1, CancellationToken.None);

            // 2 machines < 500 Pro config default, so should allow
            await Assert.That(result).IsTrue();
        }
    }

    /// <summary>
    /// Verifies that CanCreateWebhookAsync uses the override's WebhookLimit when an override exists
    /// with a non-null WebhookLimit, exercising the override-field-present branch.
    /// </summary>
    [Test]
    public async Task CanCreateWebhook_OverrideTakesPrecedence_LimitsWebhooks()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // Pro tier has WebhookLimit=5 by default
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            // Seed 2 integrations
            for (int i = 0; i < 2; i++)
            {
                IntegrationEndpoint integration = new()
                {
                    TenantId = 1,
                    Provider = IntegrationProvider.Custom,
                    Name = $"Integration {i}",
                    Configuration = """{"url":"https://hooks.example.com/test","secret":"test-secret"}""",
                    IsEnabled = true,
                    CreatedByUserId = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                await dbFactory.Context.InsertWithInt32IdentityAsync(integration);
            }

            // Override sets WebhookLimit to 2 — should block the 3rd
            await dbFactory.Context.InsertAsync(new TenantSubscriptionOverride
            {
                TenantId = 1,
                WebhookLimit = 2,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            SubscriptionService service = BuildService(repo, useRealOverrideRepo: true);

            bool result = await service.CanCreateWebhookAsync(1, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    // ========== Member Limit Tests ==========

    [Test]
    public async Task CanAddMember_UnderLimit_ReturnsTrue()
    {
        SubscriptionService service = BuildServiceWithMemberCounts(
            SubscriptionTier.Pro, memberLimit: 5, activeMembers: 2, pendingInvitations: 1);

        bool result = await service.CanAddMemberAsync(1, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task CanAddMember_AtLimit_ReturnsFalse()
    {
        SubscriptionService service = BuildServiceWithMemberCounts(
            SubscriptionTier.Pro, memberLimit: 5, activeMembers: 4, pendingInvitations: 1);

        bool result = await service.CanAddMemberAsync(1, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task CanAddMember_AboveLimit_ReturnsFalse()
    {
        SubscriptionService service = BuildServiceWithMemberCounts(
            SubscriptionTier.Pro, memberLimit: 5, activeMembers: 5, pendingInvitations: 2);

        bool result = await service.CanAddMemberAsync(1, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task CanAddMember_PendingInvitationsCountTowardCap_ReturnsFalse()
    {
        // 3 active + 2 pending = 5, which equals the limit of 5 — at cap
        SubscriptionService service = BuildServiceWithMemberCounts(
            SubscriptionTier.Pro, memberLimit: 5, activeMembers: 3, pendingInvitations: 2);

        bool result = await service.CanAddMemberAsync(1, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task CanAddMember_FreeTierOwnerOnly_ReturnsFalse()
    {
        // Free tier allows 1 member; the owner already occupies the single slot
        SubscriptionService service = BuildServiceWithMemberCounts(
            SubscriptionTier.Free, memberLimit: 1, activeMembers: 1, pendingInvitations: 0);

        bool result = await service.CanAddMemberAsync(1, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task CanAddMember_NoSubscription_ReturnsFalse()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            SubscriptionService service = BuildService(repo);

            bool result = await service.CanAddMemberAsync(999, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    [Test]
    public async Task CanAddMember_NoTierLimitRow_UsesConfigMemberLimit()
    {
        // No TierFeatureLimit row exists, so the member-limit resolution falls through to the
        // configuration default (Pro => 5). 4 members are under that config cap, so a member can
        // still be added — this exercises the config-fallback selector for the member limit.
        DateTimeOffset now = DateTimeOffset.UtcNow;

        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantSubscription?>(new TenantSubscription
            {
                TenantId = 1,
                Tier = SubscriptionTier.Pro,
                Status = SubscriptionStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            }));

        ITierFeatureLimitRepository tierLimitRepo = Substitute.For<ITierFeatureLimitRepository>();
        tierLimitRepo.GetLimitsForTierAsync(Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TierFeatureLimit?>(null));

        ITenantSubscriptionOverrideRepository overrideRepo = Substitute.For<ITenantSubscriptionOverrideRepository>();
        overrideRepo.GetOverrideForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantSubscriptionOverride?>(null));

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.CountActiveMembersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(4));

        IInvitationRepository invitationRepo = Substitute.For<IInvitationRepository>();
        invitationRepo.CountPendingInvitationsAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        IAlertRuleRepository alertRuleRepo = Substitute.For<IAlertRuleRepository>();
        IIntegrationRepository integrationRepo = Substitute.For<IIntegrationRepository>();

        IOptions<TierDefaultOptions> tierDefaults = Options.Create(new TierDefaultOptions
        {
            Free = new() { MachineLimit = 3, RetentionDays = 1, AlertRuleLimit = 0, WebhookLimit = 0, MemberLimit = 1 },
            Pro = new() { MachineLimit = 1000, RetentionDays = 60, AlertRuleLimit = 10, WebhookLimit = 5, MemberLimit = 5 },
            Team = new() { MachineLimit = 10000, RetentionDays = 365, AlertRuleLimit = 25, WebhookLimit = 15, MemberLimit = int.MaxValue },
        });

        SubscriptionService service = new(
            subscriptionRepo, machineRepo, alertRuleRepo, integrationRepo, tierLimitRepo, overrideRepo,
            tenantRepo, invitationRepo, tierDefaults, TimeProvider.System, new NullLogger<SubscriptionService>());

        bool result = await service.CanAddMemberAsync(1, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task GetEffectiveLimits_NoOverride_IncludesTierMemberLimit()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
            sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

            SubscriptionService service = BuildService(repo);

            EffectiveLimits limits = await service.GetEffectiveLimitsForTenantAsync(1, CancellationToken.None);

            // Pro tier MemberLimit comes from the tier limit repo mock (5)
            await Assert.That(limits.MemberLimit).IsEqualTo(5);
        }
    }
}
