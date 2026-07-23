// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Integration.Services.Telemetry;

/// <summary>
/// Live integration tests for <see cref="RetentionReclassifyJob"/> against a real Postgres backend
/// (Testcontainers). <c>MachineTelemetry</c> is partitioned LIST(RetentionClass) then
/// RANGE(ReceivedAt), so changing a row's class is a physical row movement between partition trees
/// that only Postgres performs — SQLite cannot prove it. These tests assert both the stored class and
/// the physical leaf partition (<c>tableoid</c>) before and after the job runs.
/// </summary>
public sealed class RetentionReclassifyLiveTests
{
    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    /// <summary>
    /// Starts the Postgres container once and runs migrations so the schema is ready for all tests.
    /// </summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _migratedConnectionString = _fixture.ConnectionString;
        await RunMigrationsAsync(_migratedConnectionString);
    }

    /// <summary>
    /// Stops the Postgres container after all tests in the class.
    /// </summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    private static async Task RunMigrationsAsync(string connectionString)
    {
        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Warning));

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    private static DatabaseContext CreateContext()
    {
        DataOptions<DatabaseContext> options = new(
            new DataOptions().UsePostgreSQL(_migratedConnectionString));

        return new DatabaseContext(options);
    }

    private static DatabaseRepository CreateRepo(DatabaseContext db)
    {
        return new DatabaseRepository(db, NullLogger<DatabaseRepository>.Instance);
    }

    private static RetentionReclassifyJob CreateJob(DatabaseRepository repo)
    {
        // The real repository serves both seams: it resolves the tenant's current effective retention
        // from the seeded subscription and tier rows, and performs the day-chunked move.
        return new RetentionReclassifyJob(
            repo, repo, TimeProvider.System, NullLogger<RetentionReclassifyJob>.Instance);
    }

    /// <summary>
    /// Inserts the Tenant -> RegistrationToken -> Machine chain plus a subscription at the given tier,
    /// and returns the ids. The system user (Id 1) is seeded by the initial migration.
    /// </summary>
    private static async Task<(long MachineId, int TenantId)> SeedTenantAsync(
        DatabaseContext db, SubscriptionTier tier)
    {
        int tenantId = await db.InsertWithInt32IdentityAsync(new Tenant
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Reclassify Test Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        });

        long registrationTokenId = await db.InsertWithInt64IdentityAsync(new RegistrationToken
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Reclassify Test Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            IsRevoked = false,
        });

        long machineId = await db.InsertWithInt64IdentityAsync(new Machine
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

        await db.InsertAsync(new TenantSubscription
        {
            TenantId = tenantId,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        return (machineId, tenantId);
    }

    private static async Task<long> SeedTelemetryAsync(
        DatabaseContext db, long machineId, int tenantId, DateTimeOffset receivedAt, RetentionClass retentionClass)
    {
        return await db.InsertWithInt64IdentityAsync(new MachineTelemetry
        {
            MachineId = machineId,
            TenantId = tenantId,
            RetentionClass = retentionClass,
            TelemetryType = 1,
            Payload = """{"cpu": 42}""",
            ReceivedAt = receivedAt,
            ServerReceivedAt = receivedAt,
            SourceEventId = Guid.NewGuid().ToString("N"),
        });
    }

    /// <summary>
    /// Reads back the stored retention class and the name of the physical leaf partition the row lives
    /// in. Reading <c>tableoid</c> is what proves the UPDATE actually relocated the row rather than
    /// only rewriting a column.
    /// </summary>
    private static async Task<(RetentionClass Class, string Partition)> ReadRowAsync(DatabaseContext db, long id)
    {
        MachineTelemetry row = await db.MachineTelemetry.FirstAsync(t => t.Id == id);
        List<string> partitions = await db.QueryToListAsync<string>(
            @"SELECT tableoid::regclass::text FROM ""MachineTelemetry"" WHERE ""Id"" = @id",
            new DataParameter("id", id));

        return (row.RetentionClass, partitions[0]);
    }

    [Test]
    public async Task Upgrade_FreeToPro_MovesSurvivingRowsIntoTheMediumClassPartition()
    {
        // Intent: a Free tenant's Short-class telemetry physically relocates into the Medium class's
        // daily leaf when the tenant upgrades to Pro, and remains queryable afterwards.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        (long machineId, int tenantId) = await SeedTenantAsync(db, SubscriptionTier.Free);

        DateTimeOffset recent = DateTimeOffset.UtcNow.AddHours(-2);
        long rowId = await SeedTelemetryAsync(db, machineId, tenantId, recent, RetentionClass.Short);

        (RetentionClass beforeClass, string beforePartition) = await ReadRowAsync(db, rowId);
        await Assert.That(beforeClass).IsEqualTo(RetentionClass.Short);
        await Assert.That(beforePartition).Contains("MachineTelemetry_Short");

        // The plan change: the tenant is now Pro (60-day retention).
        await repo.UpdateSubscriptionStateAsync(
            tenantId, SubscriptionTier.Pro, SubscriptionStatus.Active, cancellationToken: CancellationToken.None);

        await CreateJob(repo).RunAsync(tenantId, CancellationToken.None);

        (RetentionClass afterClass, string afterPartition) = await ReadRowAsync(db, rowId);
        await Assert.That(afterClass).IsEqualTo(RetentionClass.Medium);
        await Assert.That(afterPartition).Contains("MachineTelemetry_Medium");
    }

    [Test]
    public async Task Downgrade_TeamToPro_MovesInWindowRowsAndLeavesOlderRowsInTheOldClass()
    {
        // Intent: the owner-approved downgrade exception. Rows inside the new 60-day Pro window move to
        // Medium; a 70-day-old row is outside it and keeps its Long class, so it expires on the old
        // schedule and a re-upgrade within the Team window brings the history back.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        (long machineId, int tenantId) = await SeedTenantAsync(db, SubscriptionTier.Team);

        long recentRowId = await SeedTelemetryAsync(
            db, machineId, tenantId, DateTimeOffset.UtcNow.AddHours(-2), RetentionClass.Long);
        long oldRowId = await SeedTelemetryAsync(
            db, machineId, tenantId, DateTimeOffset.UtcNow.AddDays(-70), RetentionClass.Long);

        await repo.UpdateSubscriptionStateAsync(
            tenantId, SubscriptionTier.Pro, SubscriptionStatus.Active, cancellationToken: CancellationToken.None);

        await CreateJob(repo).RunAsync(tenantId, CancellationToken.None);

        (RetentionClass recentClass, string recentPartition) = await ReadRowAsync(db, recentRowId);
        await Assert.That(recentClass).IsEqualTo(RetentionClass.Medium);
        await Assert.That(recentPartition).Contains("MachineTelemetry_Medium");

        (RetentionClass oldClass, string oldPartition) = await ReadRowAsync(db, oldRowId);
        await Assert.That(oldClass).IsEqualTo(RetentionClass.Long);
        await Assert.That(oldPartition).Contains("MachineTelemetry_Long");
    }

    [Test]
    public async Task Rerun_AfterConvergence_MovesNothing()
    {
        // Intent: the job is idempotent — a Hangfire retry or an overlapping run after the tenant has
        // already converged touches no rows.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        (long machineId, int tenantId) = await SeedTenantAsync(db, SubscriptionTier.Free);

        long rowId = await SeedTelemetryAsync(
            db, machineId, tenantId, DateTimeOffset.UtcNow.AddHours(-2), RetentionClass.Short);

        await repo.UpdateSubscriptionStateAsync(
            tenantId, SubscriptionTier.Pro, SubscriptionStatus.Active, cancellationToken: CancellationToken.None);

        RetentionReclassifyJob job = CreateJob(repo);
        await job.RunAsync(tenantId, CancellationToken.None);
        await job.RunAsync(tenantId, CancellationToken.None);

        (RetentionClass afterClass, string afterPartition) = await ReadRowAsync(db, rowId);
        await Assert.That(afterClass).IsEqualTo(RetentionClass.Medium);
        await Assert.That(afterPartition).Contains("MachineTelemetry_Medium");

        // A third pass reports zero moved rows for the day the row lives in.
        DateTimeOffset dayStart = new(
            DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        int moved = await repo.ReclassifyTelemetryForTenantAsync(
            tenantId, RetentionClass.Medium, dayStart.AddDays(-1), dayStart.AddDays(1), CancellationToken.None);
        await Assert.That(moved).IsEqualTo(0);
    }

    [Test]
    public async Task Reclassify_LeavesOtherTenantsUntouched()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        (long machineId, int tenantId) = await SeedTenantAsync(db, SubscriptionTier.Free);
        (long otherMachineId, int otherTenantId) = await SeedTenantAsync(db, SubscriptionTier.Free);

        long otherRowId = await SeedTelemetryAsync(
            db, otherMachineId, otherTenantId, DateTimeOffset.UtcNow.AddHours(-2), RetentionClass.Short);
        await SeedTelemetryAsync(db, machineId, tenantId, DateTimeOffset.UtcNow.AddHours(-2), RetentionClass.Short);

        await repo.UpdateSubscriptionStateAsync(
            tenantId, SubscriptionTier.Team, SubscriptionStatus.Active, cancellationToken: CancellationToken.None);

        await CreateJob(repo).RunAsync(tenantId, CancellationToken.None);

        (RetentionClass otherClass, string otherPartition) = await ReadRowAsync(db, otherRowId);
        await Assert.That(otherClass).IsEqualTo(RetentionClass.Short);
        await Assert.That(otherPartition).Contains("MachineTelemetry_Short");
    }
}
