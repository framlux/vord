// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

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

            DowngradeCleanupService service = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());

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

            DowngradeCleanupService service = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());

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

            DowngradeCleanupService service = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());

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

            DowngradeCleanupService service = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());

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

            DowngradeCleanupService service = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());

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

            DowngradeCleanupService service = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());

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

            DowngradeCleanupService service = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());

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

            DowngradeCleanupService service = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());

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

            DowngradeCleanupService service = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());

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
            DowngradeCleanupService service = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());

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

            DowngradeCleanupService service = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());

            // Should complete without error when integration is already disabled
            await service.CleanupForFreeTierAsync(1, CancellationToken.None);

            IntegrationEndpoint? updated = await dbFactory.Context.IntegrationEndpoints
                .FirstOrDefaultAsync(i => i.Id == integration.Id);
            await Assert.That(updated).IsNotNull();
            await Assert.That(updated!.IsEnabled).IsFalse();
        }
    }

    // --- Machines are never touched by the Free cleanup ---

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

    /// <summary>
    /// A drop to Free is reached by a dunning cancellation the customer never asked for, so it must not
    /// cost them a machine. Running the cleanup twice is the duplicate-webhook case: Stripe redelivers,
    /// and a redelivery must be as harmless as the first delivery.
    /// </summary>
    [Test]
    public async Task CleanupForFreeTier_OverTheFreeLimit_LeavesEveryMachineItsSummaryAndItsKey()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            await SeedFreeMachineLimitAsync(dbFactory, machineLimit: 1);

            Dictionary<long, string> keyHashById = [];
            for (int i = 0; i < 4; i++)
            {
                Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
                long id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
                keyHashById[id] = machine.ApiKeyHash;
                await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: id, tenantId: 1));
            }

            DowngradeCleanupService service = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());

            await service.CleanupForFreeTierAsync(1, CancellationToken.None);
            await service.CleanupForFreeTierAsync(1, CancellationToken.None);

            List<Machine> machines = await dbFactory.Context.Machines.Where(m => m.TenantId == 1).ToListAsync();
            await Assert.That(machines.Count).IsEqualTo(4);
            foreach (Machine machine in machines)
            {
                await Assert.That(machine.IsDeleted).IsFalse();
                await Assert.That(machine.ApiKeyHash).IsEqualTo(keyHashById[machine.Id]);
            }

            await Assert.That(await dbFactory.Context.MachineStateSummaries.CountAsync(s => s.TenantId == 1)).IsEqualTo(4);
            await Assert.That(await dbFactory.Context.AuditLog.CountAsync(a => a.Action == AuditAction.MachineDeleted)).IsEqualTo(0);
        }
    }

    /// <summary>
    /// The cleanup must commit or roll back with the transaction that changes the tier. Run on its own
    /// connection state it would survive the rollback of that transaction, and the tenant would lose its
    /// alerting, SSO and integrations for a tier change that never happened.
    /// </summary>
    [Test]
    public async Task CleanupForFreeTier_RollsBackWithTheCallersTransaction()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            await dbFactory.Context.InsertAsync(TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 1, isEnabled: true));
            AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: 1, isCustom: false, isEnabled: true);
            rule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(rule);
            IntegrationEndpoint integration = TestDataBuilder.BuildIntegrationEndpoint(tenantId: 1, isEnabled: true);
            integration.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

            DowngradeCleanupService service = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());

            using (IDatabaseTransaction transaction = await repo.BeginTransactionAsync(CancellationToken.None))
            {
                await service.CleanupForFreeTierAsync(1, CancellationToken.None);
            }

            await Assert.That((await dbFactory.Context.TenantOidcConfigurations.FirstAsync(c => c.TenantId == 1)).IsEnabled).IsTrue();
            await Assert.That((await dbFactory.Context.AlertRules.FirstAsync(r => r.Id == rule.Id)).IsEnabled).IsTrue();
            await Assert.That((await dbFactory.Context.IntegrationEndpoints.FirstAsync(i => i.Id == integration.Id)).IsEnabled).IsTrue();
        }
    }

    [Test]
    public async Task CleanupForProTier_RollsBackWithTheCallersTransaction()
    {
        (DatabaseRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            await dbFactory.Context.InsertAsync(TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 1, isEnabled: true));
            AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: 1, isCustom: true, isEnabled: true);
            rule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(rule);
            DowngradeCleanupService service = new(repo, repo, repo, new NullLogger<DowngradeCleanupService>());

            using (IDatabaseTransaction transaction = await repo.BeginTransactionAsync(CancellationToken.None))
            {
                await service.CleanupForProTierAsync(1, CancellationToken.None);
            }

            await Assert.That((await dbFactory.Context.TenantOidcConfigurations.FirstAsync(c => c.TenantId == 1)).IsEnabled).IsTrue();
            await Assert.That((await dbFactory.Context.AlertRules.FirstAsync(r => r.Id == rule.Id)).IsEnabled).IsTrue();
        }
    }
}
