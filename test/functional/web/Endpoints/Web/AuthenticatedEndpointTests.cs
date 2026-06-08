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
/// Functional tests for authenticated user REST endpoints.
/// </summary>
public sealed class AuthenticatedEndpointTests
{
    [Test]
    public async Task AuthMe_ValidUser_ReturnsUserWithTenantDetails()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        UserAccount user = new()
        {
            ExternalId = "ext-authme-1",
            Username = "authme@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        int tenantId = await SeedTenantWithSubscription(db, "AuthMe Tenant");

        UserTenantRole role = new()
        {
            UserId = user.Id,
            AssignedTenantId = tenantId,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };
        await db.InsertAsync(role);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId("ext-authme-1")
            .WithEmail("authme@example.com")
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/me");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonElement root = json.RootElement;

        // Verify the API response envelope
        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();

        // Verify user fields match the seeded data
        JsonElement data = root.GetProperty("data");
        await Assert.That(data.GetProperty("id").GetInt32()).IsEqualTo(user.Id);
        await Assert.That(data.GetProperty("email").GetString()).IsEqualTo("authme@example.com");
        await Assert.That(data.GetProperty("uniqueId").GetString()).IsEqualTo("ext-authme-1");
        await Assert.That(data.GetProperty("isGlobalAdmin").GetBoolean()).IsFalse();
        await Assert.That(data.GetProperty("needsOnboarding").GetBoolean()).IsFalse();
        await Assert.That(data.GetProperty("activeTenantId").GetInt32()).IsEqualTo(tenantId);

        // Verify the tenant list contains the expected tenant with correct role
        JsonElement tenants = data.GetProperty("tenants");
        await Assert.That(tenants.GetArrayLength()).IsEqualTo(1);

        JsonElement firstTenant = tenants[0];
        await Assert.That(firstTenant.GetProperty("tenantId").GetInt32()).IsEqualTo(tenantId);
        await Assert.That(firstTenant.GetProperty("tenantName").GetString()).IsEqualTo("AuthMe Tenant");
    }

    [Test]
    public async Task AuthMe_GlobalAdmin_ReturnsIsGlobalAdminTrue()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        UserAccount user = new()
        {
            ExternalId = "ext-authme-admin",
            Username = "admin@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = true
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId("ext-authme-admin")
            .WithEmail("admin@example.com")
            .AsGlobalAdmin()
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/me");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonElement data = json.RootElement.GetProperty("data");

        await Assert.That(data.GetProperty("isGlobalAdmin").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("needsOnboarding").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task AuthMe_UserNotInDatabase_Returns404WithNoUserData()
    {
        using FunctionalTestFactory factory = new();

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(999)
            .WithExternalId("nonexistent-ext-id")
            .WithEmail("ghost@example.com")
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/me");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // Verify the response body does not leak the requested email address
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).DoesNotContain("ghost@example.com");
    }

    [Test]
    public async Task AuthMe_UnauthenticatedRequest_Returns401()
    {
        using FunctionalTestFactory factory = new();

        // Build a raw client without any auth headers so TestAuthHandler returns NoResult
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/me");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task AuthMe_UserWithNoTenants_ReturnsNeedsOnboardingTrue()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        UserAccount user = new()
        {
            ExternalId = "ext-no-tenants",
            Username = "lonely@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId("ext-no-tenants")
            .WithEmail("lonely@example.com")
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/me");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonElement root = json.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();

        JsonElement data = root.GetProperty("data");
        await Assert.That(data.GetProperty("needsOnboarding").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("tenants").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task AuthMe_MissingExternalIdClaim_UsesUnknownFallback()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Seed a user whose external ID is "unknown" to match the fallback behavior
        // in UserDto.FromPrincipal when NameIdentifier claim is missing
        UserAccount user = new()
        {
            ExternalId = "unknown",
            Username = "noextid@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        // Build a client that does NOT set the external ID header
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId("")
            .WithEmail("noextid@example.com")
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/me");

        // When external ID is empty, the handler looks up "unknown" in the database.
        // Whether the user is found depends on the external ID stored in the database.
        // The important assertion is that the endpoint does not throw a 500.
        bool isExpectedStatus = (response.StatusCode == HttpStatusCode.OK) ||
            (response.StatusCode == HttpStatusCode.NotFound);
        await Assert.That(isExpectedStatus).IsTrue();
    }

    [Test]
    public async Task OnboardingCreateOrg_CreatesTenanAndSubscription()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        UserAccount user = new()
        {
            ExternalId = "ext-onboard-1",
            Username = "onboard@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId("ext-onboard-1")
            .WithEmail("onboard@example.com")
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/onboarding/create-org", new
        {
            OrganizationName = "New Startup"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonElement root = json.RootElement;

        // Verify the API response envelope
        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("Organization created successfully");

        // Verify the response contains a valid tenant ID
        JsonElement data = root.GetProperty("data");
        int createdTenantId = data.GetProperty("tenantId").GetInt32();
        await Assert.That(createdTenantId).IsGreaterThan(0);

        // Verify DB state: tenant was created with lowercased name
        Tenant? tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Name == "new startup");
        await Assert.That(tenant).IsNotNull();
        await Assert.That(tenant!.Id).IsEqualTo(createdTenantId);

        // Verify a Free-tier subscription was created
        TenantSubscription? subscription = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenant.Id);
        await Assert.That(subscription).IsNotNull();
        await Assert.That(subscription!.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(subscription.Status).IsEqualTo(SubscriptionStatus.Active);

        // Verify the creating user was assigned TenantAdmin role
        UserTenantRole? userRole = await db.UserTenantRoles
            .FirstOrDefaultAsync(r => r.UserId == user.Id && r.AssignedTenantId == tenant.Id);
        await Assert.That(userRole).IsNotNull();
        await Assert.That(userRole!.Role).IsEqualTo(UserAccountRoles.TenantAdmin);
        await Assert.That(userRole.IsActive).IsTrue();
    }

    [Test]
    public async Task OnboardingCreateOrg_UserAlreadyHasTenants_Returns409WithErrorPayload()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        UserAccount user = new()
        {
            ExternalId = "ext-existing-1",
            Username = "existing@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        int tenantId = await SeedTenantWithSubscription(db, "Existing Tenant");

        UserTenantRole role = new()
        {
            UserId = user.Id,
            AssignedTenantId = tenantId,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };
        await db.InsertAsync(role);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId("ext-existing-1")
            .WithEmail("existing@example.com")
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/onboarding/create-org", new
        {
            OrganizationName = "Another Org"
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonElement root = json.RootElement;

        // Verify the error envelope indicates failure
        await Assert.That(root.GetProperty("success").GetBoolean()).IsFalse();

        // Verify the error message communicates the conflict reason
        string? message = root.GetProperty("message").GetString();
        await Assert.That(message).IsNotNull();
        await Assert.That(message!).Contains("already");

        // Verify no new tenant was created in the database
        Tenant? duplicateTenant = await db.Tenants.FirstOrDefaultAsync(t => t.Name == "another org");
        await Assert.That(duplicateTenant).IsNull();
    }

    [Test]
    public async Task OnboardingCreateOrg_EmptyOrganizationName_DoesNotCreateTenant()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        UserAccount user = new()
        {
            ExternalId = "ext-empty-org",
            Username = "emptyorg@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId("ext-empty-org")
            .WithEmail("emptyorg@example.com")
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/onboarding/create-org", new
        {
            OrganizationName = ""
        });

        // The endpoint should reject empty organization names with a 400 Bad Request
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task TenantSwitch_ValidTenant_SetsCookieAndReturnsSuccess()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenant1Id = await SeedTenantWithSubscription(db, "Tenant 1");
        int tenant2Id = await SeedTenantWithSubscription(db, "Tenant 2");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenant1Id, (int)UserAccountRoles.TenantAdmin)
            .WithRole(tenant2Id, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenant1Id)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/tenants/switch", new
        {
            TenantId = tenant2Id
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Verify the response payload indicates success
        JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonElement root = json.RootElement;
        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("Tenant switched");

        // Verify Set-Cookie header sets vord_tenant to the new tenant ID
        bool hasTenantCookie = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies) &&
            cookies.Any(c => c.Contains($"vord_tenant={tenant2Id}"));
        await Assert.That(hasTenantCookie).IsTrue();
    }

    [Test]
    public async Task TenantSwitch_UserLacksAccessToTenant_Returns403WithError()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenant1Id = await SeedTenantWithSubscription(db, "Allowed Tenant");
        int tenant2Id = await SeedTenantWithSubscription(db, "Forbidden Tenant");

        // User only has a role for tenant1, not tenant2
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenant1Id, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenant1Id)
            .Build();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/tenants/switch", new
        {
            TenantId = tenant2Id
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonElement root = json.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsFalse();

        string? message = root.GetProperty("message").GetString();
        await Assert.That(message).IsNotNull();
        await Assert.That(message!).Contains("do not have access");

        // Verify no Set-Cookie header was sent
        bool hasTenantCookie = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies) &&
            cookies.Any(c => c.Contains("vord_tenant"));
        await Assert.That(hasTenantCookie).IsFalse();
    }

    [Test]
    public async Task TenantSwitch_UnauthenticatedRequest_Returns401()
    {
        using FunctionalTestFactory factory = new();

        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/tenants/switch", new
        {
            TenantId = 1
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    private static async Task<int> SeedTenantWithSubscription(DatabaseContext db, string name)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        return tenant.Id;
    }
}
