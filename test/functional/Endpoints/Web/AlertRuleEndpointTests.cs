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

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for alert rule CRUD endpoints.
/// </summary>
public sealed class AlertRuleEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedAlertEnvironment(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Team)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Alert Tenant {Guid.NewGuid():N}",
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
            RetentionDays = tier == SubscriptionTier.Free ? 1 : 30,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-alert-user-{Guid.NewGuid():N}",
            Username = $"alertuser-{Guid.NewGuid():N}@example.com",
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

        return (tenant.Id, user.Id);
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

    // --- CreateAlertRule Tests ---

    [Test]
    public async Task CreateAlertRule_NoTenant_Returns403()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Test",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 0,
            Severity = "Warning",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task CreateAlertRule_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Test",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 0,
            Severity = "Warning",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Pro or Team subscription");
    }

    [Test]
    public async Task CreateAlertRule_ProTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Test",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 0,
            Severity = "Warning",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Team subscription");
    }

    [Test]
    public async Task CreateAlertRule_TeamTier_ValidRequest_ReturnsRule()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "High CPU",
            Description = "CPU is too high",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            NotifyEmail = true,
            NotifyWebhook = true,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("High CPU");
        await Assert.That(body).Contains("CpuUsage");
        await Assert.That(body).Contains("GreaterThan");
        await Assert.That(body).Contains("Critical");
    }

    [Test]
    public async Task CreateAlertRule_TeamTier_VerifiesStoredInDatabase()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "DB Check Rule",
            Description = "Persisted rule",
            Metric = "MemoryUsage",
            Operator = "GreaterThan",
            Threshold = 75,
            DurationMinutes = 10,
            Severity = "Warning",
            NotifyEmail = true,
            NotifyWebhook = false,
        });

        AlertRule? dbRule = await db.AlertRules.FirstOrDefaultAsync(r => r.Name == "DB Check Rule");
        await Assert.That(dbRule).IsNotNull();
        await Assert.That(dbRule!.TenantId).IsEqualTo(tenantId);
        await Assert.That(dbRule.Metric).IsEqualTo(AlertMetric.MemoryUsage);
        await Assert.That(dbRule.Operator).IsEqualTo(AlertOperator.GreaterThan);
        await Assert.That(dbRule.Threshold).IsEqualTo(75m);
        await Assert.That(dbRule.DurationMinutes).IsEqualTo(10);
        await Assert.That(dbRule.Severity).IsEqualTo(AlertSeverity.Warning);
        await Assert.That(dbRule.NotifyEmail).IsTrue();
        await Assert.That(dbRule.NotifyWebhook).IsFalse();
        await Assert.That(dbRule.IsCustom).IsTrue();
    }

    [Test]
    public async Task CreateAlertRule_InvalidMetric_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Test",
            Metric = "InvalidMetric",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 0,
            Severity = "Warning",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Invalid metric");
    }

    [Test]
    public async Task CreateAlertRule_InvalidOperator_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Test",
            Metric = "CpuUsage",
            Operator = "NotAnOperator",
            Threshold = 80,
            DurationMinutes = 0,
            Severity = "Warning",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Invalid operator");
    }

    [Test]
    public async Task CreateAlertRule_InvalidSeverity_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Test",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 0,
            Severity = "Extreme",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Invalid severity");
    }

    [Test]
    public async Task CreateAlertRule_CaseInsensitiveEnums_Succeeds()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Lowercase Test",
            Metric = "cpuusage",
            Operator = "greaterthan",
            Threshold = 50,
            DurationMinutes = 0,
            Severity = "warning",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }

    // --- UpdateAlertRule Tests ---

    [Test]
    public async Task UpdateAlertRule_NoTenant_Returns403()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).Build();

        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/alert-rules/999", new
        {
            Name = "Updated",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task UpdateAlertRule_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/alert-rules/1", new
        {
            Name = "Updated",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task UpdateAlertRule_NonexistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/alert-rules/99999", new
        {
            Name = "Updated",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UpdateAlertRule_WrongTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        (int tenantId2, int userId2) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId1,
            Name = "Tenant1 Rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = true,
            CreatedByUserId = userId1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        HttpClient client = BuildClient(factory, tenantId2, userId2);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/alert-rules/{rule.Id}", new
        {
            Name = "Hacked",
            Threshold = 1,
            DurationMinutes = 0,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = false,
            NotifyWebhook = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UpdateAlertRule_InvalidSeverity_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "Original",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = false,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/alert-rules/{rule.Id}", new
        {
            Name = "Updated",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Bogus",
            IsEnabled = true,
            NotifyEmail = false,
            NotifyWebhook = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Invalid severity");
    }

    [Test]
    public async Task UpdateAlertRule_ValidRequest_UpdatesAllMutableFields()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "Original Name",
            Description = "Original Desc",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            DurationMinutes = 0,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            NotifyEmail = false,
            NotifyWebhook = false,
            IsCustom = false,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/alert-rules/{rule.Id}", new
        {
            Name = "Updated Name",
            Description = "Updated Desc",
            Threshold = 95,
            DurationMinutes = 10,
            Severity = "Critical",
            IsEnabled = false,
            NotifyEmail = true,
            NotifyWebhook = true,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Updated Name");
        await Assert.That(body).Contains("Critical");

        AlertRule? updated = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == rule.Id);
        await Assert.That(updated!.Name).IsEqualTo("Updated Name");
        await Assert.That(updated.Threshold).IsEqualTo(95m);
        await Assert.That(updated.DurationMinutes).IsEqualTo(10);
        await Assert.That(updated.IsEnabled).IsFalse();
        await Assert.That(updated.NotifyEmail).IsTrue();
        await Assert.That(updated.NotifyWebhook).IsTrue();
    }

    [Test]
    public async Task UpdateAlertRule_DoesNotChangeMetricOrOperator()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "Immutable Check",
            Metric = AlertMetric.MemoryUsage,
            Operator = AlertOperator.LessThan,
            Threshold = 20,
            Severity = AlertSeverity.Info,
            IsEnabled = true,
            IsCustom = false,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        HttpClient client = BuildClient(factory, tenantId, userId);

        await client.PutAsJsonAsync($"/api/v1/alert-rules/{rule.Id}", new
        {
            Name = "Changed Name",
            Threshold = 50,
            DurationMinutes = 0,
            Severity = "Warning",
            IsEnabled = true,
            NotifyEmail = false,
            NotifyWebhook = false,
        });

        AlertRule? updated = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == rule.Id);
        await Assert.That(updated!.Metric).IsEqualTo(AlertMetric.MemoryUsage);
        await Assert.That(updated.Operator).IsEqualTo(AlertOperator.LessThan);
    }

    // --- DeleteAlertRule Tests ---

    [Test]
    public async Task DeleteAlertRule_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/alert-rules/1");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task DeleteAlertRule_NonexistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/alert-rules/99999");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DeleteAlertRule_DefaultRule_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule defaultRule = new()
        {
            TenantId = tenantId,
            Name = "Default Rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 90,
            Severity = AlertSeverity.Critical,
            IsEnabled = true,
            IsCustom = false,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        defaultRule.Id = await db.InsertWithInt32IdentityAsync(defaultRule);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/alert-rules/{defaultRule.Id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Default rules cannot be deleted");
    }

    [Test]
    public async Task DeleteAlertRule_CustomRule_Succeeds()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule customRule = new()
        {
            TenantId = tenantId,
            Name = "Custom Delete Me",
            Metric = AlertMetric.DiskUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 95,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = true,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        customRule.Id = await db.InsertWithInt32IdentityAsync(customRule);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/alert-rules/{customRule.Id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        AlertRule? deleted = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == customRule.Id);
        await Assert.That(deleted).IsNull();
    }

    [Test]
    public async Task DeleteAlertRule_WrongTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);
        (int tenantId2, int userId2) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId1,
            Name = "Tenant1 Rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = true,
            CreatedByUserId = userId1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        HttpClient client = BuildClient(factory, tenantId2, userId2);

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/alert-rules/{rule.Id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // --- ListAlertRules Tests ---

    [Test]
    public async Task ListAlertRules_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-rules");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task ListAlertRules_Empty_ReturnsEmptyList()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-rules");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("\"data\":[]");
    }

    // --- WS-4: DurationMinutes Validation Tests ---

    [Test]
    public async Task CreateRule_NegativeDuration_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Negative duration",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = -5,
            Severity = "Warning",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Duration");
    }

    [Test]
    public async Task UpdateRule_NegativeDuration_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "Duration Check",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = false,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/alert-rules/{rule.Id}", new
        {
            Name = "Updated",
            Threshold = 90,
            DurationMinutes = -1,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Duration");
    }

    [Test]
    public async Task CreateRule_ZeroDuration_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Zero duration",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 0,
            Severity = "Warning",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    // --- WS-4: EqualTo Operator Tests ---

    [Test]
    public async Task CreateRule_OperatorEqualTo_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "EqualTo test",
            Metric = "CpuUsage",
            Operator = "EqualTo",
            Threshold = 50,
            DurationMinutes = 0,
            Severity = "Warning",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("EqualTo");
    }

    [Test]
    public async Task CreateRule_OperatorEquals_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Old Equals test",
            Metric = "CpuUsage",
            Operator = "Equals",
            Threshold = 50,
            DurationMinutes = 0,
            Severity = "Warning",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Invalid operator");
    }

    // --- WS-2: Delete Cleanup Tests ---

    [Test]
    public async Task DeleteRule_WithTriggeredEvents_ResolvesAllEvents()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "Cleanup Test",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = true,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        AlertEvent triggered = new()
        {
            AlertRuleId = rule.Id,
            TenantId = tenantId,
            MachineId = 1,
            Severity = AlertSeverity.Warning,
            Message = "Triggered",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        triggered.Id = await db.InsertWithInt64IdentityAsync(triggered);

        AlertEvent acknowledged = new()
        {
            AlertRuleId = rule.Id,
            TenantId = tenantId,
            MachineId = 2,
            Severity = AlertSeverity.Warning,
            Message = "Acknowledged",
            Status = AlertEventStatus.Acknowledged,
            TriggeredAt = DateTimeOffset.UtcNow,
            AcknowledgedAt = DateTimeOffset.UtcNow,
        };
        acknowledged.Id = await db.InsertWithInt64IdentityAsync(acknowledged);

        AlertEvent alreadyResolved = new()
        {
            AlertRuleId = rule.Id,
            TenantId = tenantId,
            MachineId = 3,
            Severity = AlertSeverity.Warning,
            Message = "Already resolved",
            Status = AlertEventStatus.Resolved,
            TriggeredAt = DateTimeOffset.UtcNow,
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        alreadyResolved.Id = await db.InsertWithInt64IdentityAsync(alreadyResolved);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/alert-rules/{rule.Id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Verify triggered and acknowledged events are now resolved.
        AlertEvent? triggeredEvent = await db.AlertEvents.FirstOrDefaultAsync(e => e.Id == triggered.Id);
        await Assert.That(triggeredEvent!.Status).IsEqualTo(AlertEventStatus.Resolved);
        await Assert.That(triggeredEvent.ResolvedAt.HasValue).IsTrue();

        AlertEvent? ackedEvent = await db.AlertEvents.FirstOrDefaultAsync(e => e.Id == acknowledged.Id);
        await Assert.That(ackedEvent!.Status).IsEqualTo(AlertEventStatus.Resolved);

        // Already-resolved event should be unchanged.
        AlertEvent? resolvedEvent = await db.AlertEvents.FirstOrDefaultAsync(e => e.Id == alreadyResolved.Id);
        await Assert.That(resolvedEvent!.Status).IsEqualTo(AlertEventStatus.Resolved);
    }

    [Test]
    public async Task DeleteRule_WithNoEvents_SucceedsCleanly()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "No Events",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = true,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/alert-rules/{rule.Id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        AlertRule? deleted = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == rule.Id);
        await Assert.That(deleted).IsNull();
    }

    // --- WS-3: Authorization & Tier Guard Tests ---

    [Test]
    public async Task UpdateRule_ProTierCustomRule_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule customRule = new()
        {
            TenantId = tenantId,
            Name = "Custom Pro Rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = true,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        customRule.Id = await db.InsertWithInt32IdentityAsync(customRule);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/alert-rules/{customRule.Id}", new
        {
            Name = "Updated",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Team subscription");
    }

    [Test]
    public async Task UpdateRule_ProTierDefaultRule_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule defaultRule = new()
        {
            TenantId = tenantId,
            Name = "Default CPU Rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = false,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        defaultRule.Id = await db.InsertWithInt32IdentityAsync(defaultRule);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/alert-rules/{defaultRule.Id}", new
        {
            Name = "Updated Default",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task UpdateRule_TeamTierCustomRule_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);

        AlertRule customRule = new()
        {
            TenantId = tenantId,
            Name = "Team Custom Rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = true,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        customRule.Id = await db.InsertWithInt32IdentityAsync(customRule);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/alert-rules/{customRule.Id}", new
        {
            Name = "Updated Team Custom",
            Threshold = 95,
            DurationMinutes = 10,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = true,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task UpdateRule_BlankName_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "Name Check",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = false,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/alert-rules/{rule.Id}", new
        {
            Name = "",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("name");
    }

    [Test]
    public async Task UpdateRule_WhitespaceName_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "Whitespace Check",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = false,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/alert-rules/{rule.Id}", new
        {
            Name = "   ",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // --- WS-3: Acknowledge Role Tests ---

    [Test]
    public async Task AcknowledgeEvent_MachineAdminRole_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, int ruleId) = await AlertEventEndpointTests_SeedEnvironment(db);

        AlertEvent evt = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 1,
            Severity = AlertSeverity.Warning,
            Message = "MachineAdmin ack test",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        evt.Id = await db.InsertWithInt64IdentityAsync(evt);

        // Authenticate as MachineAdmin (not TenantAdmin)
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync($"/api/v1/alert-events/{evt.Id}/acknowledge", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task AcknowledgeEvent_ViewerRole_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, int ruleId) = await AlertEventEndpointTests_SeedEnvironment(db);

        AlertEvent evt = new()
        {
            AlertRuleId = ruleId,
            TenantId = tenantId,
            MachineId = 1,
            Severity = AlertSeverity.Warning,
            Message = "Viewer ack test",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        evt.Id = await db.InsertWithInt64IdentityAsync(evt);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync($"/api/v1/alert-events/{evt.Id}/acknowledge", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    private static async Task<(int TenantId, int UserId, int RuleId)> AlertEventEndpointTests_SeedEnvironment(
        DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Ack Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            MachineLimit = null,
            RetentionDays = 30,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-ack-user-{Guid.NewGuid():N}",
            Username = $"ackuser-{Guid.NewGuid():N}@example.com",
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
            Role = UserAccountRoles.MachineAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        AlertRule alertRule = new()
        {
            TenantId = tenant.Id,
            Name = "Ack Test Rule",
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

    // --- WS-6: MachineOffline Metric Tests ---

    [Test]
    public async Task CreateRule_MachineOfflineMetric_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Machine Offline Alert",
            Metric = "MachineOffline",
            Operator = "GreaterThan",
            Threshold = 0,
            DurationMinutes = 5,
            Severity = "Critical",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("MachineOffline");
    }

    [Test]
    public async Task CreateRule_MachineOffline_OperatorGreaterThan_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Offline GT check",
            Metric = "MachineOffline",
            Operator = "GreaterThan",
            Threshold = 0,
            DurationMinutes = 0,
            Severity = "Warning",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("GreaterThan");
    }

    [Test]
    public async Task ListAlertRules_MultipleRules_ReturnsSortedByName()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        string[] names = ["Charlie", "Alpha", "Bravo"];
        foreach (string name in names)
        {
            AlertRule rule = new()
            {
                TenantId = tenantId,
                Name = name,
                Metric = AlertMetric.CpuUsage,
                Operator = AlertOperator.GreaterThan,
                Threshold = 80,
                Severity = AlertSeverity.Warning,
                IsEnabled = true,
                IsCustom = true,
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await db.InsertWithInt32IdentityAsync(rule);
        }

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-rules");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();

        int alphaIndex = body.IndexOf("Alpha", StringComparison.Ordinal);
        int bravoIndex = body.IndexOf("Bravo", StringComparison.Ordinal);
        int charlieIndex = body.IndexOf("Charlie", StringComparison.Ordinal);
        await Assert.That(alphaIndex < bravoIndex).IsTrue();
        await Assert.That(bravoIndex < charlieIndex).IsTrue();
    }
}
