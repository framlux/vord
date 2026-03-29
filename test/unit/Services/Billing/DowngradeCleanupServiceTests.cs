// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
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
    // --- CleanupForProTierAsync ---

    [Test]
    public async Task CleanupForProTierAsync_DisablesCustomOidcConfig()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 1, isEnabled: true);
        oidcConfig.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(oidcConfig);

        DowngradeCleanupService service = new(
            dbFactory.Context, new NullLogger<DowngradeCleanupService>());

        await service.CleanupForProTierAsync(1, CancellationToken.None);

        TenantOidcConfiguration? updated = await dbFactory.Context.TenantOidcConfigurations
            .FirstOrDefaultAsync(c => c.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.IsEnabled).IsEqualTo(false);
    }

    [Test]
    public async Task CleanupForProTierAsync_DisablesCustomAlertRules()
    {
        using TestDatabaseFactory dbFactory = new();
        AlertRule customRule = TestDataBuilder.BuildAlertRule(
            tenantId: 1, isCustom: true, isEnabled: true);
        customRule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(customRule);

        DowngradeCleanupService service = new(
            dbFactory.Context, new NullLogger<DowngradeCleanupService>());

        await service.CleanupForProTierAsync(1, CancellationToken.None);

        AlertRule? updated = await dbFactory.Context.AlertRules
            .FirstOrDefaultAsync(r => r.Id == customRule.Id);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.IsEnabled).IsEqualTo(false);
    }

    [Test]
    public async Task CleanupForProTierAsync_KeepsDefaultRulesEnabled()
    {
        using TestDatabaseFactory dbFactory = new();

        // Default (system) rule should remain enabled
        AlertRule defaultRule = TestDataBuilder.BuildAlertRule(
            tenantId: 1, isCustom: false, isEnabled: true);
        defaultRule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(defaultRule);

        // Custom rule should be disabled
        AlertRule customRule = TestDataBuilder.BuildAlertRule(
            tenantId: 1, isCustom: true, isEnabled: true);
        customRule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(customRule);

        DowngradeCleanupService service = new(
            dbFactory.Context, new NullLogger<DowngradeCleanupService>());

        await service.CleanupForProTierAsync(1, CancellationToken.None);

        AlertRule? updatedDefault = await dbFactory.Context.AlertRules
            .FirstOrDefaultAsync(r => r.Id == defaultRule.Id);
        await Assert.That(updatedDefault).IsNotNull();
        await Assert.That(updatedDefault!.IsEnabled).IsEqualTo(true);

        AlertRule? updatedCustom = await dbFactory.Context.AlertRules
            .FirstOrDefaultAsync(r => r.Id == customRule.Id);
        await Assert.That(updatedCustom).IsNotNull();
        await Assert.That(updatedCustom!.IsEnabled).IsEqualTo(false);
    }

    [Test]
    public async Task CleanupForProTierAsync_DoesNotAffectOtherTenants()
    {
        using TestDatabaseFactory dbFactory = new();

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

        DowngradeCleanupService service = new(
            dbFactory.Context, new NullLogger<DowngradeCleanupService>());

        await service.CleanupForProTierAsync(1, CancellationToken.None);

        TenantOidcConfiguration? tenant2Oidc = await dbFactory.Context.TenantOidcConfigurations
            .FirstOrDefaultAsync(c => c.TenantId == 2);
        await Assert.That(tenant2Oidc).IsNotNull();
        await Assert.That(tenant2Oidc!.IsEnabled).IsEqualTo(true);

        AlertRule? tenant2Rule = await dbFactory.Context.AlertRules
            .FirstOrDefaultAsync(r => r.TenantId == 2);
        await Assert.That(tenant2Rule).IsNotNull();
        await Assert.That(tenant2Rule!.IsEnabled).IsEqualTo(true);
    }

    [Test]
    public async Task CleanupForProTierAsync_AlreadyDisabledOidc_NoError()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 1, isEnabled: false);
        oidcConfig.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(oidcConfig);

        DowngradeCleanupService service = new(
            dbFactory.Context, new NullLogger<DowngradeCleanupService>());

        // Should complete without error even when nothing needs disabling
        await service.CleanupForProTierAsync(1, CancellationToken.None);

        TenantOidcConfiguration? updated = await dbFactory.Context.TenantOidcConfigurations
            .FirstOrDefaultAsync(c => c.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.IsEnabled).IsEqualTo(false);
    }

    // --- CleanupForFreeTierAsync ---

    [Test]
    public async Task CleanupForFreeTierAsync_DisablesAllAlertRules()
    {
        using TestDatabaseFactory dbFactory = new();

        AlertRule defaultRule = TestDataBuilder.BuildAlertRule(
            tenantId: 1, isCustom: false, isEnabled: true);
        defaultRule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(defaultRule);

        AlertRule customRule = TestDataBuilder.BuildAlertRule(
            tenantId: 1, isCustom: true, isEnabled: true);
        customRule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(customRule);

        DowngradeCleanupService service = new(
            dbFactory.Context, new NullLogger<DowngradeCleanupService>());

        await service.CleanupForFreeTierAsync(1, CancellationToken.None);

        List<AlertRule> rules = await dbFactory.Context.AlertRules
            .Where(r => r.TenantId == 1)
            .ToListAsync();
        await Assert.That(rules.Count).IsEqualTo(2);
        await Assert.That(rules.All(r => r.IsEnabled == false)).IsEqualTo(true);
    }

    [Test]
    public async Task CleanupForFreeTierAsync_DisablesCustomOidcConfig()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 1, isEnabled: true);
        oidcConfig.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(oidcConfig);

        DowngradeCleanupService service = new(
            dbFactory.Context, new NullLogger<DowngradeCleanupService>());

        await service.CleanupForFreeTierAsync(1, CancellationToken.None);

        TenantOidcConfiguration? updated = await dbFactory.Context.TenantOidcConfigurations
            .FirstOrDefaultAsync(c => c.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.IsEnabled).IsEqualTo(false);
    }

    [Test]
    public async Task CleanupForFreeTierAsync_DisablesWebhookEndpoints()
    {
        using TestDatabaseFactory dbFactory = new();
        WebhookEndpoint webhook = TestDataBuilder.BuildWebhookEndpoint(tenantId: 1, isEnabled: true);
        webhook.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);

        DowngradeCleanupService service = new(
            dbFactory.Context, new NullLogger<DowngradeCleanupService>());

        await service.CleanupForFreeTierAsync(1, CancellationToken.None);

        WebhookEndpoint? updated = await dbFactory.Context.WebhookEndpoints
            .FirstOrDefaultAsync(w => w.Id == webhook.Id);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.IsEnabled).IsEqualTo(false);
    }

    [Test]
    public async Task CleanupForFreeTierAsync_DoesNotAffectOtherTenants()
    {
        using TestDatabaseFactory dbFactory = new();

        // Tenant 2 resources — should remain untouched
        TenantOidcConfiguration oidcTenant2 = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 2, isEnabled: true);
        oidcTenant2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(oidcTenant2);

        AlertRule ruleTenant2 = TestDataBuilder.BuildAlertRule(
            tenantId: 2, isCustom: true, isEnabled: true);
        ruleTenant2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(ruleTenant2);

        WebhookEndpoint webhookTenant2 = TestDataBuilder.BuildWebhookEndpoint(tenantId: 2, isEnabled: true);
        webhookTenant2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(webhookTenant2);

        DowngradeCleanupService service = new(
            dbFactory.Context, new NullLogger<DowngradeCleanupService>());

        await service.CleanupForFreeTierAsync(1, CancellationToken.None);

        TenantOidcConfiguration? tenant2Oidc = await dbFactory.Context.TenantOidcConfigurations
            .FirstOrDefaultAsync(c => c.TenantId == 2);
        await Assert.That(tenant2Oidc).IsNotNull();
        await Assert.That(tenant2Oidc!.IsEnabled).IsEqualTo(true);

        AlertRule? tenant2Rule = await dbFactory.Context.AlertRules
            .FirstOrDefaultAsync(r => r.TenantId == 2);
        await Assert.That(tenant2Rule).IsNotNull();
        await Assert.That(tenant2Rule!.IsEnabled).IsEqualTo(true);

        WebhookEndpoint? tenant2Webhook = await dbFactory.Context.WebhookEndpoints
            .FirstOrDefaultAsync(w => w.TenantId == 2);
        await Assert.That(tenant2Webhook).IsNotNull();
        await Assert.That(tenant2Webhook!.IsEnabled).IsEqualTo(true);
    }

    [Test]
    public async Task CleanupForFreeTierAsync_NoResources_CompletesWithoutError()
    {
        using TestDatabaseFactory dbFactory = new();
        DowngradeCleanupService service = new(
            dbFactory.Context, new NullLogger<DowngradeCleanupService>());

        // Should not throw when no resources exist for the tenant
        await service.CleanupForFreeTierAsync(1, CancellationToken.None);
    }

    [Test]
    public async Task CleanupForFreeTierAsync_AlreadyDisabledWebhook_NoError()
    {
        using TestDatabaseFactory dbFactory = new();
        WebhookEndpoint webhook = TestDataBuilder.BuildWebhookEndpoint(tenantId: 1, isEnabled: false);
        webhook.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);

        DowngradeCleanupService service = new(
            dbFactory.Context, new NullLogger<DowngradeCleanupService>());

        // Should complete without error when webhook is already disabled
        await service.CleanupForFreeTierAsync(1, CancellationToken.None);

        WebhookEndpoint? updated = await dbFactory.Context.WebhookEndpoints
            .FirstOrDefaultAsync(w => w.Id == webhook.Id);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.IsEnabled).IsEqualTo(false);
    }
}
