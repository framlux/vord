// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for alert rule-related methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class AlertRuleRepositoryTests
{
    /// <summary>
    /// Seeds a user and tenant required by most alert rule tests.
    /// Returns the generated IDs for downstream use.
    /// </summary>
    private static async Task<(int userId, int tenantId)> SeedUserAndTenantAsync(TestDatabaseFactory dbFactory)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        return (userId, tenantId);
    }

    // ========== CreateAlertRuleAsync tests ==========

    [Test]
    public async Task CreateAlertRuleAsync_ValidRule_ReturnsRuleWithId()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);

        AlertRule result = await repo.CreateAlertRuleAsync(rule);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsNotEqualTo(0);
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.Metric).IsEqualTo(AlertMetric.CpuUsage);
        await Assert.That(result.IsEnabled).IsTrue();
    }

    [Test]
    public async Task CreateAlertRuleAsync_NullInput_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(async () => await repo.CreateAlertRuleAsync(null!))
            .ThrowsException()
            .And
            .IsTypeOf<ArgumentNullException>();
    }

    // ========== GetAlertRulesForTenantAsync tests ==========

    [Test]
    public async Task GetAlertRulesForTenantAsync_NoRules_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<AlertRule> result = await repo.GetAlertRulesForTenantAsync(99999);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetAlertRulesForTenantAsync_MultipleRules_ReturnsOrderedByName()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule ruleC = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
        ruleC.Name = "Charlie Rule";
        await dbFactory.Context.InsertWithInt32IdentityAsync(ruleC);

        AlertRule ruleA = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
        ruleA.Name = "Alpha Rule";
        await dbFactory.Context.InsertWithInt32IdentityAsync(ruleA);

        AlertRule ruleB = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
        ruleB.Name = "Bravo Rule";
        await dbFactory.Context.InsertWithInt32IdentityAsync(ruleB);

        List<AlertRule> result = await repo.GetAlertRulesForTenantAsync(tenantId);

        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result[0].Name).IsEqualTo("Alpha Rule");
        await Assert.That(result[1].Name).IsEqualTo("Bravo Rule");
        await Assert.That(result[2].Name).IsEqualTo("Charlie Rule");
    }

    [Test]
    public async Task GetAlertRulesForTenantAsync_DifferentTenant_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
        await dbFactory.Context.InsertWithInt32IdentityAsync(rule);

        List<AlertRule> result = await repo.GetAlertRulesForTenantAsync(99999);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== GetAlertRuleByIdAsync (tenant-scoped) tests ==========

    [Test]
    public async Task GetAlertRuleByIdAsync_TenantScoped_Found_ReturnsRule()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
        rule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(rule);

        AlertRule? result = await repo.GetAlertRuleByIdAsync(rule.Id, tenantId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(rule.Id);
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
    }

    [Test]
    public async Task GetAlertRuleByIdAsync_TenantScoped_NotFound_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        AlertRule? result = await repo.GetAlertRuleByIdAsync(99999, 1);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetAlertRuleByIdAsync_TenantScoped_WrongTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
        rule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(rule);

        AlertRule? result = await repo.GetAlertRuleByIdAsync(rule.Id, 99999);

        await Assert.That(result).IsNull();
    }

    // ========== GetAlertRuleByIdAsync (unscoped) tests ==========

    [Test]
    public async Task GetAlertRuleByIdAsync_Unscoped_Found_ReturnsRule()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
        rule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(rule);

        AlertRule? result = await repo.GetAlertRuleByIdAsync(rule.Id);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(rule.Id);
    }

    [Test]
    public async Task GetAlertRuleByIdAsync_Unscoped_NotFound_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        AlertRule? result = await repo.GetAlertRuleByIdAsync(99999);

        await Assert.That(result).IsNull();
    }

    // ========== UpdateAlertRuleAsync tests ==========

    [Test]
    public async Task UpdateAlertRuleAsync_ValidUpdate_ChangesFields()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule rule = TestDataBuilder.BuildAlertRule(
            tenantId: tenantId,
            createdByUserId: userId,
            threshold: 80m,
            severity: AlertSeverity.Warning,
            isEnabled: true);
        rule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(rule);

        await repo.UpdateAlertRuleAsync(
            ruleId: rule.Id,
            tenantId: tenantId,
            name: "Updated Rule",
            description: "Updated description",
            threshold: 95m,
            durationMinutes: 10,
            severity: AlertSeverity.Critical,
            isEnabled: false,
            notifyEmail: true,
            notifyWebhook: true);

        AlertRule? updated = await repo.GetAlertRuleByIdAsync(rule.Id, tenantId);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Name).IsEqualTo("Updated Rule");
        await Assert.That(updated.Description).IsEqualTo("Updated description");
        await Assert.That(updated.Threshold).IsEqualTo(95m);
        await Assert.That(updated.DurationMinutes).IsEqualTo(10);
        await Assert.That(updated.Severity).IsEqualTo(AlertSeverity.Critical);
        await Assert.That(updated.IsEnabled).IsFalse();
        await Assert.That(updated.NotifyEmail).IsTrue();
        await Assert.That(updated.NotifyWebhook).IsTrue();
        await Assert.That(updated.UpdatedAt).IsNotEqualTo(default(DateTimeOffset));
    }

    [Test]
    public async Task UpdateAlertRuleAsync_WrongTenant_DoesNotModifyRule()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
        rule.Name = "Original Name";
        rule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(rule);

        // Attempt update with a different tenant ID.
        await repo.UpdateAlertRuleAsync(
            ruleId: rule.Id,
            tenantId: 99999,
            name: "Should Not Change",
            description: null,
            threshold: 50m,
            durationMinutes: 5,
            severity: AlertSeverity.Info,
            isEnabled: false,
            notifyEmail: false,
            notifyWebhook: false);

        AlertRule? unchanged = await repo.GetAlertRuleByIdAsync(rule.Id, tenantId);

        await Assert.That(unchanged).IsNotNull();
        await Assert.That(unchanged!.Name).IsEqualTo("Original Name");
    }

    // ========== DeleteAlertRuleAsync tests ==========

    [Test]
    public async Task DeleteAlertRuleAsync_ExistingRule_ReturnsOneAndRemovesRule()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
        rule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(rule);

        int deleted = await repo.DeleteAlertRuleAsync(rule.Id, tenantId);

        await Assert.That(deleted).IsEqualTo(1);

        AlertRule? result = await repo.GetAlertRuleByIdAsync(rule.Id, tenantId);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task DeleteAlertRuleAsync_WrongTenant_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
        rule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(rule);

        int deleted = await repo.DeleteAlertRuleAsync(rule.Id, 99999);

        await Assert.That(deleted).IsEqualTo(0);

        // Confirm the rule still exists.
        AlertRule? result = await repo.GetAlertRuleByIdAsync(rule.Id, tenantId);

        await Assert.That(result).IsNotNull();
    }

    [Test]
    public async Task DeleteAlertRuleAsync_NonExistentRule_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        int deleted = await repo.DeleteAlertRuleAsync(99999, 1);

        await Assert.That(deleted).IsEqualTo(0);
    }

    // ========== GetEnabledAlertRulesAsync tests ==========

    [Test]
    public async Task GetEnabledAlertRulesAsync_OnlyEnabledRules_ReturnsEnabledOnly()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule enabled = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId, isEnabled: true);
        enabled.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(enabled);

        AlertRule disabled = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId, isEnabled: false);
        await dbFactory.Context.InsertWithInt32IdentityAsync(disabled);

        List<AlertRule> result = await repo.GetEnabledAlertRulesAsync();

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Id).IsEqualTo(enabled.Id);
        await Assert.That(result[0].IsEnabled).IsTrue();
    }

    [Test]
    public async Task GetEnabledAlertRulesAsync_AcrossMultipleTenants_ReturnsAll()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId1, int tenantId1) = await SeedUserAndTenantAsync(dbFactory);
        (int userId2, int tenantId2) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule rule1 = TestDataBuilder.BuildAlertRule(tenantId: tenantId1, createdByUserId: userId1, isEnabled: true);
        await dbFactory.Context.InsertWithInt32IdentityAsync(rule1);

        AlertRule rule2 = TestDataBuilder.BuildAlertRule(tenantId: tenantId2, createdByUserId: userId2, isEnabled: true);
        await dbFactory.Context.InsertWithInt32IdentityAsync(rule2);

        List<AlertRule> result = await repo.GetEnabledAlertRulesAsync();

        await Assert.That(result.Count).IsEqualTo(2);
    }

    // ========== CountAlertRulesForTenantAsync tests ==========

    [Test]
    public async Task CountAlertRulesForTenantAsync_NoRules_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        int count = await repo.CountAlertRulesForTenantAsync(99999);

        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task CountAlertRulesForTenantAsync_MultipleRules_ReturnsCorrectCount()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        for (int i = 0; i < 4; i++)
        {
            AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId);
            await dbFactory.Context.InsertWithInt32IdentityAsync(rule);
        }

        int count = await repo.CountAlertRulesForTenantAsync(tenantId);

        await Assert.That(count).IsEqualTo(4);
    }

    // ========== DisableAlertRulesForTenantAsync tests ==========

    [Test]
    public async Task DisableAlertRulesForTenantAsync_AllRules_DisablesAll()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule customRule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId, isCustom: true, isEnabled: true);
        await dbFactory.Context.InsertWithInt32IdentityAsync(customRule);

        AlertRule defaultRule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId, isCustom: false, isEnabled: true);
        await dbFactory.Context.InsertWithInt32IdentityAsync(defaultRule);

        int disabled = await repo.DisableAlertRulesForTenantAsync(tenantId, customOnly: false);

        await Assert.That(disabled).IsEqualTo(2);

        List<AlertRule> rules = await repo.GetAlertRulesForTenantAsync(tenantId);
        bool allDisabled = true;
        foreach (AlertRule r in rules)
        {
            if (r.IsEnabled)
            {
                allDisabled = false;
            }
        }

        await Assert.That(allDisabled).IsTrue();
    }

    [Test]
    public async Task DisableAlertRulesForTenantAsync_CustomOnly_DisablesOnlyCustomRules()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule customRule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId, isCustom: true, isEnabled: true);
        customRule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(customRule);

        AlertRule defaultRule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId, isCustom: false, isEnabled: true);
        defaultRule.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(defaultRule);

        int disabled = await repo.DisableAlertRulesForTenantAsync(tenantId, customOnly: true);

        await Assert.That(disabled).IsEqualTo(1);

        AlertRule? updatedCustom = await repo.GetAlertRuleByIdAsync(customRule.Id, tenantId);
        AlertRule? updatedDefault = await repo.GetAlertRuleByIdAsync(defaultRule.Id, tenantId);

        await Assert.That(updatedCustom).IsNotNull();
        await Assert.That(updatedCustom!.IsEnabled).IsFalse();

        await Assert.That(updatedDefault).IsNotNull();
        await Assert.That(updatedDefault!.IsEnabled).IsTrue();
    }

    [Test]
    public async Task DisableAlertRulesForTenantAsync_AlreadyDisabled_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule disabledRule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId, isEnabled: false);
        await dbFactory.Context.InsertWithInt32IdentityAsync(disabledRule);

        int disabled = await repo.DisableAlertRulesForTenantAsync(tenantId, customOnly: false);

        await Assert.That(disabled).IsEqualTo(0);
    }

    // ========== DisableCustomAlertRulesForTenantAsync tests ==========

    [Test]
    public async Task DisableCustomAlertRulesForTenantAsync_DisablesOnlyCustom()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule customEnabled = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId, isCustom: true, isEnabled: true);
        customEnabled.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(customEnabled);

        AlertRule defaultEnabled = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId, isCustom: false, isEnabled: true);
        defaultEnabled.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(defaultEnabled);

        int disabled = await repo.DisableCustomAlertRulesForTenantAsync(tenantId);

        await Assert.That(disabled).IsEqualTo(1);

        AlertRule? custom = await repo.GetAlertRuleByIdAsync(customEnabled.Id, tenantId);
        AlertRule? defaultRule = await repo.GetAlertRuleByIdAsync(defaultEnabled.Id, tenantId);

        await Assert.That(custom).IsNotNull();
        await Assert.That(custom!.IsEnabled).IsFalse();

        await Assert.That(defaultRule).IsNotNull();
        await Assert.That(defaultRule!.IsEnabled).IsTrue();
    }

    // ========== HasDefaultAlertRulesAsync tests ==========

    [Test]
    public async Task HasDefaultAlertRulesAsync_WithDefaultRules_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule defaultRule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId, isCustom: false);
        await dbFactory.Context.InsertWithInt32IdentityAsync(defaultRule);

        bool result = await repo.HasDefaultAlertRulesAsync(tenantId);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task HasDefaultAlertRulesAsync_OnlyCustomRules_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AlertRule customRule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId, isCustom: true);
        await dbFactory.Context.InsertWithInt32IdentityAsync(customRule);

        bool result = await repo.HasDefaultAlertRulesAsync(tenantId);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task HasDefaultAlertRulesAsync_NoRulesAtAll_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        bool result = await repo.HasDefaultAlertRulesAsync(99999);

        await Assert.That(result).IsFalse();
    }

    // ========== InsertAlertRulesAsync tests ==========

    [Test]
    public async Task InsertAlertRulesAsync_BatchInsert_InsertsAllRules()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        List<AlertRule> rules = new()
        {
            TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId, metric: AlertMetric.CpuUsage),
            TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId, metric: AlertMetric.MemoryUsage),
            TestDataBuilder.BuildAlertRule(tenantId: tenantId, createdByUserId: userId, metric: AlertMetric.DiskUsage),
        };

        await repo.InsertAlertRulesAsync(rules);

        int count = await repo.CountAlertRulesForTenantAsync(tenantId);

        await Assert.That(count).IsEqualTo(3);
    }

    [Test]
    public async Task InsertAlertRulesAsync_EmptyCollection_InsertsNothing()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        List<AlertRule> emptyList = new();

        await repo.InsertAlertRulesAsync(emptyList);

        int count = await repo.CountAlertRulesForTenantAsync(tenantId);

        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task InsertAlertRulesAsync_NullInput_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IAlertRuleRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(async () => await repo.InsertAlertRulesAsync(null!))
            .ThrowsException()
            .And
            .IsTypeOf<ArgumentNullException>();
    }
}
