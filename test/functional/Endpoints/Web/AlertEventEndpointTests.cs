// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for alert event endpoints.
/// </summary>
public sealed class AlertEventEndpointTests
{
    private static async Task<(int TenantId, int UserId, int RuleId)> SeedAlertEventEnvironment(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Pro)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Event Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            MachineLimit = tier == SubscriptionTier.Free ? 3 : null,
            RetentionDays = 30,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-event-user-{Guid.NewGuid():N}",
            Username = $"eventuser-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
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

        AlertRule alertRule = new()
        {
            TenantId = tenant.Id,
            Name = "Test Rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = true,
            CreatedByUserId = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        alertRule.Id = await db.InsertWithInt32IdentityAsync(alertRule);

        return (tenant.Id, user.Id, alertRule.Id);
    }

    private static HttpClient BuildClient(
        FunctionalTestFactory factory,
        int tenantId,
        int userId,
        UserAccountRoles clientRole = UserAccountRoles.TenantAdmin)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)clientRole)
            .WithActiveTenant(tenantId)
            .Build();
    }

    // --- ListAlertEvents Tests ---

    [Test]
    public async Task ListAlertEvents_NoTenant_Returns403()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-events");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task ListAlertEvents_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, _) = await SeedAlertEventEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-events");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task ListAlertEvents_Empty_ReturnsEmptyPaginatedResponse()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, _) = await SeedAlertEventEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-events");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalCount\":0");
        await Assert.That(body).Contains("\"items\":[]");
    }

    [Test]
    public async Task ListAlertEvents_WithEvents_ReturnsPaginatedResults()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, int ruleId) = await SeedAlertEventEnvironment(db);

        for (int i = 0; i < 3; i++)
        {
            AlertEvent evt = new()
            {
                AlertRuleId = ruleId,
                TenantId = tenantId,
                MachineId = i + 1,
                Severity = AlertSeverity.Warning,
                Message = $"Alert event {i}",
                Status = AlertEventStatus.Triggered,
                TriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-i),
            };
            await db.InsertWithInt64IdentityAsync(evt);
        }

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-events");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalCount\":3");
        await Assert.That(body).Contains("Alert event 0");
        await Assert.That(body).Contains("Alert event 1");
        await Assert.That(body).Contains("Alert event 2");
    }

    [Test]
    public async Task ListAlertEvents_FilterByStatus_Triggered_ReturnsOnlyTriggered()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, int ruleId) = await SeedAlertEventEnvironment(db);

        AlertEvent triggered = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 1,
            Severity = AlertSeverity.Warning,
            Message = "Triggered event",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt64IdentityAsync(triggered);

        AlertEvent acknowledged = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 2,
            Severity = AlertSeverity.Warning,
            Message = "Acknowledged event",
            Status = AlertEventStatus.Acknowledged,
            TriggeredAt = DateTimeOffset.UtcNow,
            AcknowledgedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt64IdentityAsync(acknowledged);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-events?Status=Triggered");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalCount\":1");
        await Assert.That(body).Contains("Triggered event");
        await Assert.That(body.Contains("Acknowledged event")).IsFalse();
    }

    [Test]
    public async Task ListAlertEvents_FilterBySeverity_Critical_ReturnsOnlyCritical()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, int ruleId) = await SeedAlertEventEnvironment(db);

        AlertEvent critical = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 1,
            Severity = AlertSeverity.Critical,
            Message = "Critical event",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt64IdentityAsync(critical);

        AlertEvent warning = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 2,
            Severity = AlertSeverity.Warning,
            Message = "Warning event",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt64IdentityAsync(warning);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-events?Severity=Critical");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalCount\":1");
        await Assert.That(body).Contains("Critical event");
        await Assert.That(body.Contains("Warning event")).IsFalse();
    }

    [Test]
    public async Task ListAlertEvents_CombinedFilters_StatusAndSeverity()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, int ruleId) = await SeedAlertEventEnvironment(db);

        AlertEvent match = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 1,
            Severity = AlertSeverity.Critical,
            Message = "Matching event",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt64IdentityAsync(match);

        AlertEvent wrongStatus = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 2,
            Severity = AlertSeverity.Critical,
            Message = "Wrong status",
            Status = AlertEventStatus.Acknowledged,
            TriggeredAt = DateTimeOffset.UtcNow,
            AcknowledgedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt64IdentityAsync(wrongStatus);

        AlertEvent wrongSeverity = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 3,
            Severity = AlertSeverity.Warning,
            Message = "Wrong severity",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt64IdentityAsync(wrongSeverity);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-events?Status=Triggered&Severity=Critical");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalCount\":1");
        await Assert.That(body).Contains("Matching event");
    }

    [Test]
    public async Task ListAlertEvents_InvalidPageSize_DefaultsTo25()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, _) = await SeedAlertEventEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-events?PageSize=0");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"pageSize\":25");
    }

    [Test]
    public async Task ListAlertEvents_PageSizeExceedsMax_DefaultsTo25()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, _) = await SeedAlertEventEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-events?PageSize=500");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"pageSize\":25");
    }

    [Test]
    public async Task ListAlertEvents_PageBelowOne_DefaultsToOne()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, _) = await SeedAlertEventEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-events?Page=-1");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"page\":1");
    }

    [Test]
    public async Task ListAlertEvents_OrderedByTriggeredAtDescending()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, int ruleId) = await SeedAlertEventEnvironment(db);

        AlertEvent oldest = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 1,
            Severity = AlertSeverity.Warning,
            Message = "Oldest event",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow.AddHours(-2),
        };
        await db.InsertWithInt64IdentityAsync(oldest);

        AlertEvent newest = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 2,
            Severity = AlertSeverity.Warning,
            Message = "Newest event",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt64IdentityAsync(newest);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-events");

        string body = await response.Content.ReadAsStringAsync();
        int newestIndex = body.IndexOf("Newest event", StringComparison.Ordinal);
        int oldestIndex = body.IndexOf("Oldest event", StringComparison.Ordinal);
        await Assert.That(newestIndex < oldestIndex).IsTrue();
    }

    // --- AcknowledgeAlertEvent Tests ---

    [Test]
    public async Task AcknowledgeEvent_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, _) = await SeedAlertEventEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.MachineAdmin);

        HttpResponseMessage response = await client.PostAsync("/api/v1/alert-events/1/acknowledge", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task AcknowledgeEvent_NonexistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, _) = await SeedAlertEventEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.MachineAdmin);

        HttpResponseMessage response = await client.PostAsync("/api/v1/alert-events/99999/acknowledge", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task AcknowledgeEvent_WrongTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1, int ruleId1) = await SeedAlertEventEnvironment(db);
        (int tenantId2, int userId2, _) = await SeedAlertEventEnvironment(db);

        AlertEvent evt = new()
        {
            AlertRuleId = ruleId1,
            TenantId = tenantId1,
            MachineId = 1,
            Severity = AlertSeverity.Warning,
            Message = "Tenant1 event",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        evt.Id = await db.InsertWithInt64IdentityAsync(evt);

        HttpClient client = BuildClient(factory, tenantId2, userId2, UserAccountRoles.MachineAdmin);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/alert-events/{evt.Id}/acknowledge", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task AcknowledgeEvent_AlreadyAcknowledged_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, int ruleId) = await SeedAlertEventEnvironment(db);

        AlertEvent evt = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 1,
            Severity = AlertSeverity.Warning,
            Message = "Already ack",
            Status = AlertEventStatus.Acknowledged,
            TriggeredAt = DateTimeOffset.UtcNow,
            AcknowledgedAt = DateTimeOffset.UtcNow,
        };
        evt.Id = await db.InsertWithInt64IdentityAsync(evt);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.MachineAdmin);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/alert-events/{evt.Id}/acknowledge", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Only triggered alerts can be acknowledged");
    }

    [Test]
    public async Task AcknowledgeEvent_Resolved_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, int ruleId) = await SeedAlertEventEnvironment(db);

        AlertEvent evt = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 1,
            Severity = AlertSeverity.Warning,
            Message = "Resolved",
            Status = AlertEventStatus.Resolved,
            TriggeredAt = DateTimeOffset.UtcNow,
            ResolvedAt = DateTimeOffset.UtcNow,
        };
        evt.Id = await db.InsertWithInt64IdentityAsync(evt);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.MachineAdmin);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/alert-events/{evt.Id}/acknowledge", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task AcknowledgeEvent_Triggered_SetsAcknowledged()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, int ruleId) = await SeedAlertEventEnvironment(db);

        AlertEvent evt = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 1,
            Severity = AlertSeverity.Critical,
            Message = "Ack me",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        evt.Id = await db.InsertWithInt64IdentityAsync(evt);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.MachineAdmin);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/alert-events/{evt.Id}/acknowledge", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");

        AlertEvent? updated = await db.AlertEvents.FirstOrDefaultAsync(e => e.Id == evt.Id);
        await Assert.That(updated!.Status).IsEqualTo(AlertEventStatus.Acknowledged);
        await Assert.That(updated.AcknowledgedAt.HasValue).IsTrue();
    }

    // --- WS-4: AcknowledgedByUserId Tests ---

    [Test]
    public async Task AcknowledgeEvent_SetsAcknowledgedByUserId()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, int ruleId) = await SeedAlertEventEnvironment(db);

        AlertEvent evt = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 1,
            Severity = AlertSeverity.Warning,
            Message = "Track acknowledger",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        evt.Id = await db.InsertWithInt64IdentityAsync(evt);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.MachineAdmin);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/alert-events/{evt.Id}/acknowledge", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        AlertEvent? updated = await db.AlertEvents.FirstOrDefaultAsync(e => e.Id == evt.Id);
        await Assert.That(updated!.AcknowledgedByUserId).IsEqualTo(userId);
    }

    // --- WS-4: MachineName Tests ---

    [Test]
    public async Task ListEvents_IncludesMachineName()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, int ruleId) = await SeedAlertEventEnvironment(db);

        long machineId = 5001;
        MachineStateSummary summary = new()
        {
            MachineId = machineId,
            TenantId = tenantId,
            Name = "web-server-prod-01",
            OperatingSystem = 0,
            MachineType = 0,
            HealthStatus = 0,
        };
        await db.InsertAsync(summary);

        AlertEvent evt = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = machineId,
            Severity = AlertSeverity.Warning,
            Message = "Machine name test",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt64IdentityAsync(evt);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-events");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("web-server-prod-01");
    }

    [Test]
    public async Task ListEvents_MachineNotInSummary_ShowsFallbackName()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, int ruleId) = await SeedAlertEventEnvironment(db);

        AlertEvent evt = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 99999,
            Severity = AlertSeverity.Warning,
            Message = "Fallback name test",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt64IdentityAsync(evt);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-events");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Machine 99999");
    }
}
