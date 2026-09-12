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
/// Functional tests for the ids-only machine selection endpoint, which lets a picker resolve a
/// filter to a complete set of ids without transferring the machines themselves.
/// </summary>
public sealed class MachineIdSelectionEndpointTests
{
    private static async Task<int> SeedTenantWithSubscription(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Selection Tenant {Guid.NewGuid():N}",
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

    private static async Task<long> SeedMachine(
        DatabaseContext db,
        int tenantId,
        string hostname,
        OperatingSystems os = OperatingSystems.Ubuntu,
        short healthStatus = 0)
    {
        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = hostname,
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = os,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId,
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        MachineStateSummary state = new()
        {
            MachineId = machine.Id,
            TenantId = tenantId,
            Name = hostname,
            OperatingSystem = (byte)os,
            MachineType = 0,
            Hostname = hostname,
            HealthStatus = healthStatus,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(state);

        return machine.Id;
    }

    private static HttpClient BuildAuthenticatedClient(FunctionalTestFactory factory, int tenantId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();
    }

    private static async Task<JsonElement> ReadData(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);

        return doc.RootElement.GetProperty("data").Clone();
    }

    private static List<long> ReadIds(JsonElement data)
    {
        return data.GetProperty("ids").EnumerateArray().Select(e => e.GetInt64()).ToList();
    }

    [Test]
    public async Task Ids_NoFilters_ReturnsEveryMachineInTheTenant()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        long first = await SeedMachine(db, tenantId, "sel-one");
        long second = await SeedMachine(db, tenantId, "sel-two");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ids");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonElement data = await ReadData(response);
        List<long> ids = ReadIds(data);

        await Assert.That(ids.Order().ToList()).IsEquivalentTo(new List<long> { first, second }.Order().ToList());
        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(2);
        await Assert.That(data.GetProperty("truncated").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Ids_SearchFilter_ReturnsOnlyTheMatchingMachines()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        long matching = await SeedMachine(db, tenantId, "prod-web-01");
        await SeedMachine(db, tenantId, "staging-db-01");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ids?search=prod-web");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonElement data = await ReadData(response);

        await Assert.That(ReadIds(data)).IsEquivalentTo(new List<long> { matching });
        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Ids_HealthStatusFilter_MatchesTheSearchEndpointsSemantics()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "healthy-host", healthStatus: 0);
        long warningId = await SeedMachine(db, tenantId, "warning-host", healthStatus: 1);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        // A picker whose "select all matching" disagreed with the list it was drawn from would
        // assign machines the user never saw.
        HttpResponseMessage idsResponse = await client.GetAsync("/api/v1/machines/ids?healthStatus=Warning");
        HttpResponseMessage searchResponse = await client.GetAsync("/api/v1/machines/search?healthStatus=Warning");

        JsonElement idsData = await ReadData(idsResponse);
        JsonElement searchData = await ReadData(searchResponse);

        List<long> searchIds = searchData.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("id").GetInt64()).ToList();

        await Assert.That(ReadIds(idsData)).IsEquivalentTo(new List<long> { warningId });
        await Assert.That(ReadIds(idsData).Order().ToList()).IsEquivalentTo(searchIds.Order().ToList());
        await Assert.That(idsData.GetProperty("totalCount").GetInt32())
            .IsEqualTo(searchData.GetProperty("totalCount").GetInt32());
    }

    [Test]
    public async Task Ids_OsFilter_ReturnsOnlyThatOs()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        long ubuntuId = await SeedMachine(db, tenantId, "ubuntu-host", os: OperatingSystems.Ubuntu);
        await SeedMachine(db, tenantId, "debian-host", os: OperatingSystems.Debian);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ids?os=Ubuntu");

        JsonElement data = await ReadData(response);

        await Assert.That(ReadIds(data)).IsEquivalentTo(new List<long> { ubuntuId });
    }

    [Test]
    public async Task Ids_AnotherTenantsMachines_AreNeverReturned()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantA = await SeedTenantWithSubscription(db);
        int tenantB = await SeedTenantWithSubscription(db);
        long mine = await SeedMachine(db, tenantA, "mine-host");
        long theirs = await SeedMachine(db, tenantB, "theirs-host");

        HttpClient client = BuildAuthenticatedClient(factory, tenantA);

        // The caller posts these ids straight back as an assignment, so a leak here would be a
        // cross-tenant write, not merely a disclosure.
        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ids");

        JsonElement data = await ReadData(response);
        List<long> ids = ReadIds(data);

        await Assert.That(ids).IsEquivalentTo(new List<long> { mine });
        await Assert.That(ids).DoesNotContain(theirs);
        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Ids_FilterMatchingNothing_ReturnsAnEmptySelection()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "only-host");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ids?search=nothing-matches-this");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonElement data = await ReadData(response);

        await Assert.That(ReadIds(data)).IsEmpty();
        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(0);
        await Assert.That(data.GetProperty("truncated").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Ids_TenantWithNoMachines_ReturnsAnEmptySelection()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ids");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonElement data = await ReadData(response);

        await Assert.That(ReadIds(data)).IsEmpty();
        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task Ids_Unauthenticated_IsRejected()
    {
        using FunctionalTestFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ids");

        // Asserted as the specific status rather than "not 200": a 500 is also not 200, and this
        // endpoint hands out ids that are posted straight back as an assignment.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}
