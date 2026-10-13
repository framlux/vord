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
/// Functional tests that a single-board computer, machine type 5, is listed, filtered, searched and
/// shown on the wire as the number the web's own enum reads.
/// </summary>
public sealed class SingleBoardComputerEndpointTests
{
    private static async Task<int> SeedTenantWithSubscription(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Single Board Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        await db.InsertWithInt32IdentityAsync(new TenantSubscription
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        return tenant.Id;
    }

    private static async Task<long> SeedMachine(
        DatabaseContext db,
        int tenantId,
        string hostname,
        MachineTypes machineType,
        string? hardwareModel = null)
    {
        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = hostname,
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = machineType,
            OperatingSystem = OperatingSystems.Debian,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId,
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        await db.InsertAsync(new MachineStateSummary
        {
            MachineId = machine.Id,
            TenantId = tenantId,
            Name = hostname,
            OperatingSystem = (byte)OperatingSystems.Debian,
            MachineType = (byte)machineType,
            Hostname = hostname,
            HardwareModel = hardwareModel,
            HealthStatus = 0,
            LastSeenAt = DateTimeOffset.UtcNow,
        });

        return machine.Id;
    }

    private static HttpClient BuildClient(FunctionalTestFactory factory, int tenantId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();
    }

    private static async Task<JsonElement> ReadData(HttpClient client, string url)
    {
        HttpResponseMessage response = await client.GetAsync(url);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return doc.RootElement.GetProperty("data").Clone();
    }

    [Test]
    [Arguments("SingleBoardComputer")]
    [Arguments("singleboardcomputer")]
    public async Task MachineList_TypeFilter_ReturnsOnlyBoards_WithTypeFiveOnTheWire(string type)
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "raspberrypi", MachineTypes.SingleBoardComputer);
        await SeedMachine(db, tenantId, "rack-server", MachineTypes.BareMetalServer);

        JsonElement data = await ReadData(BuildClient(factory, tenantId), $"/api/v1/machines?type={type}");

        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(1);

        JsonElement machine = data.GetProperty("items")[0];
        await Assert.That(machine.GetProperty("name").GetString()).IsEqualTo("raspberrypi");

        // The enum is serialised as its number; the web's MachineType reads exactly this value.
        await Assert.That(machine.GetProperty("machineType").ValueKind).IsEqualTo(JsonValueKind.Number);
        await Assert.That(machine.GetProperty("machineType").GetInt32()).IsEqualTo(5);
    }

    [Test]
    public async Task MachineList_WithoutTypeFilter_ListsBoardsBesideOtherTypes()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "raspberrypi", MachineTypes.SingleBoardComputer);
        await SeedMachine(db, tenantId, "rack-server", MachineTypes.BareMetalServer);

        JsonElement data = await ReadData(BuildClient(factory, tenantId), "/api/v1/machines");

        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(2);
    }

    [Test]
    [Arguments("SingleBoardComputer")]
    [Arguments("singleboardcomputer")]
    public async Task FleetSearch_TypeFilter_ReturnsOnlyBoards_WithTheirModel(string type)
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "raspberrypi", MachineTypes.SingleBoardComputer, "Raspberry Pi 4 Model B Rev 1.4");
        await SeedMachine(db, tenantId, "rack-server", MachineTypes.BareMetalServer, "PowerEdge R640");

        JsonElement data = await ReadData(BuildClient(factory, tenantId), $"/api/v1/machines/search?type={type}");

        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(1);

        JsonElement machine = data.GetProperty("items")[0];
        await Assert.That(machine.GetProperty("name").GetString()).IsEqualTo("raspberrypi");
        await Assert.That(machine.GetProperty("hardwareModel").GetString()).IsEqualTo("Raspberry Pi 4 Model B Rev 1.4");
    }

    [Test]
    public async Task FleetSearch_FreeTextSearch_FindsBoardsByTheirModel()
    {
        // The model is how a Raspberry Pi is told from another board, so the fleet search's free
        // text matches it.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "kitchen", MachineTypes.SingleBoardComputer, "Raspberry Pi 4 Model B Rev 1.4");
        await SeedMachine(db, tenantId, "garage", MachineTypes.SingleBoardComputer, "Orange Pi 5");

        JsonElement data = await ReadData(BuildClient(factory, tenantId), "/api/v1/machines/search?search=raspberry");

        await Assert.That(data.GetProperty("totalCount").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("items")[0].GetProperty("name").GetString()).IsEqualTo("kitchen");
    }

    [Test]
    public async Task MachineDetail_ReportsTypeFive()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        long machineId = await SeedMachine(db, tenantId, "raspberrypi", MachineTypes.SingleBoardComputer, "Raspberry Pi 4 Model B Rev 1.4");

        JsonElement data = await ReadData(BuildClient(factory, tenantId), $"/api/v1/machines/{machineId}");

        await Assert.That(data.GetProperty("machineType").GetInt32()).IsEqualTo(5);
    }
}
