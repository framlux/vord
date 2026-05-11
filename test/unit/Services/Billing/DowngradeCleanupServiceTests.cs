// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Billing;
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
}
