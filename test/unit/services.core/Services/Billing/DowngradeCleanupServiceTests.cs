// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="DowngradeCleanupService"/>.
/// </summary>
public class DowngradeCleanupServiceTests
{
    private static (DatabaseRepository repo, TestDatabaseFactory dbFactory) BuildRepoAndFactory()
    {
        TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        return (repo, dbFactory);
    }

    // --- CleanupForProTierAsync ---

    [Test]
    public async Task CleanupForProTierAsync_DisablesCustomOidcConfig()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 1, isEnabled: true);
            oidcConfig.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(oidcConfig);

            DowngradeCleanupService service = new(repo, repo, repo, repo, repo, repo, Substitute.For<IApiKeyCacheInvalidator>(), new NullLogger<DowngradeCleanupService>());

            await service.CleanupForProTierAsync(1, CancellationToken.None);

            TenantOidcConfiguration? updated = await dbFactory.Context.TenantOidcConfigurations
                .FirstOrDefaultAsync(c => c.TenantId == 1);
            await Assert.That(updated).IsNotNull();
            await Assert.That(updated!.IsEnabled).IsFalse();
        }
    }

    [Test]
    public async Task CleanupForProTierAsync_DisablesCustomAlertRules()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            AlertRule customRule = TestDataBuilder.BuildAlertRule(
                tenantId: 1, isCustom: true, isEnabled: true);
            customRule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(customRule);

            DowngradeCleanupService service = new(repo, repo, repo, repo, repo, repo, Substitute.For<IApiKeyCacheInvalidator>(), new NullLogger<DowngradeCleanupService>());

            await service.CleanupForProTierAsync(1, CancellationToken.None);

            AlertRule? updated = await dbFactory.Context.AlertRules
                .FirstOrDefaultAsync(r => r.Id == customRule.Id);
            await Assert.That(updated).IsNotNull();
            await Assert.That(updated!.IsEnabled).IsFalse();
        }
    }

    [Test]
    public async Task CleanupForProTierAsync_KeepsDefaultRulesEnabled()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // Default (system) rule should remain enabled
            AlertRule defaultRule = TestDataBuilder.BuildAlertRule(
                tenantId: 1, isCustom: false, isEnabled: true);
            defaultRule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(defaultRule);

            // Custom rule should be disabled
            AlertRule customRule = TestDataBuilder.BuildAlertRule(
                tenantId: 1, isCustom: true, isEnabled: true);
            customRule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(customRule);

            DowngradeCleanupService service = new(repo, repo, repo, repo, repo, repo, Substitute.For<IApiKeyCacheInvalidator>(), new NullLogger<DowngradeCleanupService>());

            await service.CleanupForProTierAsync(1, CancellationToken.None);

            AlertRule? updatedDefault = await dbFactory.Context.AlertRules
                .FirstOrDefaultAsync(r => r.Id == defaultRule.Id);
            await Assert.That(updatedDefault).IsNotNull();
            await Assert.That(updatedDefault!.IsEnabled).IsTrue();

            AlertRule? updatedCustom = await dbFactory.Context.AlertRules
                .FirstOrDefaultAsync(r => r.Id == customRule.Id);
            await Assert.That(updatedCustom).IsNotNull();
            await Assert.That(updatedCustom!.IsEnabled).IsFalse();
        }
    }

    [Test]
    public async Task CleanupForProTierAsync_DoesNotAffectOtherTenants()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // Tenant 1 OIDC config
            TenantOidcConfiguration oidcTenant1 = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 1, isEnabled: true);
            oidcTenant1.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(oidcTenant1);

            // Tenant 2 OIDC config — should remain untouched
            TenantOidcConfiguration oidcTenant2 = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 2, isEnabled: true);
            oidcTenant2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(oidcTenant2);

            // Tenant 2 custom rule — should remain untouched
            AlertRule ruleTenant2 = TestDataBuilder.BuildAlertRule(
                tenantId: 2, isCustom: true, isEnabled: true);
            ruleTenant2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(ruleTenant2);

            DowngradeCleanupService service = new(repo, repo, repo, repo, repo, repo, Substitute.For<IApiKeyCacheInvalidator>(), new NullLogger<DowngradeCleanupService>());

            await service.CleanupForProTierAsync(1, CancellationToken.None);

            TenantOidcConfiguration? tenant2Oidc = await dbFactory.Context.TenantOidcConfigurations
                .FirstOrDefaultAsync(c => c.TenantId == 2);
            await Assert.That(tenant2Oidc).IsNotNull();
            await Assert.That(tenant2Oidc!.IsEnabled).IsTrue();

            AlertRule? tenant2Rule = await dbFactory.Context.AlertRules
                .FirstOrDefaultAsync(r => r.TenantId == 2);
            await Assert.That(tenant2Rule).IsNotNull();
            await Assert.That(tenant2Rule!.IsEnabled).IsTrue();
        }
    }

    [Test]
    public async Task CleanupForProTierAsync_AlreadyDisabledOidc_NoError()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 1, isEnabled: false);
            oidcConfig.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(oidcConfig);

            DowngradeCleanupService service = new(repo, repo, repo, repo, repo, repo, Substitute.For<IApiKeyCacheInvalidator>(), new NullLogger<DowngradeCleanupService>());

            // Should complete without error even when nothing needs disabling
            await service.CleanupForProTierAsync(1, CancellationToken.None);

            TenantOidcConfiguration? updated = await dbFactory.Context.TenantOidcConfigurations
                .FirstOrDefaultAsync(c => c.TenantId == 1);
            await Assert.That(updated).IsNotNull();
            await Assert.That(updated!.IsEnabled).IsFalse();
        }
    }

    // --- CleanupForFreeTierAsync ---

    [Test]
    public async Task CleanupForFreeTierAsync_DisablesAllAlertRules()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            AlertRule defaultRule = TestDataBuilder.BuildAlertRule(
                tenantId: 1, isCustom: false, isEnabled: true);
            defaultRule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(defaultRule);

            AlertRule customRule = TestDataBuilder.BuildAlertRule(
                tenantId: 1, isCustom: true, isEnabled: true);
            customRule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(customRule);

            DowngradeCleanupService service = new(repo, repo, repo, repo, repo, repo, Substitute.For<IApiKeyCacheInvalidator>(), new NullLogger<DowngradeCleanupService>());

            await service.CleanupForFreeTierAsync(1, CancellationToken.None);

            List<AlertRule> rules = await dbFactory.Context.AlertRules
                .Where(r => r.TenantId == 1)
                .ToListAsync();
            await Assert.That(rules.Count).IsEqualTo(2);
            await Assert.That(rules.All(r => r.IsEnabled == false)).IsTrue();
        }
    }

    [Test]
    public async Task CleanupForFreeTierAsync_DisablesCustomOidcConfig()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 1, isEnabled: true);
            oidcConfig.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(oidcConfig);

            DowngradeCleanupService service = new(repo, repo, repo, repo, repo, repo, Substitute.For<IApiKeyCacheInvalidator>(), new NullLogger<DowngradeCleanupService>());

            await service.CleanupForFreeTierAsync(1, CancellationToken.None);

            TenantOidcConfiguration? updated = await dbFactory.Context.TenantOidcConfigurations
                .FirstOrDefaultAsync(c => c.TenantId == 1);
            await Assert.That(updated).IsNotNull();
            await Assert.That(updated!.IsEnabled).IsFalse();
        }
    }

    [Test]
    public async Task CleanupForFreeTierAsync_DisablesIntegrationEndpoints()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            IntegrationEndpoint integration = new()
            {
                TenantId = 1,
                Provider = IntegrationProvider.Custom,
                Name = "Test Integration",
                Configuration = """{"url":"https://hooks.example.com/test","secret":"test-secret"}""",
                IsEnabled = true,
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            integration.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

            DowngradeCleanupService service = new(repo, repo, repo, repo, repo, repo, Substitute.For<IApiKeyCacheInvalidator>(), new NullLogger<DowngradeCleanupService>());

            await service.CleanupForFreeTierAsync(1, CancellationToken.None);

            IntegrationEndpoint? updated = await dbFactory.Context.IntegrationEndpoints
                .FirstOrDefaultAsync(i => i.Id == integration.Id);
            await Assert.That(updated).IsNotNull();
            await Assert.That(updated!.IsEnabled).IsFalse();
        }
    }

    [Test]
    public async Task CleanupForFreeTierAsync_DoesNotAffectOtherTenants()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // Tenant 2 resources — should remain untouched
            TenantOidcConfiguration oidcTenant2 = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 2, isEnabled: true);
            oidcTenant2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(oidcTenant2);

            AlertRule ruleTenant2 = TestDataBuilder.BuildAlertRule(
                tenantId: 2, isCustom: true, isEnabled: true);
            ruleTenant2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(ruleTenant2);

            IntegrationEndpoint integrationTenant2 = new()
            {
                TenantId = 2,
                Provider = IntegrationProvider.Custom,
                Name = "Tenant 2 Integration",
                Configuration = """{"url":"https://hooks.example.com/test","secret":"test-secret"}""",
                IsEnabled = true,
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            integrationTenant2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(integrationTenant2);

            DowngradeCleanupService service = new(repo, repo, repo, repo, repo, repo, Substitute.For<IApiKeyCacheInvalidator>(), new NullLogger<DowngradeCleanupService>());

            await service.CleanupForFreeTierAsync(1, CancellationToken.None);

            TenantOidcConfiguration? tenant2Oidc = await dbFactory.Context.TenantOidcConfigurations
                .FirstOrDefaultAsync(c => c.TenantId == 2);
            await Assert.That(tenant2Oidc).IsNotNull();
            await Assert.That(tenant2Oidc!.IsEnabled).IsTrue();

            AlertRule? tenant2Rule = await dbFactory.Context.AlertRules
                .FirstOrDefaultAsync(r => r.TenantId == 2);
            await Assert.That(tenant2Rule).IsNotNull();
            await Assert.That(tenant2Rule!.IsEnabled).IsTrue();

            IntegrationEndpoint? tenant2Integration = await dbFactory.Context.IntegrationEndpoints
                .FirstOrDefaultAsync(i => i.TenantId == 2);
            await Assert.That(tenant2Integration).IsNotNull();
            await Assert.That(tenant2Integration!.IsEnabled).IsTrue();
        }
    }

    [Test]
    public async Task CleanupForFreeTierAsync_NoResources_CompletesWithoutError()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            DowngradeCleanupService service = new(repo, repo, repo, repo, repo, repo, Substitute.For<IApiKeyCacheInvalidator>(), new NullLogger<DowngradeCleanupService>());

            // Should not throw when no resources exist for the tenant
            await service.CleanupForFreeTierAsync(1, CancellationToken.None);
        }
    }

    [Test]
    public async Task CleanupForFreeTierAsync_AlreadyDisabledIntegration_NoError()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            IntegrationEndpoint integration = new()
            {
                TenantId = 1,
                Provider = IntegrationProvider.Custom,
                Name = "Disabled Integration",
                Configuration = """{"url":"https://hooks.example.com/test","secret":"test-secret"}""",
                IsEnabled = false,
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            integration.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

            DowngradeCleanupService service = new(repo, repo, repo, repo, repo, repo, Substitute.For<IApiKeyCacheInvalidator>(), new NullLogger<DowngradeCleanupService>());

            // Should complete without error when integration is already disabled
            await service.CleanupForFreeTierAsync(1, CancellationToken.None);

            IntegrationEndpoint? updated = await dbFactory.Context.IntegrationEndpoints
                .FirstOrDefaultAsync(i => i.Id == integration.Id);
            await Assert.That(updated).IsNotNull();
            await Assert.That(updated!.IsEnabled).IsFalse();
        }
    }

    // --- Machine trimming to the Free limit ---

    private static async Task SeedFreeMachineLimitAsync(TestDatabaseFactory dbFactory, int machineLimit)
    {
        await dbFactory.Context.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Free,
            MachineLimit = machineLimit,
            RetentionDays = 1,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            MemberLimit = 1,
            MinimumBillableMachines = 0,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    [Test]
    public async Task CleanupForFreeTier_OverLimit_SoftDeletesNewestMachines_KeepsOldest()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            await SeedFreeMachineLimitAsync(dbFactory, machineLimit: 1);

            DateTimeOffset t0 = DateTimeOffset.UtcNow.AddDays(-10);
            long[] ids = new long[4];
            for (int i = 0; i < 4; i++)
            {
                Machine m = TestDataBuilder.BuildMachine(tenantId: 1);
                m.RegisteredOn = t0.AddDays(i); // ids[0] oldest, ids[3] newest
                ids[i] = await dbFactory.Context.InsertWithInt64IdentityAsync(m);
            }

            IApiKeyCacheInvalidator invalidator = Substitute.For<IApiKeyCacheInvalidator>();
            DowngradeCleanupService service = new(repo, repo, repo, repo, repo, repo, invalidator, new NullLogger<DowngradeCleanupService>());

            await service.CleanupForFreeTierAsync(1, CancellationToken.None);

            // The oldest-registered machine survives; the three newest are soft-deleted.
            await Assert.That((await dbFactory.Context.Machines.FirstAsync(m => m.Id == ids[0])).IsDeleted).IsFalse();
            for (int i = 1; i < 4; i++)
            {
                await Assert.That((await dbFactory.Context.Machines.FirstAsync(m => m.Id == ids[i])).IsDeleted).IsTrue();
            }

            // One API-key cache invalidation and one audit entry per trimmed machine.
            await invalidator.Received(3).InvalidateByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            int trimAudits = await dbFactory.Context.AuditLog.CountAsync(a => a.Action == AuditAction.MachineDeleted);
            await Assert.That(trimAudits).IsEqualTo(3);
        }
    }

    [Test]
    public async Task CleanupForFreeTier_AtLimit_TrimsNothing()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            await SeedFreeMachineLimitAsync(dbFactory, machineLimit: 2);

            for (int i = 0; i < 2; i++)
            {
                await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 1));
            }

            IApiKeyCacheInvalidator invalidator = Substitute.For<IApiKeyCacheInvalidator>();
            DowngradeCleanupService service = new(repo, repo, repo, repo, repo, repo, invalidator, new NullLogger<DowngradeCleanupService>());

            await service.CleanupForFreeTierAsync(1, CancellationToken.None);

            int active = await dbFactory.Context.Machines.CountAsync(m => (m.TenantId == 1) && (m.IsDeleted == false));
            await Assert.That(active).IsEqualTo(2);
            await invalidator.DidNotReceive().InvalidateByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task CleanupForFreeTier_ZeroMachines_IsNoOp()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            await SeedFreeMachineLimitAsync(dbFactory, machineLimit: 1);

            IApiKeyCacheInvalidator invalidator = Substitute.For<IApiKeyCacheInvalidator>();
            DowngradeCleanupService service = new(repo, repo, repo, repo, repo, repo, invalidator, new NullLogger<DowngradeCleanupService>());

            await service.CleanupForFreeTierAsync(1, CancellationToken.None);

            await invalidator.DidNotReceive().InvalidateByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
    }
}
