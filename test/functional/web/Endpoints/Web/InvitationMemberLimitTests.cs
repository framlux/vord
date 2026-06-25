// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests that drive per-tier member-limit enforcement through the full HTTP
/// invitation pipeline (real auth, real handlers, real database).
/// </summary>
public sealed class InvitationMemberLimitTests
{
    private static async Task<(int TenantId, int UserId)> SeedInvitationEnvironment(
        DatabaseContext db,
        SubscriptionTier tier)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Limit Tenant {Guid.NewGuid():N}",
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
            ExternalId = $"ext-limit-user-{Guid.NewGuid():N}",
            Username = $"limituser-{Guid.NewGuid():N}@example.com",
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

        return (tenant.Id, user.Id);
    }

    private static HttpClient BuildClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithEmail("admin@example.com")
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    private static async Task<(HttpStatusCode Status, bool Success, string Message)> ReadResult(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        string message = root.GetProperty("message").GetString() ?? string.Empty;

        return (response.StatusCode, success, message);
    }

    [Test]
    public async Task ProTenant_InviteUpToCap_ThenNextRejected()
    {
        // Pro MemberLimit is 5. Enforcement allows a create while
        // (activeMembers + pendingInvitations) < limit, evaluated at each create.
        // With 1 active member (the seeded admin), pending may grow to 4 before the
        // sum reaches the cap (1 + 4 == 5). So exactly 4 creates succeed, and the
        // 5th additional invite (which would make 1 + 5 == 6 > 5) is rejected with 409.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db, SubscriptionTier.Pro);
        HttpClient client = BuildClient(factory, tenantId, userId);

        const int allowedInvites = 4;
        for (int i = 0; i < allowedInvites; i++)
        {
            HttpResponseMessage ok = await client.PostAsJsonAsync("/api/v1/invitations", new
            {
                Email = $"member-{i}@example.com",
            });

            (HttpStatusCode status, bool success, _) = await ReadResult(ok);
            await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(success).IsTrue();
        }

        HttpResponseMessage overCap = await client.PostAsJsonAsync("/api/v1/invitations", new
        {
            Email = "member-over-cap@example.com",
        });

        (HttpStatusCode overStatus, bool overSuccess, string overMessage) = await ReadResult(overCap);
        await Assert.That(overStatus).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(overSuccess).IsFalse();
        await Assert.That(overMessage).Contains("member limit");
    }

    [Test]
    public async Task FreeTenant_CannotInvite_Returns402()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/invitations", new
        {
            Email = "newmember@example.com",
        });

        (HttpStatusCode status, bool success, string message) = await ReadResult(response);
        await Assert.That(status).IsEqualTo(HttpStatusCode.PaymentRequired);
        await Assert.That(success).IsFalse();
        await Assert.That(message).Contains("Upgrade");
    }
}
