// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Integration;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Integration.Repositories;

/// <summary>
/// Verifies the registration duplicate check against real Postgres, where the optional serial
/// number and asset tag arrive as null parameters. The SQLite-backed unit suite cannot prove that
/// the generated SQL compares a null parameter the same way on Postgres.
/// </summary>
public sealed class MachineDuplicateCheckLiveTests
{
    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _migratedConnectionString = _fixture.ConnectionString;
        await RunMigrationsAsync(_migratedConnectionString);
    }

    [After(Class)]
    public static async Task AfterClass() => await _fixture.DisposeAsync();

    [Test]
    public async Task DoesMachineExistAsync_NullSerialAndAssetTag_IgnoresFillerStoredByAnotherMachine()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);

        int tenantId = await SeedTenantAsync(db);
        await SeedMachineAsync(db, tenantId, "system serial number", "machine-id-0001", "Default string");

        bool exists = await repo.DoesMachineExistAsync(null, "machine-id-0002", null, tenantId, CancellationToken.None);

        await Assert.That(exists).IsFalse();
    }

    [Test]
    public async Task DoesMachineExistAsync_NullSerialAndAssetTag_StillMatchesBySystemId()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);

        int tenantId = await SeedTenantAsync(db);
        await SeedMachineAsync(db, tenantId, "system serial number", "machine-id-0001", "Default string");

        bool exists = await repo.DoesMachineExistAsync(null, "machine-id-0001", null, tenantId, CancellationToken.None);

        await Assert.That(exists).IsTrue();
    }

    [Test]
    public async Task DoesMachineExistAsync_RealSerialAndAssetTag_StillMatch()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);

        int tenantId = await SeedTenantAsync(db);
        await SeedMachineAsync(db, tenantId, "c02xl0gtjgh5", "machine-id-0001", "ASSET-00417");

        bool bySerial = await repo.DoesMachineExistAsync("c02xl0gtjgh5", "machine-id-0002", null, tenantId, CancellationToken.None);
        bool byAssetTag = await repo.DoesMachineExistAsync(null, "machine-id-0003", "ASSET-00417", tenantId, CancellationToken.None);

        await Assert.That(bySerial).IsTrue();
        await Assert.That(byAssetTag).IsTrue();
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

    private static async Task<int> SeedTenantAsync(DatabaseContext db)
    {
        return await db.InsertWithInt32IdentityAsync(new Tenant
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Live Test Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        });
    }

    private static async Task SeedMachineAsync(DatabaseContext db, int tenantId, string serialNumber, string systemId, string? assetTag)
    {
        await db.InsertWithInt64IdentityAsync(new Machine
        {
            TenantId = tenantId,
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = $"Test Machine {Guid.NewGuid():N}",
            SerialNumber = serialNumber,
            SystemId = systemId,
            AssetTagNumber = assetTag,
            MachineType = MachineTypes.Unknown,
            OperatingSystem = OperatingSystems.Unknown,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
        });
    }
}
