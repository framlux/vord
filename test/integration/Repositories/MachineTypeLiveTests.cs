// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Integration.Repositories;

/// <summary>
/// Live test, against real Postgres, that the single-board computer type round-trips through the
/// real <c>smallint</c> columns on both the machine and its summary, and is not refused by any
/// constraint. SQLite is untyped enough to accept almost anything, so only the real schema shows it.
/// </summary>
public sealed class MachineTypeLiveTests
{
    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

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

    private static async Task<long> SeedMachineAsync(DatabaseContext db, MachineTypes machineType)
    {
        int tenantId = await db.InsertWithInt32IdentityAsync(new Tenant
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Machine Type Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        });

        long registrationTokenId = await db.InsertWithInt64IdentityAsync(new RegistrationToken
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Machine Type Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            IsRevoked = false,
        });

        long machineId = await db.InsertWithInt64IdentityAsync(new Machine
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = "raspberrypi",
            SerialNumber = Guid.NewGuid().ToString("N"),
            SystemId = Guid.NewGuid().ToString("N"),
            MachineType = machineType,
            OperatingSystem = OperatingSystems.Debian,
            RegistrationTokenId = registrationTokenId,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId,
        });

        await db.InsertAsync(new MachineStateSummary
        {
            MachineId = machineId,
            TenantId = tenantId,
            Name = "raspberrypi",
            MachineType = (byte)machineType,
            HealthStatus = 3,
        });

        return machineId;
    }

    [Test]
    public async Task UpdateMachineType_UnknownToSingleBoardComputer_MovesBothRows()
    {
        await using DatabaseContext db = CreateContext();
        IMachineRepository repository = new DatabaseRepository(db, NullLogger<DatabaseRepository>.Instance);
        long machineId = await SeedMachineAsync(db, MachineTypes.Unknown);

        bool changed = await repository.UpdateMachineTypeAsync(machineId, MachineTypes.SingleBoardComputer, CancellationToken.None);

        Machine machine = await db.GetTable<Machine>().FirstAsync(m => m.Id == machineId);
        MachineStateSummary summary = await db.GetTable<MachineStateSummary>().FirstAsync(s => s.MachineId == machineId);

        await Assert.That(changed).IsTrue();
        await Assert.That(machine.MachineType).IsEqualTo(MachineTypes.SingleBoardComputer);
        await Assert.That((int)machine.MachineType).IsEqualTo(5);
        await Assert.That(summary.MachineType).IsEqualTo((byte)5);
    }

    [Test]
    public async Task UpdateMachineType_AlreadySingleBoardComputer_ChangesNothing()
    {
        // Every agent reports its type on every configuration fetch, so the common case is a write
        // that matches no row and must say so.
        await using DatabaseContext db = CreateContext();
        IMachineRepository repository = new DatabaseRepository(db, NullLogger<DatabaseRepository>.Instance);
        long machineId = await SeedMachineAsync(db, MachineTypes.SingleBoardComputer);

        bool changed = await repository.UpdateMachineTypeAsync(machineId, MachineTypes.SingleBoardComputer, CancellationToken.None);

        await Assert.That(changed).IsFalse();
    }
}
