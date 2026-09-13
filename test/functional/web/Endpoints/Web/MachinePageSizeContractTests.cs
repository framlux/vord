// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the page-size contract shared by the machine collection endpoints.
/// A caller that asks for more than an endpoint will serve is refused rather than handed a
/// short page, because a silently reduced page reads exactly like the end of the collection.
/// </summary>
public sealed class MachinePageSizeContractTests
{
    private static async Task<int> SeedTenantWithSubscription(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"PageSize Tenant {Guid.NewGuid():N}",
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

        return tenant.Id;
    }

    private static async Task SeedMachine(DatabaseContext db, int tenantId, string hostname)
    {
        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = hostname,
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId,
        };
        await db.InsertWithInt64IdentityAsync(machine);
    }

    private static HttpClient BuildAuthenticatedClient(FunctionalTestFactory factory, int tenantId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();
    }

    private static HttpClient BuildAdminClient(FunctionalTestFactory factory, int tenantId)
    {
        // Authorisation runs before the handler, so a Viewer would be refused by the
        // registration-tokens endpoint before its page size was ever read.
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    // ========== Over the ceiling is refused, not quietly reduced ==========

    [Test]
    public async Task List_PageSizeAboveTheCeiling_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "ceiling-list");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines?pageSize={PaginationLimits.MaxPageSize + 1}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Search_PageSizeAboveTheCeiling_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "ceiling-search");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines/search?pageSize={PaginationLimits.MaxPageSize + 1}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task List_PageSizeAboveTheCeiling_NamesTheCeilingInTheError()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines?pageSize=1000");

        // Assert the refusal before reading the body. A successful response echoes the served
        // page size, so a body-only assertion would pass against the very behaviour being removed.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();

        // A caller who guessed 1000 has to learn the real number from the refusal, or the
        // refusal just moves the guessing game one step later.
        await Assert.That(body).Contains(PaginationLimits.MaxPageSize.ToString());
    }

    // ========== The boundary itself is servable ==========

    [Test]
    public async Task List_PageSizeAtTheCeiling_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "at-ceiling-list");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines?pageSize={PaginationLimits.MaxPageSize}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("pageSize").GetInt32()).IsEqualTo(PaginationLimits.MaxPageSize);
    }

    [Test]
    public async Task Search_PageSizeAtTheCeiling_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "at-ceiling-search");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines/search?pageSize={PaginationLimits.MaxPageSize}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("pageSize").GetInt32()).IsEqualTo(PaginationLimits.MaxPageSize);
    }

    // ========== Nonsense page sizes are refused too ==========

    [Test]
    public async Task List_PageSizeZero_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines?pageSize=0");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Search_PageSizeNegative_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/search?pageSize=-5");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // ========== Omitting the parameter still works ==========

    [Test]
    public async Task List_NoPageSize_UsesTheDefault()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "default-list");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("pageSize").GetInt32()).IsEqualTo(PaginationLimits.DefaultPageSize);
    }

    // ========== A low page number is a floor, not a truncation ==========

    [Test]
    public async Task List_PageBelowOne_IsFlooredRatherThanRefused()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "floor-list");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        // Unlike a reduced page size, clamping the page to 1 cannot hide rows from the caller:
        // page 1 is where they would have started anyway.
        HttpResponseMessage response = await client.GetAsync("/api/v1/machines?page=-1");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("page").GetInt32()).IsEqualTo(1);
    }

    // ========== The rule is the same on every paginated collection ==========

    [Test]
    public async Task DashboardFleet_PageSizeAboveTheCeiling_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/dashboard/fleet?pageSize={PaginationLimits.MaxPageSize + 1}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task DashboardFleet_PageSizeAtTheCeiling_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/dashboard/fleet?pageSize={PaginationLimits.MaxPageSize}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task RegistrationTokens_PageSizeAboveTheCeiling_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAdminClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines/registration-tokens?pageSize={PaginationLimits.MaxPageSize + 1}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CommandHistory_PageSizeAboveTheCeiling_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "command-host");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines/1/commands?pageSize={PaginationLimits.MaxPageSize + 1}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task EveryPaginatedCollection_RefusesTheSameValue()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "uniform-host");

        HttpClient client = BuildAdminClient(factory, tenantId);

        // The point of the shared rule is that a caller learns it once. A single endpoint drifting
        // back to a silent clamp would be invisible in its own test file but is caught here.
        string[] paths =
        [
            "/api/v1/machines",
            "/api/v1/machines/search",
            "/api/v1/dashboard/fleet",
            "/api/v1/machines/registration-tokens",
            "/api/v1/machines/1/commands",
        ];

        foreach (string path in paths)
        {
            HttpResponseMessage response = await client.GetAsync($"{path}?pageSize=1000");

            await Assert.That(response.StatusCode)
                .IsEqualTo(HttpStatusCode.BadRequest)
                .Because($"{path} must refuse an over-limit page size rather than serve a short one");
        }
    }
}
