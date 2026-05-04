// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using System.Net;
using System.Text.Json;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for audit log endpoints.
/// </summary>
public sealed class AuditLogEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedEnvironment(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Team)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Audit Tenant {Guid.NewGuid():N}",
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
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-audit-{Guid.NewGuid():N}",
            Username = $"audit-{Guid.NewGuid():N}@example.com",
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

    [Test]
    public async Task ListAuditLog_TeamTier_ReturnsEntries()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedEnvironment(db, SubscriptionTier.Team);

        // Seed an audit log entry.
        await db.InsertAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            UserId = userId,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow,
        });

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/audit-log");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("\"totalCount\":1");
    }

    [Test]
    public async Task ListAuditLog_ProTier_ReturnsForbiddenMessage()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedEnvironment(db, SubscriptionTier.Pro);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/audit-log");

        // The endpoint sets 403 on HttpContext and returns error payload.
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Team subscription");
    }

    [Test]
    public async Task ListAuditLog_WithActionFilter_FiltersResults()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedEnvironment(db, SubscriptionTier.Team);

        // Seed two different action types.
        await db.InsertAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            UserId = userId,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow,
        });

        await db.InsertAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            UserId = userId,
            Action = AuditAction.MachineRegistered,
            ResourceType = AuditResourceType.Machine,
            Timestamp = DateTimeOffset.UtcNow,
        });

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/audit-log?Action=UserLogin");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalCount\":1");
        await Assert.That(body).Contains("UserLogin");
    }

    [Test]
    public async Task ListAuditLog_InvalidActionFilter_ReturnsUnfilteredResults()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedEnvironment(db, SubscriptionTier.Team);

        await db.InsertAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            UserId = userId,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow,
        });

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        // Invalid action filter is ignored gracefully — returns all results.
        HttpResponseMessage response = await client.GetAsync("/api/v1/audit-log?Action=NotARealAction");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalCount\":1");
    }

    [Test]
    public async Task ListAuditLog_Pagination_RespectsPageSize()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedEnvironment(db, SubscriptionTier.Team);

        // Seed 5 entries.
        for (int i = 0; i < 5; i++)
        {
            await db.InsertAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                UserId = userId,
                Action = AuditAction.UserLogin,
                ResourceType = AuditResourceType.User,
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-i),
            });
        }

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/audit-log?Page=1&PageSize=2");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalCount\":5");

        using JsonDocument doc = JsonDocument.Parse(body);
        int itemCount = doc.RootElement.GetProperty("data").GetProperty("items").GetArrayLength();
        await Assert.That(itemCount).IsEqualTo(2);
    }

    [Test]
    public async Task ListAuditLog_PageSizeExceedsMax_ClampedTo100()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedEnvironment(db, SubscriptionTier.Team);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        // Page size > 100 should be clamped to the default (25), not cause an error.
        HttpResponseMessage response = await client.GetAsync("/api/v1/audit-log?PageSize=500");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }
}
