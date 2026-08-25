// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.Vord.BillingGrpc;
using LinqToDB;
using NSubstitute;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the billing catalog endpoint. The catalog is deliberately available
/// to Free-tier tenants (it powers the upgrade pricing cards) and returns an empty list
/// when billing is disabled.
/// </summary>
public sealed class BillingCatalogEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedTenantAndUser(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Free)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Catalog Tenant {Guid.NewGuid():N}",
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
            ExternalId = $"ext-catalog-{Guid.NewGuid():N}",
            Username = $"catalog-{Guid.NewGuid():N}@example.com",
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

    private static List<CatalogItemResult> SampleCatalog()
    {
        return
        [
            new CatalogItemResult(BillingTier.Pro, BillingInterval.Monthly, 300, "usd"),
            new CatalogItemResult(BillingTier.Pro, BillingInterval.Annual, 3000, "usd"),
            new CatalogItemResult(BillingTier.Team, BillingInterval.Monthly, 500, "usd"),
        ];
    }

    [Test]
    public async Task Catalog_Unauthenticated_Returns401Or403()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/catalog");

        bool isUnauthorized = (response.StatusCode == HttpStatusCode.Unauthorized) ||
                              (response.StatusCode == HttpStatusCode.Forbidden);
        await Assert.That(isUnauthorized).IsTrue();
    }

    [Test]
    public async Task Catalog_NoTenantClaim_IsRejected()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/catalog");

        bool isRejected = (response.StatusCode == HttpStatusCode.Unauthorized) ||
                          (response.StatusCode == HttpStatusCode.Forbidden);
        await Assert.That(isRejected).IsTrue();
    }

    [Test]
    public async Task Catalog_FreeTierTenant_ReturnsMappedItems()
    {
        // Free tenants are deliberately allowed: the catalog powers the upgrade pricing cards
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db, SubscriptionTier.Free);
        factory.BillingApiClientMock.GetPublicCatalogAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SampleCatalog()));
        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/catalog");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetArrayLength()).IsEqualTo(3);
        await Assert.That(data[0].GetProperty("tier").GetString()).IsEqualTo("Pro");
        await Assert.That(data[0].GetProperty("interval").GetString()).IsEqualTo("monthly");
        await Assert.That(data[0].GetProperty("unitAmountCents").GetInt64()).IsEqualTo(300);
        await Assert.That(data[0].GetProperty("currency").GetString()).IsEqualTo("usd");
        // Every price is licensed per-machine, so the catalog must not advertise a metered flag.
        await Assert.That(data[0].TryGetProperty("isMetered", out JsonElement _)).IsFalse();
        await Assert.That(data[1].GetProperty("interval").GetString()).IsEqualTo("annual");
        await Assert.That(data[2].GetProperty("tier").GetString()).IsEqualTo("Team");
    }

    /// <summary>
    /// A self-hosted deployment sells nothing, so it has no prices to list. This previously answered
    /// 200 with an empty array, which reads as "we have no plans for sale" rather than "this
    /// product is not sold".
    /// </summary>
    [Test]
    public async Task Catalog_SelfHosted_Returns404()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db, SubscriptionTier.Free);
        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/catalog");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
