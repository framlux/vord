// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using System.Text.Json;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Verifies the tenant-scope enforcement behavior for endpoints tagged
/// <c>EndpointTags.RequiresTenant</c>: an authenticated request without a resolvable tenant
/// (a global admin has no tenant role claims) is rejected with a 401 ApiResponse envelope,
/// while untagged endpoints keep serving tenant-less requests. The 401 originally came from
/// each endpoint's own null check and now comes from the tenant context pre-processor — this
/// pin must stay green across that move.
/// </summary>
public sealed class RequiresTenantEnforcementTests
{
    [Test]
    public async Task TaggedEndpoint_AuthenticatedWithoutTenantScope_Returns401Envelope()
    {
        using FunctionalTestFactory factory = new();

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .AsGlobalAdmin()
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);

        await Assert.That((int)response.StatusCode).IsEqualTo(401);
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("message").GetString()).IsEqualTo("Unauthorized");
    }

    [Test]
    public async Task UntaggedEndpoint_AuthenticatedWithoutTenantScope_IsNotRejected()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        UserAccount user = new()
        {
            ExternalId = $"ext-no-tenant-{Guid.NewGuid():N}",
            Username = $"notenant-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = true,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId(user.ExternalId)
            .WithEmail(user.Username)
            .AsGlobalAdmin()
            .Build();

        // /auth/me serves any authenticated user and never requires a tenant scope; the
        // pre-processor must leave it untouched for tenant-less principals.
        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/me");

        await Assert.That((int)response.StatusCode).IsEqualTo(200);
    }
}
