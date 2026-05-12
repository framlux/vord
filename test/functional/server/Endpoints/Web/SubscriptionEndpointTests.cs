// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the SubscriptionEndpoint.
/// </summary>
public sealed class SubscriptionEndpointTests
{
    [Test]
    public async Task GetSubscription_FreeTier_ReturnsTierFreeWithMachineLimit3AndRetention1()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Free, 3, 1);

        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonElement data = await ExtractDataElement(response);
        await Assert.That(data.GetProperty("tier").GetString()).IsEqualTo("Free");
        await Assert.That(data.GetProperty("machineLimit").GetInt32()).IsEqualTo(3);
        await Assert.That(data.GetProperty("retentionDays").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("status").GetString()).IsEqualTo("Active");
        await Assert.That(data.GetProperty("cancelAtPeriodEnd").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task GetSubscription_ProTier_ReturnsTierProWithExpectedRetention()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Pro, null, 60);

        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonElement data = await ExtractDataElement(response);
        await Assert.That(data.GetProperty("tier").GetString()).IsEqualTo("Pro");
        await Assert.That(data.GetProperty("retentionDays").GetInt32()).IsEqualTo(60);
        await Assert.That(data.GetProperty("machineLimit").GetInt32()).IsEqualTo(1000);
    }

    [Test]
    public async Task GetSubscription_TeamTier_ReturnsTierTeamWithRetention365AndNullMachineLimit()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Team, null, 365);

        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonElement data = await ExtractDataElement(response);
        await Assert.That(data.GetProperty("tier").GetString()).IsEqualTo("Team");
        await Assert.That(data.GetProperty("retentionDays").GetInt32()).IsEqualTo(365);
        await Assert.That(data.GetProperty("machineLimit").GetInt32()).IsEqualTo(10000);
    }

    [Test]
    public async Task GetSubscription_PastDueStatus_ReturnsStatusPastDue()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(
            db,
            SubscriptionTier.Pro,
            null,
            60,
            status: SubscriptionStatus.PastDue);

        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonElement data = await ExtractDataElement(response);
        await Assert.That(data.GetProperty("status").GetString()).IsEqualTo("PastDue");
        await Assert.That(data.GetProperty("tier").GetString()).IsEqualTo("Pro");
    }

    [Test]
    public async Task GetSubscription_CanceledStatus_ReturnsStatusCanceled()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(
            db,
            SubscriptionTier.Pro,
            null,
            60,
            status: SubscriptionStatus.Canceled);

        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonElement data = await ExtractDataElement(response);
        await Assert.That(data.GetProperty("status").GetString()).IsEqualTo("Canceled");
    }

    [Test]
    public async Task GetSubscription_WithCurrentPeriodEnd_ReturnsDateInResponse()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        DateTimeOffset periodEnd = new(2026, 04, 15, 0, 0, 0, TimeSpan.Zero);
        int tenantId = await SeedTenantWithSubscription(
            db,
            SubscriptionTier.Pro,
            null,
            60,
            currentPeriodEnd: periodEnd);

        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonElement data = await ExtractDataElement(response);
        string? currentPeriodEndStr = data.GetProperty("currentPeriodEnd").GetString();
        await Assert.That(currentPeriodEndStr).IsNotNull();

        // Parse the returned value and verify it matches the seeded date
        DateTimeOffset parsedDate = DateTimeOffset.Parse(currentPeriodEndStr!);
        await Assert.That(parsedDate).IsEqualTo(periodEnd);
    }

    [Test]
    public async Task GetSubscription_WithoutCurrentPeriodEnd_ReturnsNullDate()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Free, 3, 1);

        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonElement data = await ExtractDataElement(response);
        await Assert.That(data.GetProperty("currentPeriodEnd").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task GetSubscription_DefaultCancelAtPeriodEnd_ReturnsFalseInDto()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Pro, null, 60);

        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonElement data = await ExtractDataElement(response);
        await Assert.That(data.GetProperty("cancelAtPeriodEnd").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task GetSubscription_NoSubscription_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Create tenant without subscription
        Tenant tenant = new()
        {
            Name = "No Sub Tenant",
            ExternalId = $"ext-nosub-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        int tenantId = (int)(long)await db.InsertWithIdentityAsync(tenant);

        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.Contains("\"success\":true")).IsFalse();
    }

    [Test]
    public async Task GetSubscription_Unauthenticated_Returns401()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body.Contains("\"success\":true")).IsFalse();
    }

    [Test]
    public async Task GetSubscription_MachineCountIsZero_WhenNoMachinesRegistered()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Pro, null, 60);

        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonElement data = await ExtractDataElement(response);
        await Assert.That(data.GetProperty("machineCount").GetInt32()).IsEqualTo(0);
    }

    // ========== Helpers ==========

    private static HttpClient BuildViewerClient(FunctionalTestFactory factory, int tenantId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithExternalId($"ext-sub-{Guid.NewGuid():N}")
            .WithEmail("sub@example.com")
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();
    }

    /// <summary>
    /// Extracts the "data" element from the API response JSON, asserting the outer success flag.
    /// </summary>
    private static async Task<JsonElement> ExtractDataElement(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        // Verify the outer envelope indicates success
        await Assert.That(success).IsTrue();

        return root.GetProperty("data");
    }

    private static async Task<int> SeedTenantWithSubscription(
        DatabaseContext db,
        SubscriptionTier tier,
        int? machineLimit,
        int retentionDays,
        SubscriptionStatus status = SubscriptionStatus.Active,
        DateTimeOffset? currentPeriodEnd = null)
    {
        Tenant tenant = new()
        {
            Name = $"Test Tenant {Guid.NewGuid():N}",
            ExternalId = $"ext-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        int tenantId = (int)(long)await db.InsertWithIdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = tier,
            Status = status,
            CurrentPeriodEnd = currentPeriodEnd,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(subscription);

        return tenantId;
    }
}
