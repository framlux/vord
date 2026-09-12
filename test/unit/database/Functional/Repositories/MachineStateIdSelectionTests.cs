// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Tests for resolving a fleet filter to the set of machine ids it matches.
/// </summary>
public sealed class MachineStateIdSelectionTests
{
    private static IMachineStateRepository CreateRepository(TestDatabaseFactory dbFactory)
    {
        return new Database.Repositories.DatabaseRepository(
            dbFactory.Context,
            new NullLogger<Database.Repositories.DatabaseRepository>());
    }

    private static async Task<int> SeedTenant(TestDatabaseFactory dbFactory)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);

        return await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);
    }

    private static async Task<long> SeedMachine(TestDatabaseFactory dbFactory, int tenantId, short healthStatus = 0)
    {
        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        await dbFactory.Context.InsertAsync(
            TestDataBuilder.BuildMachineStateSummary(machineId: machineId, tenantId: tenantId, healthStatus: healthStatus));

        return machineId;
    }

    [Test]
    public async Task SearchFleetMachineIdsAsync_NoFilters_ReturnsEveryMachineInTheTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = CreateRepository(dbFactory);
        int tenantId = await SeedTenant(dbFactory);

        List<long> seeded = [];
        for (int i = 0; i < 5; i++)
        {
            seeded.Add(await SeedMachine(dbFactory, tenantId));
        }

        (List<long> ids, int totalCount) = await repo.SearchFleetMachineIdsAsync(
            tenantId, new FleetSearchParameters(), 1000);

        await Assert.That(totalCount).IsEqualTo(5);
        await Assert.That(ids.Count).IsEqualTo(5);
        await Assert.That(ids.Order().ToList()).IsEquivalentTo(seeded.Order().ToList());
    }

    [Test]
    public async Task SearchFleetMachineIdsAsync_IgnoresPagingInTheParameters()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = CreateRepository(dbFactory);
        int tenantId = await SeedTenant(dbFactory);

        for (int i = 0; i < 5; i++)
        {
            await SeedMachine(dbFactory, tenantId);
        }

        // Skip and Take describe a page of rows. This answers "what does the filter match", so a
        // page window in the parameters must not shrink the answer.
        (List<long> ids, int totalCount) = await repo.SearchFleetMachineIdsAsync(
            tenantId, new FleetSearchParameters { Skip = 2, Take = 1 }, 1000);

        await Assert.That(totalCount).IsEqualTo(5);
        await Assert.That(ids.Count).IsEqualTo(5);
    }

    [Test]
    public async Task SearchFleetMachineIdsAsync_HealthStatusFilter_ReturnsOnlyMatchingIds()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = CreateRepository(dbFactory);
        int tenantId = await SeedTenant(dbFactory);

        long healthyId = await SeedMachine(dbFactory, tenantId, healthStatus: 0);
        await SeedMachine(dbFactory, tenantId, healthStatus: 1);
        await SeedMachine(dbFactory, tenantId, healthStatus: 2);

        (List<long> ids, int totalCount) = await repo.SearchFleetMachineIdsAsync(
            tenantId, new FleetSearchParameters { HealthStatusValues = [0] }, 1000);

        await Assert.That(totalCount).IsEqualTo(1);
        await Assert.That(ids).IsEquivalentTo(new List<long> { healthyId });
    }

    [Test]
    public async Task SearchFleetMachineIdsAsync_OtherTenantsMachines_AreNeverReturned()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = CreateRepository(dbFactory);
        int tenantA = await SeedTenant(dbFactory);
        int tenantB = await SeedTenant(dbFactory);

        long mineId = await SeedMachine(dbFactory, tenantA);
        long theirsId = await SeedMachine(dbFactory, tenantB);

        // These ids are posted straight back as an assignment, so a leak here is a cross-tenant
        // write primitive, not just an information disclosure.
        (List<long> ids, int totalCount) = await repo.SearchFleetMachineIdsAsync(
            tenantA, new FleetSearchParameters(), 1000);

        await Assert.That(totalCount).IsEqualTo(1);
        await Assert.That(ids).IsEquivalentTo(new List<long> { mineId });
        await Assert.That(ids).DoesNotContain(theirsId);
    }

    [Test]
    public async Task SearchFleetMachineIdsAsync_NoMatches_ReturnsEmptyRatherThanFailing()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = CreateRepository(dbFactory);
        int tenantId = await SeedTenant(dbFactory);
        await SeedMachine(dbFactory, tenantId, healthStatus: 0);

        (List<long> ids, int totalCount) = await repo.SearchFleetMachineIdsAsync(
            tenantId, new FleetSearchParameters { HealthStatusValues = [2] }, 1000);

        await Assert.That(totalCount).IsEqualTo(0);
        await Assert.That(ids).IsEmpty();
    }

    [Test]
    public async Task SearchFleetMachineIdsAsync_MoreMatchesThanTheCap_ReturnsTheCapButCountsThemAll()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = CreateRepository(dbFactory);
        int tenantId = await SeedTenant(dbFactory);

        for (int i = 0; i < 5; i++)
        {
            await SeedMachine(dbFactory, tenantId);
        }

        (List<long> ids, int totalCount) = await repo.SearchFleetMachineIdsAsync(
            tenantId, new FleetSearchParameters(), 3);

        // The count is deliberately unbounded by the cap: it is the only thing that tells a caller
        // its selection is a prefix of the match rather than the whole of it.
        await Assert.That(ids.Count).IsEqualTo(3);
        await Assert.That(totalCount).IsEqualTo(5);
    }

    [Test]
    public async Task SearchFleetMachineIdsAsync_UnderTheCap_ReturnsIdsInAscendingOrder()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = CreateRepository(dbFactory);
        int tenantId = await SeedTenant(dbFactory);

        for (int i = 0; i < 4; i++)
        {
            await SeedMachine(dbFactory, tenantId);
        }

        (List<long> ids, _) = await repo.SearchFleetMachineIdsAsync(
            tenantId, new FleetSearchParameters(), 1000);

        // A stable order makes which ids the cap keeps deterministic, rather than dependent on
        // whichever sort the picker happened to be using. Compared position by position: an
        // order-insensitive assertion here would hold against any ordering at all.
        await Assert.That(ids.Count).IsEqualTo(4);
        for (int i = 1; i < ids.Count; i++)
        {
            await Assert.That(ids[i]).IsGreaterThan(ids[i - 1]);
        }
    }
}
