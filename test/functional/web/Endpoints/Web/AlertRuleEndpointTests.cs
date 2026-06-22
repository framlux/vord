// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using NSubstitute;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for alert rule CRUD endpoints.
/// </summary>
public sealed class AlertRuleEndpointTests
{
    private static async Task<(int TenantId, int UserId, long MachineId)> SeedAlertEnvironment(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Team)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Alert Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
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
            ExternalId = $"ext-alert-user-{Guid.NewGuid():N}",
            Username = $"alertuser-{Guid.NewGuid():N}@example.com",
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
            Name = $"test-machine-{Guid.NewGuid():N}",
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
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { 1 },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        // The TenantAdmin authorization policy rejects the request before the handler runs because
        // there is no active tenant claim. ASP.NET emits an empty body for policy failures, so we
        // assert that no handler-level success payload made it through.
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.Contains("\"success\":true")).IsFalse();
    }

    [Test]
    public async Task CreateAlertRule_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Test",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Test",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
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
            MachineIds = new long[] { machineId },
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
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
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Test",
            Metric = "InvalidMetric",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Test",
            Metric = "CpuUsage",
            Operator = "NotAnOperator",
            Threshold = 80,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Test",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 5,
            Severity = "Extreme",
            MachineIds = new long[] { machineId },
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Lowercase Test",
            Metric = "cpuusage",
            Operator = "greaterthan",
            Threshold = 50,
            DurationMinutes = 5,
            Severity = "warning",
            MachineIds = new long[] { machineId },
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
            Metric = "CpuUsage",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            MachineIds = new long[] { 1 },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        // Policy rejection produces an empty body before the handler runs; assert no success
        // envelope leaked from the handler path.
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.Contains("\"success\":true")).IsFalse();
    }

    [Test]
    public async Task UpdateAlertRule_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/alert-rules/1", new
        {
            Name = "Updated",
            Metric = "CpuUsage",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Pro or Team subscription");
    }

    [Test]
    public async Task UpdateAlertRule_NonexistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PutAsJsonAsync("/api/v1/alert-rules/99999", new
        {
            Name = "Updated",
            Metric = "CpuUsage",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Alert rule not found");
    }

    [Test]
    public async Task UpdateAlertRule_WrongTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1, long machineId1) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        (int tenantId2, int userId2, long machineId2) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

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
            Metric = "CpuUsage",
            Threshold = 1,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = false,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId2 },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Alert rule not found");

        // Confirm the cross-tenant write attempt did not mutate tenant1's rule.
        AlertRule? untouched = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == rule.Id);
        await Assert.That(untouched!.Name).IsEqualTo("Tenant1 Rule");
        await Assert.That(untouched.TenantId).IsEqualTo(tenantId1);
    }

    [Test]
    public async Task UpdateAlertRule_InvalidSeverity_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

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
            Metric = "CpuUsage",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Bogus",
            IsEnabled = true,
            NotifyEmail = false,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId },
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "Original Name",
            Description = "Original Desc",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            DurationMinutes = 5,
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
            Metric = "CpuUsage",
            Threshold = 95,
            DurationMinutes = 10,
            Severity = "Critical",
            IsEnabled = false,
            NotifyEmail = true,
            NotifyWebhook = true,
            MachineIds = new long[] { machineId },
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

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

        HttpResponseMessage response = await client.PutAsJsonAsync($"/api/v1/alert-rules/{rule.Id}", new
        {
            Name = "Changed Name",
            Metric = "MemoryUsage",
            Threshold = 50,
            DurationMinutes = 5,
            Severity = "Warning",
            IsEnabled = true,
            NotifyEmail = false,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Response payload must reflect the new mutable name AND the unchanged immutable metric/operator.
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Changed Name");
        await Assert.That(body).Contains("MemoryUsage");
        await Assert.That(body).Contains("LessThan");

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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/alert-rules/1");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Pro or Team subscription");
    }

    [Test]
    public async Task DeleteAlertRule_NonexistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.DeleteAsync("/api/v1/alert-rules/99999");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Alert rule not found");
    }

    [Test]
    public async Task DeleteAlertRule_DefaultRule_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

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
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("Alert rule deleted");

        AlertRule? deleted = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == customRule.Id);
        await Assert.That(deleted).IsNull();
    }

    [Test]
    public async Task DeleteAlertRule_AlsoClearsAlertConditionStates()
    {
        // Intent: deleting a rule must remove every per-machine condition-tracking row for that
        // rule. Replaces the previous Redis-key cleanup contract; same invariant.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        long secondMachineId = await db.InsertWithInt64IdentityAsync(new Machine
        {
            ApiKeyHash = "hash-2",
            Name = "host-2",
            SerialNumber = "sn-2",
            SystemId = "sys-2",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId,
        });

        AlertRule customRule = new()
        {
            TenantId = tenantId,
            Name = "Custom With State",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = true,
            DurationMinutes = 5,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        customRule.Id = await db.InsertWithInt32IdentityAsync(customRule);

        await db.InsertAsync(new AlertConditionState
        {
            AlertRuleId = customRule.Id, MachineId = machineId,
            FirstTriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            LastObservedAt = DateTimeOffset.UtcNow,
        });
        await db.InsertAsync(new AlertConditionState
        {
            AlertRuleId = customRule.Id, MachineId = secondMachineId,
            FirstTriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            LastObservedAt = DateTimeOffset.UtcNow,
        });

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.DeleteAsync($"/api/v1/alert-rules/{customRule.Id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");

        int remaining = await db.AlertConditionStates.Where(s => s.AlertRuleId == customRule.Id).CountAsync();
        await Assert.That(remaining).IsEqualTo(0);

        // Intent: alert-rule deletion is a synchronous DB operation. It must NOT enqueue any
        // Hangfire job — there is no follow-up async work. If a future change adds an enqueue
        // path here (e.g., a notification job), this assertion fails and forces a deliberate
        // decision.
        factory.BackgroundJobClientMock.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Test]
    public async Task DeleteAlertRule_WrongTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1, long machineId1) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);
        (int tenantId2, int userId2, long machineId2) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

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
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Alert rule not found");

        // Cross-tenant delete must not remove tenant1's rule.
        AlertRule? stillThere = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == rule.Id);
        await Assert.That(stillThere).IsNotNull();
        await Assert.That(stillThere!.TenantId).IsEqualTo(tenantId1);
    }

    // --- ListAlertRules Tests ---

    [Test]
    public async Task ListAlertRules_FreeTier_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-rules");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Pro or Team subscription");
    }

    [Test]
    public async Task ListAlertRules_Empty_ReturnsEmptyList()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/alert-rules");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("\"data\":[]");
    }

    // --- Name Validation Tests ---

    [Test]
    public async Task CreateAlertRule_EmptyName_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("name");
    }

    [Test]
    public async Task CreateAlertRule_WhitespaceName_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "   ",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("name");
    }

    // --- WS-4: DurationMinutes Validation Tests ---

    [Test]
    public async Task CreateRule_NegativeDuration_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Negative duration",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = -5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

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
            Metric = "CpuUsage",
            Threshold = 90,
            DurationMinutes = -1,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Duration");
    }

    [Test]
    public async Task CreateRule_ZeroDuration_VolatileMetric_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Zero duration",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 0,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Duration must be at least 5 minutes for CpuUsage alerts");
    }

    // --- WS-4: EqualTo Operator Tests ---

    [Test]
    public async Task CreateRule_OperatorEqualTo_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "EqualTo test",
            Metric = "CpuUsage",
            Operator = "EqualTo",
            Threshold = 50,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Old Equals test",
            Metric = "CpuUsage",
            Operator = "Equals",
            Threshold = 50,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

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
            MachineId = machineId,
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
            MachineId = machineId,
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
            MachineId = machineId,
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

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
            Metric = "CpuUsage",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId },
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

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
            Metric = "CpuUsage",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Updated Default");
        await Assert.That(body).Contains("Critical");

        // Pro tier is allowed to edit DEFAULT rules; verify the row was actually updated.
        AlertRule? updated = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == defaultRule.Id);
        await Assert.That(updated!.Name).IsEqualTo("Updated Default");
        await Assert.That(updated.Threshold).IsEqualTo(90m);
        await Assert.That(updated.Severity).IsEqualTo(AlertSeverity.Critical);
    }

    [Test]
    public async Task UpdateRule_TeamTierCustomRule_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);

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
            Metric = "CpuUsage",
            Threshold = 95,
            DurationMinutes = 10,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = true,
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Updated Team Custom");
        await Assert.That(body).Contains("\"success\":true");

        // Team tier is the only tier allowed to edit CUSTOM rules; verify the row was updated.
        AlertRule? updated = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == customRule.Id);
        await Assert.That(updated!.Name).IsEqualTo("Updated Team Custom");
        await Assert.That(updated.Threshold).IsEqualTo(95m);
        await Assert.That(updated.DurationMinutes).IsEqualTo(10);
        await Assert.That(updated.NotifyEmail).IsTrue();
        await Assert.That(updated.NotifyWebhook).IsTrue();
    }

    [Test]
    public async Task UpdateRule_BlankName_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

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
            Metric = "CpuUsage",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId },
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

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
            Metric = "CpuUsage",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("name");
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
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Alert acknowledged");

        // The event was actually transitioned in the DB by the handler.
        AlertEvent? acked = await db.AlertEvents.FirstOrDefaultAsync(e => e.Id == evt.Id);
        await Assert.That(acked!.Status).IsEqualTo(AlertEventStatus.Acknowledged);
        await Assert.That(acked.AcknowledgedAt.HasValue).IsTrue();
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

        // Viewer role is blocked by the MachineAdmin authorization policy before the handler runs.
        // ASP.NET emits an empty body for policy failures, so we assert no success envelope.
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.Contains("\"success\":true")).IsFalse();

        // The event must remain in Triggered state — viewer should not be able to ack.
        AlertEvent? stillTriggered = await db.AlertEvents.FirstOrDefaultAsync(e => e.Id == evt.Id);
        await Assert.That(stillTriggered!.Status).IsEqualTo(AlertEventStatus.Triggered);
    }

    private static async Task<(int TenantId, int UserId, int RuleId)> AlertEventEndpointTests_SeedEnvironment(
        DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Ack Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-ack-user-{Guid.NewGuid():N}",
            Username = $"ackuser-{Guid.NewGuid():N}@example.com",
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

    // --- WS-4: Update DurationMinutes Metric-Specific Validation Tests ---

    [Test]
    public async Task UpdateAlertRule_CpuMetric_DurationBelowMinimum_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "CPU Duration Check",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            DurationMinutes = 5,
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
            Name = "CPU Duration Check",
            Metric = "CpuUsage",
            Threshold = 80,
            DurationMinutes = 3,
            Severity = "Warning",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Duration must be at least 5 minutes");
    }

    [Test]
    public async Task UpdateAlertRule_CpuMetric_DurationAtMinimum_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "CPU Duration OK",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            DurationMinutes = 10,
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
            Name = "CPU Duration OK",
            Metric = "CpuUsage",
            Threshold = 80,
            DurationMinutes = 5,
            Severity = "Warning",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("\"durationMinutes\":5");

        // Duration at the CPU minimum (5) must persist to the DB, not be silently coerced.
        AlertRule? updated = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == rule.Id);
        await Assert.That(updated!.DurationMinutes).IsEqualTo(5);
    }

    [Test]
    public async Task UpdateAlertRule_StateMetric_DurationBelowMinimum_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "Offline Duration Check",
            Metric = AlertMetric.MachineOffline,
            Operator = AlertOperator.EqualTo,
            Threshold = 1,
            DurationMinutes = 1,
            Severity = AlertSeverity.Critical,
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
            Name = "Offline Duration Check",
            Metric = "MachineOffline",
            Threshold = 1,
            DurationMinutes = 0,
            Severity = "Critical",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Duration must be at least 1 minutes");
    }

    [Test]
    public async Task UpdateAlertRule_SshMetric_NonZeroDuration_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "SSH Duration Check",
            Metric = AlertMetric.SshConnection,
            Operator = AlertOperator.EqualTo,
            Threshold = 1,
            DurationMinutes = 0,
            Severity = AlertSeverity.Info,
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
            Name = "SSH Duration Check",
            Metric = "SshConnection",
            Threshold = 1,
            DurationMinutes = 1,
            Severity = "Info",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Duration must be zero for event-based metrics");
    }

    [Test]
    public async Task UpdateAlertRule_SshMetric_ZeroDuration_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = "SSH Zero Duration",
            Metric = AlertMetric.SshConnection,
            Operator = AlertOperator.EqualTo,
            Threshold = 1,
            DurationMinutes = 0,
            Severity = AlertSeverity.Info,
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
            Name = "SSH Zero Duration",
            Metric = "SshConnection",
            Threshold = 1,
            DurationMinutes = 0,
            Severity = "Info",
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("SshConnection");
        await Assert.That(body).Contains("\"durationMinutes\":0");

        // Event-based metrics (SSH) require duration to remain zero; verify the DB row reflects that.
        AlertRule? updated = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == rule.Id);
        await Assert.That(updated!.DurationMinutes).IsEqualTo(0);
    }

    // --- WS-6: MachineOffline Metric Tests ---

    [Test]
    public async Task CreateRule_MachineOfflineMetric_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Machine Offline Alert",
            Metric = "MachineOffline",
            Operator = "GreaterThan",
            Threshold = 0,
            DurationMinutes = 5,
            Severity = "Critical",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("MachineOffline");
        await Assert.That(body).Contains("\"success\":true");

        // The created rule must be persisted with the requested metric/severity for this tenant.
        AlertRule? created = await db.AlertRules.FirstOrDefaultAsync(r => r.Name == "Machine Offline Alert" && r.TenantId == tenantId);
        await Assert.That(created).IsNotNull();
        await Assert.That(created!.Metric).IsEqualTo(AlertMetric.MachineOffline);
        await Assert.That(created.Severity).IsEqualTo(AlertSeverity.Critical);
    }

    [Test]
    public async Task CreateRule_MachineOffline_OperatorGreaterThan_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Offline GT check",
            Metric = "MachineOffline",
            Operator = "GreaterThan",
            Threshold = 0,
            DurationMinutes = 1,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
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
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Pro);

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

        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement dataArray = doc.RootElement.GetProperty("data");
        await Assert.That(dataArray.GetArrayLength()).IsGreaterThanOrEqualTo(3);
        await Assert.That(dataArray[0].GetProperty("name").GetString()).IsEqualTo("Alpha");
        await Assert.That(dataArray[1].GetProperty("name").GetString()).IsEqualTo("Bravo");
        await Assert.That(dataArray[2].GetProperty("name").GetString()).IsEqualTo("Charlie");
    }

    // --- Limit, Validation, and Cross-Tenant Tests ---

    [Test]
    public async Task CreateAlertRule_AtLimit_Returns403WithLimitMessage()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);

        // Reduce the Team tier alert rule limit to 1 so we can hit it with a single rule
        await db.TierFeatureLimits
            .Where(l => l.Tier == SubscriptionTier.Team)
            .Set(l => l.AlertRuleLimit, 1)
            .Set(l => l.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync();

        HttpClient client = BuildClient(factory, tenantId, userId);

        // Create first rule to consume the limit
        HttpResponseMessage firstResponse = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "First Rule",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
        });
        await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Second rule should be rejected because the limit is reached
        HttpResponseMessage secondResponse = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Second Rule",
            Metric = "MemoryUsage",
            Operator = "GreaterThan",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Critical",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(secondResponse.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await secondResponse.Content.ReadAsStringAsync();
        await Assert.That(body.ToLowerInvariant()).Contains("limit");
    }

    [Test]
    public async Task CreateAlertRule_NameTooLong_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        string longName = new string('A', 251);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = longName,
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("250 characters");
    }

    [Test]
    public async Task CreateAlertRule_DescriptionTooLong_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        string longDescription = new string('D', 2001);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Valid Name",
            Description = longDescription,
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("2000 characters");
    }

    [Test]
    public async Task CreateAlertRule_CpuUsage_ThresholdOver100_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "CPU Over 100",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 101,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("percentage");
    }

    [Test]
    public async Task CreateAlertRule_CpuUsage_ThresholdNegative_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "CPU Negative",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = -1,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("percentage");
    }

    [Test]
    public async Task CreateAlertRule_MachineOffline_Threshold2_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Offline Bad Threshold",
            Metric = "MachineOffline",
            Operator = "GreaterThan",
            Threshold = 2,
            DurationMinutes = 1,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("0 or 1");
    }

    [Test]
    public async Task CreateAlertRule_FailedServices_NegativeThreshold_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Failed Services Negative",
            Metric = "FailedServices",
            Operator = "GreaterThan",
            Threshold = -1,
            DurationMinutes = 1,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("zero or positive");
    }

    [Test]
    public async Task CreateAlertRule_CanceledSubscription_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);

        // Mark the subscription as canceled to simulate a lapsed subscription
        await db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.Status, SubscriptionStatus.Canceled)
            .UpdateAsync();

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Should Fail",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 80,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.ToLowerInvariant()).Contains("canceled");
    }

    // --- Transactional Audit Log Tests ---

    [Test]
    public async Task CreateAlertRule_HappyPath_WritesAuditLogEntry()
    {
        // Intent: the alert rule creation and its audit log entry must be written in the same
        // transaction. This test confirms the transactional path still records the audit row on
        // a successful create.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId, long machineId) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Audit Log Test Rule",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 85,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        AuditLogEntry? auditEntry = await db.AuditLog
            .Where(a => (a.TenantId == tenantId) && (a.Action == AuditAction.AlertRuleCreated))
            .FirstOrDefaultAsync();

        await Assert.That(auditEntry).IsNotNull();
        await Assert.That(auditEntry!.ResourceType).IsEqualTo(AuditResourceType.AlertRule);
    }

    [Test]
    public async Task DeleteRule_CrossTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantIdA, int userIdA, long machineIdA) = await SeedAlertEnvironment(db, SubscriptionTier.Team);
        (int tenantIdB, int userIdB, long machineIdB) = await SeedAlertEnvironment(db, SubscriptionTier.Team);

        // Create a custom rule owned by tenant A
        AlertRule ruleA = new()
        {
            TenantId = tenantIdA,
            Name = "TenantA Custom Rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 85,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = true,
            CreatedByUserId = userIdA,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        ruleA.Id = await db.InsertWithInt32IdentityAsync(ruleA);

        // Authenticate as tenant B and try to delete tenant A's rule
        HttpClient clientB = BuildClient(factory, tenantIdB, userIdB);

        HttpResponseMessage response = await clientB.DeleteAsync($"/api/v1/alert-rules/{ruleA.Id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // Verify the rule still exists in the database
        AlertRule? stillExists = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == ruleA.Id);
        await Assert.That(stillExists).IsNotNull();
    }
}
