// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Interceptors;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;

namespace Framlux.FleetManagement.Test.Repositories;

/// <summary>
/// Tests the registered-machine cutoff query that decides which of a tenant's hosts are paused for
/// exceeding its machine limit: the oldest machines by id stay active, so one id describes the set.
/// </summary>
public sealed class MachineCutoffQueryTests
{
    [Test]
    [Arguments(5, 3, 3, null)]   // under the limit: nothing paused
    [Arguments(3, 3, 3, null)]   // exactly at the limit: nothing paused
    [Arguments(3, 7, 7, 3)]      // over: the cutoff is the 3rd registered machine
    [Arguments(0, 4, 4, null)]   // limit zero: the caller treats it as all-paused, no cutoff needed
    public async Task GetRegisteredMachineCutoff_ReturnsCountAndTheLimitthMachine(int limit, int machines, int expectedCount, int? expectedCutoffOrdinal)
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());
        List<long> ids = [];
        for (int i = 0; i < machines; i++)
        {
            ids.Add(await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 1)));
        }

        // Another tenant's machines and a deleted one must never count.
        await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 2));
        Machine deleted = TestDataBuilder.BuildMachine(tenantId: 1);
        deleted.IsDeleted = true;
        await dbFactory.Context.InsertWithInt64IdentityAsync(deleted);

        (int count, long? cutoff) = await repo.GetRegisteredMachineCutoffAsync(1, limit, CancellationToken.None);

        await Assert.That(count).IsEqualTo(expectedCount);
        await Assert.That(cutoff).IsEqualTo(expectedCutoffOrdinal is null ? null : ids[expectedCutoffOrdinal.Value - 1]);
    }

    [Test]
    public async Task GetRegisteredMachineCutoff_DeletedMachineBeforeTheLimit_DoesNotShiftTheCutoff()
    {
        // A deleted machine is no longer registered, so it must not occupy one of the tenant's
        // active slots: with a limit of two the cutoff is the second live machine, not the second row.
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());
        Machine deleted = TestDataBuilder.BuildMachine(tenantId: 1);
        deleted.IsDeleted = true;
        await dbFactory.Context.InsertWithInt64IdentityAsync(deleted);
        long first = await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 1));
        long second = await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 1));
        await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 1));

        (int count, long? cutoff) = await repo.GetRegisteredMachineCutoffAsync(1, 2, CancellationToken.None);

        await Assert.That(count).IsEqualTo(3);
        await Assert.That(cutoff).IsEqualTo(second);
        await Assert.That(cutoff).IsNotEqualTo(first);
    }

    [Test]
    public async Task GetRegisteredMachineCutoff_MachinesVanishAfterTheCount_ReturnsNoCutoffRatherThanFailing()
    {
        // The count and the offset lookup are separate statements. If machines are removed between
        // them, fewer than limit remain and nothing is over the limit; that must read as "no cutoff",
        // not as a failure and not as a zero id (which would pause every machine).
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());
        for (int i = 0; i < 5; i++)
        {
            await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 1));
        }

        DeleteMachinesBeforeTheOffsetLookup interceptor = new();
        dbFactory.Context.AddInterceptor(interceptor);

        (int count, long? cutoff) = await repo.GetRegisteredMachineCutoffAsync(1, 3, CancellationToken.None);

        await Assert.That(interceptor.Deleted).IsTrue();
        await Assert.That(count).IsEqualTo(5);
        await Assert.That(cutoff).IsNull();
    }

    [Test]
    public async Task GetRegisteredMachineCutoff_NoMachines_ReturnsZeroAndNoCutoff()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        (int count, long? cutoff) = await repo.GetRegisteredMachineCutoffAsync(1, 3, CancellationToken.None);

        await Assert.That(count).IsEqualTo(0);
        await Assert.That(cutoff).IsNull();
    }
}

/// <summary>
/// Removes the tenant's machines at the moment the offset lookup is about to run, on the same
/// connection and after the count has already returned, so the race between the two statements is
/// reproduced deterministically instead of by timing.
/// </summary>
internal sealed class DeleteMachinesBeforeTheOffsetLookup : CommandInterceptor
{
    public bool Deleted { get; private set; }

    public override DbCommand CommandInitialized(CommandEventData eventData, DbCommand command)
    {
        if ((Deleted == false) && command.CommandText.Contains("OFFSET", StringComparison.Ordinal))
        {
            using DbCommand delete = command.Connection!.CreateCommand();
            delete.CommandText = "DELETE FROM Machines";
            delete.ExecuteNonQuery();
            Deleted = true;
        }

        return command;
    }
}
