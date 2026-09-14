// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Integration.Services.Machines;

/// <summary>
/// Live test for the projection-lag query, against real Postgres.
/// </summary>
/// <remarks>
/// The unit tests substitute this query away entirely, so this is the only place a mistake in the
/// copied predicate chain would show up. Each predicate is exercised on its own: a row the
/// projection loop would never read is not lag, and reporting it would produce permanent,
/// unremediable lag on a perfectly healthy projection.
/// </remarks>
[NotInParallel]
public sealed class ProjectionLagLiveTests
{
    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Starts Postgres and runs migrations once for the class.</summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _migratedConnectionString = _fixture.ConnectionString;

        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(_migratedConnectionString)
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Warning));

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    /// <summary>Stops the container after the class.</summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    private static DatabaseContext CreateContext()
    {
        DataOptions<DatabaseContext> options = new(new DataOptions().UsePostgreSQL(_migratedConnectionString));

        return new DatabaseContext(options);
    }

    private static DatabaseRepository CreateRepo(DatabaseContext db) => new(db, NullLogger<DatabaseRepository>.Instance);

    /// <summary>
    /// Clears telemetry before each test. The Postgres fixture is shared for the class, and these
    /// assertions are about a query returning nothing, so a row left behind by a neighbouring test
    /// would satisfy the predicates and fail the test for a reason unrelated to the query.
    /// </summary>
    private static async Task ClearTelemetryAsync(DatabaseContext db)
    {
        await db.MachineTelemetry.DeleteAsync();
    }

    private static async Task<int> SeedTenantAsync(DatabaseContext db)
    {
        return await db.InsertWithInt32IdentityAsync(new Tenant
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Projection Lag Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        });
    }

    /// <summary>
    /// Seeds a machine whose id has the requested remainder under two-shard modulo partitioning, so
    /// a test can place telemetry in a chosen shard rather than hoping the identity lands there.
    /// </summary>
    private static async Task<long> SeedMachineInShardAsync(DatabaseContext db, int tenantId, int shardIndex, int shardCount)
    {
        long machineId;

        do
        {
            long registrationTokenId = await db.InsertWithInt64IdentityAsync(new RegistrationToken
            {
                TenantId = tenantId,
                TokenHash = Guid.NewGuid().ToString("N"),
                Name = "Projection Lag Token",
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                IsRevoked = false,
            });

            machineId = await db.InsertWithInt64IdentityAsync(new Machine
            {
                ApiKeyHash = Guid.NewGuid().ToString("N"),
                Name = "m",
                SerialNumber = Guid.NewGuid().ToString("N"),
                SystemId = Guid.NewGuid().ToString("N"),
                MachineType = MachineTypes.BareMetalServer,
                OperatingSystem = OperatingSystems.Ubuntu,
                RegistrationTokenId = registrationTokenId,
                RegisteredOn = DateTimeOffset.UtcNow,
                IsDeleted = false,
                TenantId = tenantId,
            });
        }
        while ((machineId % shardCount) != shardIndex);

        return machineId;
    }

    private static async Task<long> InsertTelemetryAsync(
        DatabaseContext db, int tenantId, long machineId, DateTimeOffset receivedAt, DateTimeOffset serverReceivedAt)
    {
        MachineTelemetry row = new()
        {
            MachineId = machineId,
            TenantId = tenantId,
            TelemetryType = 1,
            Payload = "{}",
            ReceivedAt = receivedAt,
            ServerReceivedAt = serverReceivedAt,
        };

        return await db.InsertWithInt64IdentityAsync(row);
    }

    [Test]
    public async Task OldestUnprojectedReceipt_ReturnsTheOldestRowAboveTheCursorInThatShard()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        await ClearTelemetryAsync(db);

        int tenantId = await SeedTenantAsync(db);
        long shardZeroMachine = await SeedMachineInShardAsync(db, tenantId, shardIndex: 0, shardCount: 2);
        long shardOneMachine = await SeedMachineInShardAsync(db, tenantId, shardIndex: 1, shardCount: 2);

        // The shard-1 row is older, so returning it would prove the shard predicate was dropped.
        long inShard = await InsertTelemetryAsync(db, tenantId, shardZeroMachine, Now.AddMinutes(-10), Now.AddMinutes(-10));
        await InsertTelemetryAsync(db, tenantId, shardOneMachine, Now.AddMinutes(-20), Now.AddMinutes(-20));

        DateTimeOffset? oldest = await repo.GetOldestUnprojectedReceiptAsync(
            cursor: inShard - 1,
            streamingWindow: Now.AddDays(-2),
            visibilityCutoff: Now.AddSeconds(-5),
            shardIndex: 0,
            shardCount: 2,
            CancellationToken.None);

        await Assert.That(oldest).IsNotNull();
        await Assert.That(oldest!.Value).IsEqualTo(Now.AddMinutes(-10));
    }

    [Test]
    public async Task OldestUnprojectedReceipt_IgnoresRowsOutsideTheStreamingWindow()
    {
        // A row above the cursor but older than the window is outside the window by design and is
        // never projected. Counting it would report lag nobody can ever clear.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        await ClearTelemetryAsync(db);

        int tenantId = await SeedTenantAsync(db);
        long machineId = await SeedMachineInShardAsync(db, tenantId, shardIndex: 0, shardCount: 1);
        long ancient = await InsertTelemetryAsync(db, tenantId, machineId, Now.AddDays(-5), Now.AddDays(-5));

        DateTimeOffset? oldest = await repo.GetOldestUnprojectedReceiptAsync(
            cursor: ancient - 1,
            streamingWindow: Now.AddDays(-2),
            visibilityCutoff: Now.AddSeconds(-5),
            shardIndex: 0,
            shardCount: 1,
            CancellationToken.None);

        await Assert.That(oldest).IsNull();
    }

    [Test]
    public async Task OldestUnprojectedReceipt_IgnoresRowsNewerThanTheVisibilityCutoff()
    {
        // A row inside the safety lag has not become visible to the loop yet. Counting it would
        // show a constant lag floor equal to the safety lag on perfectly healthy traffic.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        await ClearTelemetryAsync(db);

        int tenantId = await SeedTenantAsync(db);
        long machineId = await SeedMachineInShardAsync(db, tenantId, shardIndex: 0, shardCount: 1);
        long fresh = await InsertTelemetryAsync(db, tenantId, machineId, Now, Now);

        DateTimeOffset? oldest = await repo.GetOldestUnprojectedReceiptAsync(
            cursor: fresh - 1,
            streamingWindow: Now.AddDays(-2),
            visibilityCutoff: Now.AddMinutes(-1),
            shardIndex: 0,
            shardCount: 1,
            CancellationToken.None);

        await Assert.That(oldest).IsNull();
    }

    [Test]
    public async Task OldestUnprojectedReceipt_ReturnsNullOnceTheCursorHasPassedEverything()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        await ClearTelemetryAsync(db);

        int tenantId = await SeedTenantAsync(db);
        long machineId = await SeedMachineInShardAsync(db, tenantId, shardIndex: 0, shardCount: 1);
        long last = await InsertTelemetryAsync(db, tenantId, machineId, Now.AddMinutes(-30), Now.AddMinutes(-30));

        DateTimeOffset? oldest = await repo.GetOldestUnprojectedReceiptAsync(
            cursor: last,
            streamingWindow: Now.AddDays(-2),
            visibilityCutoff: Now.AddSeconds(-5),
            shardIndex: 0,
            shardCount: 1,
            CancellationToken.None);

        await Assert.That(oldest).IsNull();
    }
}
