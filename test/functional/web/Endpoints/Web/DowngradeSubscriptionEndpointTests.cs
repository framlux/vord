// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the downgrade subscription endpoint.
/// </summary>
public sealed class DowngradeSubscriptionEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedBillingEnvironment(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Pro,
        SubscriptionStatus status = SubscriptionStatus.Active)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Downgrade Tenant {Guid.NewGuid():N}",
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
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-downgrade-user-{Guid.NewGuid():N}",
            Username = $"downgradeuser-{Guid.NewGuid():N}@example.com",
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
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    private static StringContent BuildDowngradeContent(string targetTier)
    {
        return new StringContent(
            JsonSerializer.Serialize(new { targetTier }),
            Encoding.UTF8,
            "application/json");
    }

    [Test]
    public async Task TeamToPro_Returns200_WithSuccessMessage()
    {
        // A Team-tier tenant requesting a downgrade to Pro should receive an immediate
        // successful downgrade response
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db, tier: SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/billing/downgrade",
            BuildDowngradeContent("pro"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsTrue();

        JsonElement data = root.GetProperty("data");
        bool dataSuccess = data.GetProperty("success").GetBoolean();
        await Assert.That(dataSuccess).IsTrue();

        string message = data.GetProperty("message").GetString()!;
        await Assert.That(message).Contains("downgraded to Pro");
    }

    [Test]
    public async Task TeamToFree_Returns200_WithScheduledDowngradeMessage()
    {
        // A Team-tier tenant requesting a downgrade to Free should receive a response
        // indicating the downgrade is scheduled for the end of the billing period
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db, tier: SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/billing/downgrade",
            BuildDowngradeContent("free"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsTrue();

        JsonElement data = root.GetProperty("data");
        bool dataSuccess = data.GetProperty("success").GetBoolean();
        await Assert.That(dataSuccess).IsTrue();

        string message = data.GetProperty("message").GetString()!;
        await Assert.That(message).Contains("end of the current billing period");
    }

    [Test]
    public async Task ProToFree_Returns200_WithScheduledDowngradeMessage()
    {
        // A Pro-tier tenant requesting a downgrade to Free should receive a response
        // indicating the downgrade is scheduled for the end of the billing period
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db, tier: SubscriptionTier.Pro);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/billing/downgrade",
            BuildDowngradeContent("free"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsTrue();

        JsonElement data = root.GetProperty("data");
        bool dataSuccess = data.GetProperty("success").GetBoolean();
        await Assert.That(dataSuccess).IsTrue();

        string message = data.GetProperty("message").GetString()!;
        await Assert.That(message).Contains("end of the current billing period");
    }

    [Test]
    public async Task AlreadyOnFree_Returns400()
    {
        // A tenant already on the Free tier cannot downgrade further in any direction
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db, tier: SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/billing/downgrade",
            BuildDowngradeContent("free"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsFalse();

        string errorMessage = root.GetProperty("message").GetString()!;
        await Assert.That(errorMessage).Contains("Already on the Free tier");
    }

    [Test]
    public async Task ProRequestingPro_Returns400()
    {
        // A tenant already on the Pro tier requesting a downgrade to Pro is not a valid path
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db, tier: SubscriptionTier.Pro);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/billing/downgrade",
            BuildDowngradeContent("pro"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsFalse();

        string errorMessage = root.GetProperty("message").GetString()!;
        await Assert.That(errorMessage).Contains("Already on the Pro tier");
    }

    [Test]
    public async Task InvalidTargetTier_Returns400()
    {
        // A request with an unrecognised target tier value should be rejected before
        // any subscription logic is evaluated
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db, tier: SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/billing/downgrade",
            BuildDowngradeContent("starter"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsFalse();

        string errorMessage = root.GetProperty("message").GetString()!;
        await Assert.That(errorMessage).Contains("Target tier must be 'free' or 'pro'");
    }

    [Test]
    public async Task CanceledSubscription_Returns400()
    {
        // A subscription that has already been fully canceled cannot be downgraded
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(
            db,
            tier: SubscriptionTier.Pro,
            status: SubscriptionStatus.Canceled);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/billing/downgrade",
            BuildDowngradeContent("free"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsFalse();

        string errorMessage = root.GetProperty("message").GetString()!;
        await Assert.That(errorMessage).Contains("Cannot downgrade a canceled subscription");
    }

    [Test]
    public async Task MachineCountExceedsFreeTierLimit_Returns400()
    {
        // When the tenant has more active machines than the Free tier allows,
        // the downgrade to Free must be blocked with an informative error
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db, tier: SubscriptionTier.Pro);

        // Seed a registration token that the machines will reference
        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Downgrade Test Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false
        };
        long tokenId = await db.InsertWithInt64IdentityAsync(token);

        // Seed four machines — one beyond the default Free tier limit of three
        for (int i = 0; i < 4; i++)
        {
            Machine machine = new()
            {
                TenantId = tenantId,
                ApiKeyHash = $"hash-{Guid.NewGuid():N}",
                Name = $"test-host-{i}",
                SerialNumber = $"SN-{Guid.NewGuid():N}",
                SystemId = $"SID-{Guid.NewGuid():N}",
                AssetTagNumber = null,
                MachineType = MachineTypes.BareMetalServer,
                OperatingSystem = OperatingSystems.Ubuntu,
                RegistrationTokenId = tokenId,
                RegisteredOn = DateTimeOffset.UtcNow,
                IsDeleted = false,
            };
            await db.InsertWithInt64IdentityAsync(machine);
        }

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/billing/downgrade",
            BuildDowngradeContent("free"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsFalse();

        string errorMessage = root.GetProperty("message").GetString()!;
        await Assert.That(errorMessage).Contains("Cannot downgrade to Free");
        await Assert.That(errorMessage).Contains("4");
    }

    [Test]
    public async Task MissingSubscription_Returns404()
    {
        // A tenant with no subscription record should receive a 404 Not Found response
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Create a tenant and user without seeding a subscription
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"No-Sub Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        UserAccount user = new()
        {
            ExternalId = $"ext-nosub-user-{Guid.NewGuid():N}",
            Username = $"nosubuser-{Guid.NewGuid():N}@example.com",
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

        HttpClient client = BuildClient(factory, tenant.Id, user.Id);

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/billing/downgrade",
            BuildDowngradeContent("free"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // --- Transactional Audit Log Tests ---

    [Test]
    public async Task TeamToPro_WritesAuditLogEntryAtomically()
    {
        // Intent: for the Team-to-Pro downgrade the subscription update and audit log entry are
        // wrapped in the same transaction. This test confirms the transactional path still records
        // the audit row when the operation succeeds.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db, tier: SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/billing/downgrade",
            BuildDowngradeContent("pro"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using DatabaseContext verifyDb = factory.CreateDbContext();

        AuditLogEntry? auditEntry = await verifyDb.AuditLog
            .Where(a => (a.TenantId == tenantId) && (a.Action == AuditAction.SubscriptionDowngradeRequested))
            .FirstOrDefaultAsync();

        await Assert.That(auditEntry).IsNotNull();
        await Assert.That(auditEntry!.ResourceType).IsEqualTo(AuditResourceType.Subscription);
    }

    [Test]
    public async Task NoTenantClaim_Returns403()
    {
        // A user without an active tenant claim should be rejected by the TenantAdmin
        // policy before the handler executes, resulting in a 403 Forbidden
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).Build();

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/billing/downgrade",
            BuildDowngradeContent("free"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }
}
