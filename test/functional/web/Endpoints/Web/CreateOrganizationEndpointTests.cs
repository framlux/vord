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
/// Functional tests for self-service organization creation (<c>POST /api/v1/onboarding/create-org</c>).
/// Exercises the validator (length + blocked characters) and the handler's business rules
/// (one-org-per-user, unique tenant name, Free-tier default).
/// </summary>
public sealed class CreateOrganizationEndpointTests
{
    private static async Task<UserAccount> SeedNewUserWithoutTenant(DatabaseContext db)
    {
        UserAccount user = new()
        {
            ExternalId = $"ext-onboarding-{Guid.NewGuid():N}",
            Username = $"onboarding-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        return user;
    }

    private static HttpClient BuildAuthenticatedNoTenantClient(FunctionalTestFactory factory, UserAccount user)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId(user.ExternalId)
            .WithEmail(user.Username)
            .Build();
    }

    [Test]
    public async Task CreateOrg_ValidName_CreatesTenantAndReturnsId()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        UserAccount user = await SeedNewUserWithoutTenant(db);

        HttpClient client = BuildAuthenticatedNoTenantClient(factory, user);
        string orgName = $"Org {Guid.NewGuid():N}".Substring(0, 30);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/onboarding/create-org", new
        {
            OrganizationName = orgName,
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();
        int tenantId = root.GetProperty("data").GetProperty("tenantId").GetInt32();
        await Assert.That(tenantId).IsGreaterThan(0);

        // Verify the persisted side effects: tenant exists, user is TenantAdmin, Free subscription.
        // The TenantRepository.CreateTenantAsync normalizes name to lowercase before persisting.
        Tenant? created = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
        await Assert.That(created).IsNotNull();
        await Assert.That(created!.Name).IsEqualTo(orgName.ToLowerInvariant());
        await Assert.That(created.CreatedByUserId).IsEqualTo(user.Id);
        await Assert.That(created.IsActive).IsTrue();

        UserTenantRole? role = await db.UserTenantRoles
            .FirstOrDefaultAsync(r => (r.UserId == user.Id) && (r.AssignedTenantId == tenantId));
        await Assert.That(role).IsNotNull();
        await Assert.That(role!.Role).IsEqualTo(UserAccountRoles.TenantAdmin);

        TenantSubscription? subscription = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        await Assert.That(subscription).IsNotNull();
        await Assert.That(subscription!.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(subscription.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task CreateOrg_EmptyName_Returns400WithRequiredMessage()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        UserAccount user = await SeedNewUserWithoutTenant(db);

        HttpClient client = BuildAuthenticatedNoTenantClient(factory, user);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/onboarding/create-org", new
        {
            OrganizationName = "",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("message").GetString()).IsEqualTo("Organization name is required");
    }

    [Test]
    public async Task CreateOrg_NameTooShort_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        UserAccount user = await SeedNewUserWithoutTenant(db);

        HttpClient client = BuildAuthenticatedNoTenantClient(factory, user);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/onboarding/create-org", new
        {
            OrganizationName = "abcd",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("between 5 and 100");
    }

    [Test]
    public async Task CreateOrg_NameTooLong_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        UserAccount user = await SeedNewUserWithoutTenant(db);

        HttpClient client = BuildAuthenticatedNoTenantClient(factory, user);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/onboarding/create-org", new
        {
            OrganizationName = new string('a', 101),
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("between 5 and 100");
    }

    [Test]
    public async Task CreateOrg_NameContainsBlockedCharacter_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        UserAccount user = await SeedNewUserWithoutTenant(db);

        HttpClient client = BuildAuthenticatedNoTenantClient(factory, user);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/onboarding/create-org", new
        {
            OrganizationName = "Acme<script>",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("not allowed");
    }

    [Test]
    public async Task CreateOrg_DuplicateName_Returns409()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string sharedName = $"Duplicate Org {Guid.NewGuid():N}".Substring(0, 30);

        // First user takes the name.
        UserAccount firstUser = await SeedNewUserWithoutTenant(db);
        HttpClient firstClient = BuildAuthenticatedNoTenantClient(factory, firstUser);
        HttpResponseMessage firstResponse = await firstClient.PostAsJsonAsync("/api/v1/onboarding/create-org", new
        {
            OrganizationName = sharedName,
        });
        await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Second user attempts the same name — should be rejected.
        UserAccount secondUser = await SeedNewUserWithoutTenant(db);
        HttpClient secondClient = BuildAuthenticatedNoTenantClient(factory, secondUser);
        HttpResponseMessage secondResponse = await secondClient.PostAsJsonAsync("/api/v1/onboarding/create-org", new
        {
            OrganizationName = sharedName,
        });

        await Assert.That(secondResponse.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        string body = await secondResponse.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("already exists");
    }

    [Test]
    public async Task CreateOrg_UserAlreadyHasOrg_Returns409()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        UserAccount user = await SeedNewUserWithoutTenant(db);

        // Seed a pre-existing membership so the handler short-circuits.
        Tenant priorTenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Prior Org {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = user.Id,
            IsActive = true,
            LogoUrl = ""
        };
        priorTenant.Id = await db.InsertWithInt32IdentityAsync(priorTenant);
        await db.InsertAsync(new UserTenantRole
        {
            UserId = user.Id,
            AssignedTenantId = priorTenant.Id,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        });

        HttpClient client = BuildAuthenticatedNoTenantClient(factory, user);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/onboarding/create-org", new
        {
            OrganizationName = $"New Org {Guid.NewGuid():N}".Substring(0, 20),
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("already belong");
    }

    [Test]
    public async Task CreateOrg_Unauthenticated_Returns401()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/onboarding/create-org", new
        {
            OrganizationName = "Some Valid Org Name",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}
