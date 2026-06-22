// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using System.Net;
using System.Net.Http.Json;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the machine-specific alert rule endpoints
/// (<c>GET/PUT /machines/{machineId}/alert-rules</c>), including cross-tenant isolation.
/// </summary>
public sealed class MachineAlertRulesEndpointTests
{
    private static async Task<(int TenantId, int UserId, long MachineId)> SeedTenantWithMachine(DatabaseContext db, string label)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"{label} {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Team,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-{Guid.NewGuid():N}",
            Username = $"user-{Guid.NewGuid():N}@example.com",
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
            Name = $"machine-{Guid.NewGuid():N}",
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

    private static async Task<int> SeedAlertRule(DatabaseContext db, int tenantId, string name)
    {
        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = name,
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80m,
            DurationMinutes = 5,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        return await db.InsertWithInt32IdentityAsync(rule);
    }

    private static HttpClient BuildClient(FunctionalTestFactory factory, int tenantId, int userId, UserAccountRoles role = UserAccountRoles.TenantAdmin)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)role)
            .WithActiveTenant(tenantId)
            .Build();
    }

    // ========== List ==========

    [Test]
    public async Task List_ReturnsAssignedRules()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedTenantWithMachine(db, "List Tenant");

        int ruleId = await SeedAlertRule(db, tenantId, "assigned-rule");
        int otherRuleId = await SeedAlertRule(db, tenantId, "unassigned-rule");
        await db.InsertAsync(new AlertRuleMachine { AlertRuleId = ruleId, MachineId = machineId, CreatedAt = DateTimeOffset.UtcNow });

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/machines/{machineId}/alert-rules");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("assigned-rule");
        await Assert.That(body.Contains("unassigned-rule")).IsFalse();
    }

    [Test]
    public async Task List_ForeignMachine_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantAId, int userAId, _) = await SeedTenantWithMachine(db, "Tenant A");
        (_, _, long machineBId) = await SeedTenantWithMachine(db, "Tenant B");

        HttpClient clientA = BuildClient(factory, tenantAId, userAId, UserAccountRoles.Viewer);

        // Tenant A asks for Tenant B's machine.
        HttpResponseMessage response = await clientA.GetAsync($"/api/v1/machines/{machineBId}/alert-rules");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // ========== Update ==========

    [Test]
    public async Task Update_HappyPath_PersistsAndWritesAudit()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedTenantWithMachine(db, "Update Tenant");

        int ruleId = await SeedAlertRule(db, tenantId, "rule-to-assign");

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/api/v1/machines/{machineId}/alert-rules",
            new { RuleIds = new[] { ruleId } });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Assignment persisted.
        List<AlertRuleMachine> assignments = await db.AlertRuleMachines
            .Where(a => a.MachineId == machineId)
            .ToListAsync();
        await Assert.That(assignments.Count).IsEqualTo(1);
        await Assert.That(assignments[0].AlertRuleId).IsEqualTo(ruleId);

        // Audit entry written.
        List<AuditLogEntry> audits = await db.AuditLog
            .Where(a => (a.TenantId == tenantId) && (a.Action == AuditAction.MachineAlertRulesUpdated))
            .ToListAsync();
        await Assert.That(audits.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Update_ForeignMachine_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantAId, int userAId, _) = await SeedTenantWithMachine(db, "Tenant A");
        (_, _, long machineBId) = await SeedTenantWithMachine(db, "Tenant B");

        HttpClient clientA = BuildClient(factory, tenantAId, userAId);

        HttpResponseMessage response = await clientA.PutAsJsonAsync(
            $"/api/v1/machines/{machineBId}/alert-rules",
            new { RuleIds = Array.Empty<int>() });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Update_RuleFromAnotherTenant_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantAId, int userAId, long machineAId) = await SeedTenantWithMachine(db, "Tenant A");
        (int tenantBId, _, _) = await SeedTenantWithMachine(db, "Tenant B");

        // A rule that belongs to Tenant B.
        int tenantBRuleId = await SeedAlertRule(db, tenantBId, "tenant-b-rule");

        HttpClient clientA = BuildClient(factory, tenantAId, userAId);

        // Tenant A tries to assign Tenant B's rule to Tenant A's machine.
        HttpResponseMessage response = await clientA.PutAsJsonAsync(
            $"/api/v1/machines/{machineAId}/alert-rules",
            new { RuleIds = new[] { tenantBRuleId } });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        // Nothing must have been persisted for the cross-tenant rule.
        List<AlertRuleMachine> assignments = await db.AlertRuleMachines
            .Where(a => a.MachineId == machineAId)
            .ToListAsync();
        await Assert.That(assignments.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Update_NoTenantClaim_IsRejected()
    {
        using FunctionalTestFactory factory = new();
        // Authenticated user but no active tenant claim.
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).Build();

        HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/v1/machines/1/alert-rules",
            new { RuleIds = Array.Empty<int>() });

        // The TenantAdmin policy rejects before the handler runs; assert no success payload.
        await Assert.That(response.IsSuccessStatusCode).IsFalse();
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.Contains("\"success\":true")).IsFalse();
    }

    [Test]
    public async Task Update_CrossTenantIsolation_TenantBCannotTargetTenantAMachineOrRule()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantAId, _, long machineAId) = await SeedTenantWithMachine(db, "Tenant A");
        (int tenantBId, int userBId, _) = await SeedTenantWithMachine(db, "Tenant B");

        int tenantARuleId = await SeedAlertRule(db, tenantAId, "tenant-a-rule");

        HttpClient clientB = BuildClient(factory, tenantBId, userBId);

        // Tenant B cannot target Tenant A's machine (404, machine not visible to tenant B).
        HttpResponseMessage targetForeignMachine = await clientB.PutAsJsonAsync(
            $"/api/v1/machines/{machineAId}/alert-rules",
            new { RuleIds = Array.Empty<int>() });
        await Assert.That(targetForeignMachine.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // Nothing was assigned to Tenant A's machine.
        List<AlertRuleMachine> assignments = await db.AlertRuleMachines
            .Where(a => a.MachineId == machineAId)
            .ToListAsync();
        await Assert.That(assignments.Count).IsEqualTo(0);

        // And Tenant A's rule was never assignable by Tenant B.
        bool tenantARuleAssignedAnywhere = await db.AlertRuleMachines
            .AnyAsync(a => a.AlertRuleId == tenantARuleId);
        await Assert.That(tenantARuleAssignedAnywhere).IsFalse();
    }
}
