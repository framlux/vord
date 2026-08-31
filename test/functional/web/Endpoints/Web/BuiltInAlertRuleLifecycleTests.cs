// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Net;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// End-to-end coverage for the built-in alert rule lifecycle. The defects these rules exist to fix
/// are sequences — provision, entitle, assign, evaluate, downgrade, re-upgrade — and no single-step
/// test observes them. Every test here drives the real services through the real database.
/// </summary>
public sealed class BuiltInAlertRuleLifecycleTests
{
    private static async Task<(int TenantId, int UserId, long MachineId)> SeedTenantAsync(
        DatabaseContext db,
        SubscriptionTier tier)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Lifecycle Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-lifecycle-user-{Guid.NewGuid():N}",
            Username = $"lifecycle-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        UserTenantRole role = new()
        {
            UserId = user.Id,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        Machine machine = new()
        {
            TenantId = tenant.Id,
            Name = $"lifecycle-machine-{Guid.NewGuid():N}",
            ApiKeyHash = Guid.NewGuid().ToString("N").PadLeft(64, '0'),
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = MachineTypes.VirtualMachine,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        return (tenant.Id, user.Id, machine.Id);
    }

    private static HttpClient BuildClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    private static async Task ProvisionAsync(FunctionalTestFactory factory, int tenantId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IBuiltInAlertRuleProvisioner provisioner = scope.ServiceProvider.GetRequiredService<IBuiltInAlertRuleProvisioner>();

        await provisioner.EnsureProvisionedAsync(tenantId, CancellationToken.None);
    }

    private static async Task<AlertRule> GetBuiltInAsync(DatabaseContext db, int tenantId, AlertMetric metric)
    {
        AlertRule? rule = await db.AlertRules
            .Where(r => (r.TenantId == tenantId) && (r.IsCustom == false) && (r.Metric == metric))
            .FirstOrDefaultAsync();

        await Assert.That(rule).IsNotNull();

        return rule!;
    }

    /// <summary>
    /// Records the machine's live state and backdates the rule's condition-state row so the
    /// duration window is already satisfied. Backdating the stored observation is how a
    /// multi-minute window is exercised without waiting on, or depending on, the wall clock.
    /// </summary>
    private static async Task SeedBreachedDiskStateAsync(DatabaseContext db, int tenantId, long machineId, int ruleId, int durationMinutes)
    {
        await db.InsertAsync(new MachineStateSummary
        {
            MachineId = machineId,
            TenantId = tenantId,
            Name = "lifecycle-machine",
            OperatingSystem = (byte)OperatingSystems.Ubuntu,
            MachineType = (byte)MachineTypes.VirtualMachine,
            MaxDiskUsagePercent = 95,
            HealthStatus = 0,
            LastSeenAt = DateTimeOffset.UtcNow,
        });

        await db.InsertAsync(new AlertConditionState
        {
            AlertRuleId = ruleId,
            MachineId = machineId,
            FirstTriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-(durationMinutes + 5)),
            LastObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });
    }

    private static async Task RunEvaluationAsync(FunctionalTestFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AlertEvaluationJob job = scope.ServiceProvider.GetRequiredService<AlertEvaluationJob>();

        await job.RunAsync(CancellationToken.None);
    }

    // --- The feature, end to end ---

    [Test]
    public async Task AssignedBuiltInRule_BreachingItsThreshold_CreatesAnAlertEvent()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedTenantAsync(db, SubscriptionTier.Pro);

        await ProvisionAsync(factory, tenantId);

        AlertRule diskRule = await GetBuiltInAsync(db, tenantId, AlertMetric.DiskUsage);
        await Assert.That(diskRule.IsEnabled).IsTrue();

        // Assignment through the rule-side endpoint is the step that makes a built-in real: the
        // evaluation lookup inner-joins AlertRuleMachines and rules are provisioned unassigned.
        HttpClient client = BuildClient(factory, tenantId, userId);
        HttpResponseMessage assignResponse = await client.PutAsJsonAsync(
            $"/api/v1/alert-rules/{diskRule.Id}/machines",
            new { MachineIds = new[] { machineId } });

        await Assert.That(assignResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await SeedBreachedDiskStateAsync(db, tenantId, machineId, diskRule.Id, diskRule.DurationMinutes);

        await RunEvaluationAsync(factory);

        int events = await db.AlertEvents
            .CountAsync(e => (e.AlertRuleId == diskRule.Id) && (e.MachineId == machineId));

        await Assert.That(events).IsEqualTo(1);
    }

    [Test]
    public async Task UnassignedBuiltInRule_BreachingItsThreshold_CreatesNoAlertEvent()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, _, long machineId) = await SeedTenantAsync(db, SubscriptionTier.Pro);

        await ProvisionAsync(factory, tenantId);

        AlertRule diskRule = await GetBuiltInAsync(db, tenantId, AlertMetric.DiskUsage);

        await SeedBreachedDiskStateAsync(db, tenantId, machineId, diskRule.Id, diskRule.DurationMinutes);

        await RunEvaluationAsync(factory);

        // Deliberate, not incidental: an unassigned rule watches nothing, and that is the decided
        // model. Coverage is the tenant's choice, which is why the rule-side assignment surface and
        // the unassigned empty state carry the feature. Anyone who makes this test fail by having an
        // unassigned rule fire has changed the product, not fixed a bug.
        int events = await db.AlertEvents
            .CountAsync(e => (e.AlertRuleId == diskRule.Id) && (e.MachineId == machineId));

        await Assert.That(events).IsEqualTo(0);
    }

    // --- Tier round trips ---

    [Test]
    public async Task FreeToProToFreeToPro_LeavesBuiltInRulesEnabled()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, _, _) = await SeedTenantAsync(db, SubscriptionTier.Free);

        await ProvisionAsync(factory, tenantId);

        int enabledWhileFree = await db.AlertRules.CountAsync(r => (r.TenantId == tenantId) && r.IsEnabled);
        await Assert.That(enabledWhileFree).IsEqualTo(0);

        await InvokeWebhookAsync(factory, h => h.HandleCheckoutCompletedAsync(tenantId, SubscriptionTier.Pro, CancellationToken.None));
        await InvokeWebhookAsync(factory, h => h.HandleSubscriptionDeletedAsync(tenantId, CancellationToken.None));
        await InvokeWebhookAsync(factory, h => h.HandleCheckoutCompletedAsync(tenantId, SubscriptionTier.Pro, CancellationToken.None));

        // The second upgrade is the one that used to fail. Provisioning short-circuits when nothing
        // is missing, so before the enable step existed a re-upgraded tenant paid for eight rules
        // that the downgrade had switched off and nothing switched back on.
        int enabled = await db.AlertRules.CountAsync(r => (r.TenantId == tenantId) && r.IsEnabled);

        await Assert.That(enabled).IsEqualTo(BuiltInAlertRuleDefinitions.All.Count);
    }

    [Test]
    public async Task ProvisioningRepeatedly_YieldsExactlyOneRulePerDefinition()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, _, _) = await SeedTenantAsync(db, SubscriptionTier.Pro);

        await ProvisionAsync(factory, tenantId);
        await ProvisionAsync(factory, tenantId);
        await ProvisionAsync(factory, tenantId);

        int count = await db.AlertRules.CountAsync(r => (r.TenantId == tenantId) && (r.IsCustom == false));

        await Assert.That(count).IsEqualTo(BuiltInAlertRuleDefinitions.All.Count);
    }

    [Test]
    public async Task CancelThenReactivate_ReEnablesBuiltInRules()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, _, _) = await SeedTenantAsync(db, SubscriptionTier.Pro);

        await ProvisionAsync(factory, tenantId);

        // Cancellation disables every rule while leaving the tier alone, so nothing but the payment
        // recovery restores them.
        await InvokeWebhookAsync(factory, h => h.HandleAccountCanceledAsync(tenantId, CancellationToken.None));

        int enabledWhileCanceled = await db.AlertRules.CountAsync(r => (r.TenantId == tenantId) && r.IsEnabled);
        await Assert.That(enabledWhileCanceled).IsEqualTo(0);

        await InvokeWebhookAsync(factory, h => h.HandlePaymentSucceededAsync(tenantId, CancellationToken.None));

        int enabled = await db.AlertRules.CountAsync(r => (r.TenantId == tenantId) && r.IsEnabled);

        await Assert.That(enabled).IsEqualTo(BuiltInAlertRuleDefinitions.All.Count);
    }

    [Test]
    public async Task TeamToProToTeam_ReEnablesCustomRules()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, _) = await SeedTenantAsync(db, SubscriptionTier.Team);

        await ProvisionAsync(factory, tenantId);

        AlertRule customRule = new()
        {
            TenantId = tenantId,
            Name = "Custom CPU rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 70,
            DurationMinutes = 15,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        customRule.Id = await db.InsertWithInt32IdentityAsync(customRule);

        // A billing-initiated downgrade is the whole of the transition: nothing else runs behind it,
        // so the handler owes the freeze as well as the tier change.
        await InvokeWebhookAsync(factory, h => h.HandleDowngradeToProAsync(tenantId, CancellationToken.None));

        AlertRule? frozen = await db.AlertRules.Where(r => r.Id == customRule.Id).FirstOrDefaultAsync();
        await Assert.That(frozen!.IsEnabled).IsFalse();

        int builtInsWhilePro = await db.AlertRules.CountAsync(r => (r.TenantId == tenantId) && (r.IsCustom == false) && r.IsEnabled);
        await Assert.That(builtInsWhilePro).IsEqualTo(BuiltInAlertRuleDefinitions.All.Count);

        await InvokeWebhookAsync(factory, h => h.HandleCheckoutCompletedAsync(tenantId, SubscriptionTier.Team, CancellationToken.None));

        AlertRule? thawed = await db.AlertRules.Where(r => r.Id == customRule.Id).FirstOrDefaultAsync();

        await Assert.That(thawed!.IsEnabled).IsTrue();
    }

    private static async Task InvokeWebhookAsync(FunctionalTestFactory factory, Func<IBillingWebhookHandler, Task> action)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IBillingWebhookHandler handler = scope.ServiceProvider.GetRequiredService<IBillingWebhookHandler>();

        await action(handler);
    }
}
