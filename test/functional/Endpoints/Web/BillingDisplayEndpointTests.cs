// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using LinqToDB;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for billing display endpoints: Invoices, UpcomingInvoice, UsageHistory.
/// These endpoints use NoOpBillingApiClient in tests, so we verify route configuration,
/// auth enforcement, and DTO mapping.
/// </summary>
public sealed class BillingDisplayEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedTenantAndUser(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Pro,
        SubscriptionStatus status = SubscriptionStatus.Active)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Display Tenant {Guid.NewGuid():N}",
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
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-display-{Guid.NewGuid():N}",
            Username = $"display-{Guid.NewGuid():N}@example.com",
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
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        return (tenant.Id, user.Id);
    }

    private static HttpClient BuildViewerClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();
    }

    // ========== Invoices Endpoint ==========

    [Test]
    public async Task Invoices_Unauthenticated_Returns401Or403()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/invoices");

        // Unauthenticated requests get 401 or 403 depending on pipeline ordering
        bool isUnauthorized = (response.StatusCode == HttpStatusCode.Unauthorized) ||
                              (response.StatusCode == HttpStatusCode.Forbidden);
        await Assert.That(isUnauthorized).IsTrue();
    }

    [Test]
    public async Task Invoices_ViewerRole_CanAccess()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/invoices");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Invoices_NoTenantClaim_ReturnsUnauthorized()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/invoices");

        // Without tenant claim, policies reject or handler returns 401
        bool isRejected = (response.StatusCode == HttpStatusCode.Unauthorized) ||
                          (response.StatusCode == HttpStatusCode.Forbidden);
        await Assert.That(isRejected).IsTrue();
    }

    // ========== UpcomingInvoice Endpoint ==========

    [Test]
    public async Task UpcomingInvoice_Unauthenticated_Returns401Or403()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/upcoming-invoice");

        bool isUnauthorized = (response.StatusCode == HttpStatusCode.Unauthorized) ||
                              (response.StatusCode == HttpStatusCode.Forbidden);
        await Assert.That(isUnauthorized).IsTrue();
    }

    [Test]
    public async Task UpcomingInvoice_NoActiveSubscription_ReturnsNoInvoice()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db, tier: SubscriptionTier.Free);
        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/upcoming-invoice");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        bool hasInvoice = doc.RootElement.GetProperty("data").GetProperty("hasInvoice").GetBoolean();
        await Assert.That(hasInvoice).IsFalse();
    }

    // ========== UsageHistory Endpoint ==========

    [Test]
    public async Task UsageHistory_Unauthenticated_Returns401Or403()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/usage-history");

        bool isUnauthorized = (response.StatusCode == HttpStatusCode.Unauthorized) ||
                              (response.StatusCode == HttpStatusCode.Forbidden);
        await Assert.That(isUnauthorized).IsTrue();
    }

    [Test]
    public async Task UsageHistory_NewTenant_DefaultsSixMonthsOfDataPoints()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/usage-history");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        bool success = doc.RootElement.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        // Default is 6 months of data points when no months param is specified
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetArrayLength()).IsEqualTo(6);
    }

    // ========== UsageHistory months parameter tests ==========

    [Test]
    public async Task UsageHistory_CustomMonthsParam_ReturnsCorrectCount()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/usage-history?months=3");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetArrayLength()).IsEqualTo(3);
    }

    [Test]
    public async Task UsageHistory_MonthsParamMax12_Returns12Points()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/usage-history?months=12");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetArrayLength()).IsEqualTo(12);
    }

    [Test]
    public async Task UsageHistory_MonthsParamExceeds12_DefaultsTo6()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        // months=13 is out of range (> 12), so should fall back to default of 6
        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/usage-history?months=13");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetArrayLength()).IsEqualTo(6);
    }

    [Test]
    public async Task UsageHistory_MonthsParamZero_DefaultsTo6()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        // months=0 is out of range (<= 0), so should fall back to default of 6
        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/usage-history?months=0");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetArrayLength()).IsEqualTo(6);
    }

    [Test]
    public async Task UsageHistory_MonthsParamNegative_DefaultsTo6()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/usage-history?months=-1");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetArrayLength()).IsEqualTo(6);
    }

    [Test]
    public async Task UsageHistory_EachPointHasMonthAndMachineCount()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/usage-history?months=1");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetArrayLength()).IsEqualTo(1);

        JsonElement point = data[0];
        // Verify the structure has expected properties
        await Assert.That(point.TryGetProperty("month", out _)).IsTrue();
        await Assert.That(point.TryGetProperty("machineCount", out _)).IsTrue();
        await Assert.That(point.TryGetProperty("invoiceAmountCents", out _)).IsTrue();
    }

    [Test]
    public async Task UsageHistory_NoTenantClaim_ReturnsUnauthorized()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/usage-history");

        bool isRejected = (response.StatusCode == HttpStatusCode.Unauthorized) ||
                          (response.StatusCode == HttpStatusCode.Forbidden);
        await Assert.That(isRejected).IsTrue();
    }

    [Test]
    public async Task UsageHistory_TenantNotInDatabase_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        // Delete the tenant from the database after creating the client
        await db.Tenants.Where(t => t.Id == tenantId).DeleteAsync();

        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/usage-history");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // ========== UpcomingInvoice additional tests ==========

    [Test]
    public async Task UpcomingInvoice_TenantNotInDatabase_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        // Delete the tenant from the database after creating the client
        await db.Tenants.Where(t => t.Id == tenantId).DeleteAsync();

        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/upcoming-invoice");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UpcomingInvoice_NoTenantClaim_ReturnsUnauthorized()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/upcoming-invoice");

        bool isRejected = (response.StatusCode == HttpStatusCode.Unauthorized) ||
                          (response.StatusCode == HttpStatusCode.Forbidden);
        await Assert.That(isRejected).IsTrue();
    }

    [Test]
    public async Task UpcomingInvoice_ResponseContainsExpectedFields()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);
        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/upcoming-invoice");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        // NoOpBillingApiClient returns null -> HasInvoice = false
        bool hasInvoice = data.GetProperty("hasInvoice").GetBoolean();
        await Assert.That(hasInvoice).IsFalse();
        // When no invoice, amounts should be zero
        long amount = data.GetProperty("amountDueCents").GetInt64();
        await Assert.That(amount).IsEqualTo(0);
    }

    // ========== Invoices additional tests ==========

    [Test]
    public async Task Invoices_TenantNotInDatabase_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db);

        // Delete the tenant from the database after creating the client
        await db.Tenants.Where(t => t.Id == tenantId).DeleteAsync();

        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/invoices");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

}
