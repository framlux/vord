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
/// Functional tests for invitation endpoints.
/// </summary>
public sealed class InvitationEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedInvitationEnvironment(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Pro)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Invite Tenant {Guid.NewGuid():N}",
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
            ExternalId = $"ext-inv-user-{Guid.NewGuid():N}",
            Username = $"invuser-{Guid.NewGuid():N}@example.com",
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
        string email = "admin@example.com")
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithEmail(email)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    // --- InvitationListEndpoint Tests ---

    [Test]
    public async Task ListInvitations_NoTenant_Returns403()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/invitations");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task ListInvitations_Empty_ReturnsEmptyList()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/invitations");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();
        int dataLength = root.GetProperty("data").GetArrayLength();
        await Assert.That(dataLength).IsEqualTo(0);
    }

    [Test]
    public async Task ListInvitations_WithInvitations_ReturnsCorrectInvitationFields()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);

        for (int i = 0; i < 2; i++)
        {
            TenantInvitation invitation = new()
            {
                TenantId = tenantId,
                Email = $"invitee{i}@example.com",
                TokenHash = Guid.NewGuid().ToString("N"),
                Role = UserAccountRoles.Viewer,
                Status = InvitationStatus.Pending,
                InvitedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            };
            await db.InsertWithInt32IdentityAsync(invitation);
        }

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/invitations");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        JsonElement data = root.GetProperty("data");
        int dataLength = data.GetArrayLength();
        await Assert.That(dataLength).IsEqualTo(2);

        // Verify each invitation has the expected structure and field values
        List<string> emails = new();
        for (int i = 0; i < dataLength; i++)
        {
            JsonElement item = data[i];
            int id = item.GetProperty("id").GetInt32();
            await Assert.That(id).IsGreaterThan(0);

            string email = item.GetProperty("email").GetString() ?? string.Empty;
            await Assert.That(email).IsNotEmpty();
            emails.Add(email);

            string status = item.GetProperty("status").GetString() ?? string.Empty;
            await Assert.That(status).IsEqualTo("Pending");

            string role = item.GetProperty("role").GetString() ?? string.Empty;
            await Assert.That(role).IsNotEmpty();

            string createdAt = item.GetProperty("createdAt").GetString() ?? string.Empty;
            await Assert.That(createdAt).IsNotEmpty();

            string expiresAt = item.GetProperty("expiresAt").GetString() ?? string.Empty;
            await Assert.That(expiresAt).IsNotEmpty();
        }

        await Assert.That(emails).Contains("invitee0@example.com");
        await Assert.That(emails).Contains("invitee1@example.com");
    }

    [Test]
    public async Task ListInvitations_ExpiredPending_LabeledAsExpired()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);

        TenantInvitation invitation = new()
        {
            TenantId = tenantId,
            Email = "expired@example.com",
            TokenHash = Guid.NewGuid().ToString("N"),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Pending,
            InvitedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-3),
        };
        await db.InsertWithInt32IdentityAsync(invitation);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/invitations");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        JsonElement data = root.GetProperty("data");
        int dataLength = data.GetArrayLength();
        await Assert.That(dataLength).IsEqualTo(1);

        JsonElement expiredItem = data[0];
        string email = expiredItem.GetProperty("email").GetString() ?? string.Empty;
        await Assert.That(email).IsEqualTo("expired@example.com");

        string status = expiredItem.GetProperty("status").GetString() ?? string.Empty;
        await Assert.That(status).IsEqualTo("Expired");
    }

    [Test]
    public async Task ListInvitations_CrossTenantIsolation_OnlyOwnTenantInvitations()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1) = await SeedInvitationEnvironment(db);
        (int tenantId2, int userId2) = await SeedInvitationEnvironment(db);

        TenantInvitation inv1 = new()
        {
            TenantId = tenantId1,
            Email = "tenant1-invite@example.com",
            TokenHash = Guid.NewGuid().ToString("N"),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Pending,
            InvitedByUserId = userId1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        await db.InsertWithInt32IdentityAsync(inv1);

        TenantInvitation inv2 = new()
        {
            TenantId = tenantId2,
            Email = "tenant2-invite@example.com",
            TokenHash = Guid.NewGuid().ToString("N"),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Pending,
            InvitedByUserId = userId2,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        await db.InsertWithInt32IdentityAsync(inv2);

        HttpClient client = BuildClient(factory, tenantId1, userId1);

        HttpResponseMessage response = await client.GetAsync("/api/v1/invitations");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        JsonElement data = root.GetProperty("data");
        int dataLength = data.GetArrayLength();
        await Assert.That(dataLength).IsEqualTo(1);

        string email = data[0].GetProperty("email").GetString() ?? string.Empty;
        await Assert.That(email).IsEqualTo("tenant1-invite@example.com");
    }

    // --- InvitationCreateEndpoint Tests ---

    [Test]
    public async Task CreateInvitation_InvalidEmailFormat_Returns400WithError()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/invitations", new
        {
            Email = "not-an-email",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        string message = root.GetProperty("message").GetString() ?? string.Empty;
        await Assert.That(message).Contains("valid email");
    }

    [Test]
    public async Task CreateInvitation_EmptyEmail_Returns400WithError()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/invitations", new
        {
            Email = "",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        string message = root.GetProperty("message").GetString() ?? string.Empty;
        await Assert.That(message).Contains("Email is required");
    }

    [Test]
    public async Task CreateInvitation_UserAlreadyMember_Returns409WithConflict()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);

        // Seed a second user who is already a member of this tenant
        string memberEmail = $"existing-member-{Guid.NewGuid():N}@example.com";
        UserAccount existingMember = new()
        {
            ExternalId = $"ext-existing-{Guid.NewGuid():N}",
            Username = memberEmail,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        existingMember.Id = await db.InsertWithInt32IdentityAsync(existingMember);

        UserTenantRole memberRole = new()
        {
            UserId = existingMember.Id,
            AssignedTenantId = tenantId,
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = userId,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(memberRole);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/invitations", new
        {
            Email = memberEmail,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        string message = root.GetProperty("message").GetString() ?? string.Empty;
        await Assert.That(message).Contains("already a member");
    }

    [Test]
    public async Task CreateInvitation_FreeTier_Returns402RequiresUpgrade()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/invitations", new
        {
            Email = "newuser@example.com",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.PaymentRequired);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        string message = root.GetProperty("message").GetString() ?? string.Empty;
        await Assert.That(message).Contains("Upgrade");
    }

    [Test]
    public async Task CreateInvitation_DuplicatePendingInvitation_Returns409()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);

        // Seed an existing pending invitation
        string duplicateEmail = "duplicate@example.com";
        TenantInvitation existing = new()
        {
            TenantId = tenantId,
            Email = duplicateEmail,
            TokenHash = Guid.NewGuid().ToString("N"),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Pending,
            InvitedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        await db.InsertWithInt32IdentityAsync(existing);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/invitations", new
        {
            Email = duplicateEmail,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        string message = root.GetProperty("message").GetString() ?? string.Empty;
        await Assert.That(message).Contains("pending invitation already exists");
    }

    // --- InvitationDetailEndpoint Tests ---

    [Test]
    public async Task InvitationDetail_InvalidToken_Returns404()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/invitations/by-token/nonexistenttoken");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task InvitationDetail_NonExistentToken_Returns404WithErrorBody()
    {
        // A randomly-generated token that was never seeded must return 404 with an error body.
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).Build();

        string randomToken = Guid.NewGuid().ToString("N");
        HttpResponseMessage response = await client.GetAsync($"/api/v1/invitations/by-token/{randomToken}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        string message = root.GetProperty("message").GetString() ?? string.Empty;
        await Assert.That(message).Contains("not found");
    }

    [Test]
    public async Task InvitationDetail_TokenNotRegisteredInAnyTenant_Returns404WithErrorBody()
    {
        // Verifies that a token never seeded in any tenant returns 404 regardless of caller context.
        // The detail endpoint is intentionally public (pre-login accept page) and looks up purely
        // by token hash — so tenant context of the caller is irrelevant. A missing token always 404s.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1) = await SeedInvitationEnvironment(db);
        (int tenantId2, int userId2) = await SeedInvitationEnvironment(db);

        // Seed a real invitation in tenant 2 — it will be found by its token
        string realToken = $"real-token-tenant2-{Guid.NewGuid():N}";
        TenantInvitation invitation = new()
        {
            TenantId = tenantId2,
            Email = "detail-tenant2@example.com",
            TokenHash = HashToken(realToken),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Pending,
            InvitedByUserId = userId2,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        await db.InsertWithInt32IdentityAsync(invitation);

        // A different token never seeded in any tenant returns 404 even from a tenant 1 client
        string unregisteredToken = $"unregistered-cross-tenant-{Guid.NewGuid():N}";
        HttpClient client = BuildClient(factory, tenantId1, userId1);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/invitations/by-token/{unregisteredToken}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        string message = root.GetProperty("message").GetString() ?? string.Empty;
        await Assert.That(message).Contains("not found");
    }

    // --- InvitationAcceptEndpoint Tests ---

    [Test]
    public async Task AcceptInvitation_InvalidToken_Returns404()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithEmail("user@example.com")
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/invitations/nonexistenttoken/accept", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // --- InvitationRevokeEndpoint Tests ---

    [Test]
    public async Task RevokeInvitation_PendingInvitation_Succeeds()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);

        TenantInvitation invitation = new()
        {
            TenantId = tenantId,
            Email = "revoke-me@example.com",
            TokenHash = Guid.NewGuid().ToString("N"),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Pending,
            InvitedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        invitation.Id = await db.InsertWithInt32IdentityAsync(invitation);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/invitations/{invitation.Id}/revoke", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        string message = root.GetProperty("message").GetString() ?? string.Empty;
        await Assert.That(message).IsEqualTo("Invitation revoked");
    }

    [Test]
    public async Task RevokeInvitation_AlreadyRevoked_ReturnsErrorWithReason()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);

        TenantInvitation invitation = new()
        {
            TenantId = tenantId,
            Email = "already-revoked@example.com",
            TokenHash = Guid.NewGuid().ToString("N"),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Revoked,
            InvitedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        invitation.Id = await db.InsertWithInt32IdentityAsync(invitation);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/invitations/{invitation.Id}/revoke", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        string message = root.GetProperty("message").GetString() ?? string.Empty;
        await Assert.That(message).Contains("Only pending invitations can be revoked");
    }

    [Test]
    public async Task RevokeInvitation_AcceptedInvitation_ReturnsErrorWithReason()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);

        TenantInvitation invitation = new()
        {
            TenantId = tenantId,
            Email = "already-accepted@example.com",
            TokenHash = Guid.NewGuid().ToString("N"),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Accepted,
            InvitedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        invitation.Id = await db.InsertWithInt32IdentityAsync(invitation);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/invitations/{invitation.Id}/revoke", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        string message = root.GetProperty("message").GetString() ?? string.Empty;
        await Assert.That(message).Contains("Only pending invitations can be revoked");
    }

    [Test]
    public async Task RevokeInvitation_NonExistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/invitations/99999/revoke", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task RevokeInvitation_CrossTenantInvitation_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1) = await SeedInvitationEnvironment(db);
        (int tenantId2, int userId2) = await SeedInvitationEnvironment(db);

        // Create invitation in tenant 2
        TenantInvitation invitation = new()
        {
            TenantId = tenantId2,
            Email = "cross-tenant@example.com",
            TokenHash = Guid.NewGuid().ToString("N"),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Pending,
            InvitedByUserId = userId2,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        invitation.Id = await db.InsertWithInt32IdentityAsync(invitation);

        // Try to revoke from tenant 1
        HttpClient client = BuildClient(factory, tenantId1, userId1);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/invitations/{invitation.Id}/revoke", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // --- InvitationResendEndpoint Tests ---

    [Test]
    public async Task ResendInvitation_NonexistentId_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/invitations/99999/resend", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ResendInvitation_AlreadyRevokedInvitation_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);

        TenantInvitation invitation = new()
        {
            TenantId = tenantId,
            Email = "resend-revoked@example.com",
            TokenHash = Guid.NewGuid().ToString("N"),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Revoked,
            InvitedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        invitation.Id = await db.InsertWithInt32IdentityAsync(invitation);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/invitations/{invitation.Id}/resend", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        string message = root.GetProperty("message").GetString() ?? string.Empty;
        await Assert.That(message).Contains("Only pending invitations can be resent");
    }

    [Test]
    public async Task ResendInvitation_CrossTenantInvitation_Returns404()
    {
        // Verifies tenant isolation: an invitation belonging to tenant 2 cannot be resent
        // by a tenant-admin acting on behalf of tenant 1.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId1, int userId1) = await SeedInvitationEnvironment(db);
        (int tenantId2, int userId2) = await SeedInvitationEnvironment(db);

        TenantInvitation invitation = new()
        {
            TenantId = tenantId2,
            Email = "resend-cross-tenant@example.com",
            TokenHash = Guid.NewGuid().ToString("N"),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Pending,
            InvitedByUserId = userId2,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        invitation.Id = await db.InsertWithInt32IdentityAsync(invitation);

        HttpClient client = BuildClient(factory, tenantId1, userId1);

        HttpResponseMessage response = await client.PostAsync($"/api/v1/invitations/{invitation.Id}/resend", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();

        string message = root.GetProperty("message").GetString() ?? string.Empty;
        await Assert.That(message).Contains("not found");
    }

    // --- InvitationAcceptEndpoint Tests ---

    [Test]
    public async Task AcceptInvitation_ValidInvitation_DatabaseStateUpdated()
    {
        // The accept endpoint's happy path triggers SignInAsync to re-issue the cookie,
        // which the test auth handler doesn't support. We verify that the handler logic
        // (invitation status change and UserTenantRole creation) works correctly by
        // confirming the DB state after the attempt.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int adminUserId) = await SeedInvitationEnvironment(db);

        // Create a different user who will accept the invitation
        UserAccount acceptingUser = new()
        {
            ExternalId = $"ext-acceptor-{Guid.NewGuid():N}",
            Username = "acceptor@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        acceptingUser.Id = await db.InsertWithInt32IdentityAsync(acceptingUser);

        string rawToken = $"valid-accept-token-{Guid.NewGuid():N}";
        TenantInvitation invitation = new()
        {
            TenantId = tenantId,
            Email = "acceptor@example.com",
            TokenHash = HashToken(rawToken),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Pending,
            InvitedByUserId = adminUserId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        invitation.Id = await db.InsertWithInt32IdentityAsync(invitation);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(acceptingUser.Id)
            .WithEmail("acceptor@example.com")
            .WithExternalId(acceptingUser.ExternalId)
            .Build();

        // The request may return 500 due to SignInAsync limitation in test infrastructure,
        // but the handler logic (DB writes) should still have executed.
        await client.PostAsync($"/api/v1/invitations/{rawToken}/accept", null);

        // Verify the invitation status changed to Accepted
        TenantInvitation? updatedInvitation = await db.TenantInvitations
            .Where(i => i.Id == invitation.Id)
            .FirstOrDefaultAsync();

        await Assert.That(updatedInvitation).IsNotNull();
        await Assert.That(updatedInvitation!.Status).IsEqualTo(InvitationStatus.Accepted);

        // Verify UserTenantRole was created
        UserTenantRole? tenantRole = await db.UserTenantRoles
            .Where(r => r.UserId == acceptingUser.Id && r.AssignedTenantId == tenantId)
            .FirstOrDefaultAsync();

        await Assert.That(tenantRole).IsNotNull();
        await Assert.That(tenantRole!.Role).IsEqualTo(UserAccountRoles.Viewer);
    }

    [Test]
    public async Task AcceptInvitation_EmailMismatch_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int adminUserId) = await SeedInvitationEnvironment(db);

        UserAccount wrongUser = new()
        {
            ExternalId = $"ext-wrong-{Guid.NewGuid():N}",
            Username = "wronguser@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        wrongUser.Id = await db.InsertWithInt32IdentityAsync(wrongUser);

        string rawToken = $"mismatch-token-{Guid.NewGuid():N}";
        TenantInvitation invitation = new()
        {
            TenantId = tenantId,
            Email = "correct@example.com",
            TokenHash = HashToken(rawToken),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Pending,
            InvitedByUserId = adminUserId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        await db.InsertWithInt32IdentityAsync(invitation);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(wrongUser.Id)
            .WithEmail("wronguser@example.com")
            .WithExternalId(wrongUser.ExternalId)
            .Build();

        HttpResponseMessage response = await client.PostAsync($"/api/v1/invitations/{rawToken}/accept", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("does not match");
    }

    // --- InvitationAcceptEndpoint Error Path Tests ---

    private static string HashToken(string token)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

        return Convert.ToHexStringLower(hash);
    }

    [Test]
    public async Task AcceptInvitation_ExpiredToken_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);

        string rawToken = "expired-test-token-value-123456";
        TenantInvitation invitation = new()
        {
            TenantId = tenantId,
            Email = "accept-expired@example.com",
            TokenHash = HashToken(rawToken),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Pending,
            InvitedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-3),
        };
        await db.InsertWithInt32IdentityAsync(invitation);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithEmail("accept-expired@example.com")
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync($"/api/v1/invitations/{rawToken}/accept", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("expired");
    }

    [Test]
    public async Task AcceptInvitation_AlreadyAccepted_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);

        string rawToken = "already-accepted-token-value-123";
        TenantInvitation invitation = new()
        {
            TenantId = tenantId,
            Email = "accept-dup@example.com",
            TokenHash = HashToken(rawToken),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Accepted,
            InvitedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        await db.InsertWithInt32IdentityAsync(invitation);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithEmail("accept-dup@example.com")
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync($"/api/v1/invitations/{rawToken}/accept", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("accepted");
    }

    [Test]
    public async Task AcceptInvitation_RevokedInvitation_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedInvitationEnvironment(db);

        string rawToken = "revoked-invitation-token-value-1";
        TenantInvitation invitation = new()
        {
            TenantId = tenantId,
            Email = "accept-revoked@example.com",
            TokenHash = HashToken(rawToken),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Revoked,
            InvitedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        await db.InsertWithInt32IdentityAsync(invitation);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithEmail("accept-revoked@example.com")
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync($"/api/v1/invitations/{rawToken}/accept", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("revoked");
    }

    [Test]
    public async Task AcceptInvitation_NonexistentToken_Returns404()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithEmail("user@example.com")
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/invitations/completely-nonexistent-token/accept", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
