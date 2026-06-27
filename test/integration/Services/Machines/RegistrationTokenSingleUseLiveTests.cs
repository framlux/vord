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

namespace Framlux.FleetManagement.Test.Integration.Services.Machines;

/// <summary>
/// Live integration tests for single-use registration tokens against a real Postgres backend
/// (Testcontainers). The atomic consume is a conditional <c>UPDATE</c> executed inside the
/// Serializable registration transaction in
/// <see cref="DatabaseRepository.CreateMachineWithKeyAsync"/>. The conditional affected-rows
/// semantics — exactly one of two concurrent consumers can produce a non-zero row count — are the
/// entire reason these run against real Postgres rather than the in-memory SQLite unit suite. Each
/// test seeds a fresh tenant/token chain so the tests stay isolated on the shared container.
/// </summary>
public sealed class RegistrationTokenSingleUseLiveTests
{
    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    // A fixed reference instant so the ExpiresAt > now predicate is deterministic; no wall-clock.
    private static readonly DateTimeOffset ReferenceNow = new(2026, 06, 26, 12, 0, 0, TimeSpan.Zero);

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

    /// <summary>
    /// Seeds a fresh tenant and an available (non-consumed, non-revoked, future-dated) registration
    /// token, returning the tenant and token ids. The system user (Id 1) is seeded by the initial
    /// migration so CreatedByUserId resolves.
    /// </summary>
    private static async Task<(int TenantId, long TokenId)> SeedTenantAndTokenAsync(
        DatabaseContext db,
        bool revoked = false,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? consumedAt = null)
    {
        int tenantId = await db.InsertWithInt32IdentityAsync(new Tenant
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Live Token Tenant {Guid.NewGuid():N}",
            CreatedAt = ReferenceNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        });

        long tokenId = await db.InsertWithInt64IdentityAsync(new RegistrationToken
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Live Single-Use Token",
            CreatedByUserId = 1,
            CreatedAt = ReferenceNow,
            ExpiresAt = expiresAt ?? ReferenceNow.AddDays(7),
            IsRevoked = revoked,
            RevokedAt = revoked ? ReferenceNow : null,
            ConsumedAt = consumedAt,
        });

        return (tenantId, tokenId);
    }

    private static Machine BuildMachine(int tenantId, long tokenId)
    {
        return new Machine
        {
            ApiKeyHash = string.Empty,
            Name = "live-machine",
            SerialNumber = Guid.NewGuid().ToString("N"),
            SystemId = Guid.NewGuid().ToString("N"),
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = tokenId,
            RegisteredOn = ReferenceNow,
            IsDeleted = false,
            TenantId = tenantId,
        };
    }

    [Test]
    public async Task FirstRegistration_ConsumesToken_AndStampsConsumedByMachineId()
    {
        // Intent: the first registration against a fresh token must succeed and the token row must
        // then carry ConsumedAt and ConsumedByMachineId == the created machine's id.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);

        (int tenantId, long tokenId) = await SeedTenantAndTokenAsync(db);

        (Machine? created, string? apiKey) = await repo.CreateMachineWithKeyAsync(
            BuildMachine(tenantId, tokenId), tokenId, ReferenceNow, machineLimit: null, CancellationToken.None);

        await Assert.That(created).IsNotNull();
        await Assert.That(apiKey).IsNotNull();

        RegistrationToken token = await db.RegistrationTokens.FirstAsync(t => t.Id == tokenId);
        await Assert.That(token.ConsumedAt).IsEqualTo(ReferenceNow);
        await Assert.That(token.ConsumedByMachineId).IsEqualTo(created!.Id);
    }

    [Test]
    public async Task SecondRegistration_WithConsumedToken_IsRejected_AndCreatesNoMachine()
    {
        // Intent: once a token is consumed, a second registration against it must be rejected and
        // no second machine row may exist for that token.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);

        (int tenantId, long tokenId) = await SeedTenantAndTokenAsync(db);

        (Machine? first, string? _) = await repo.CreateMachineWithKeyAsync(
            BuildMachine(tenantId, tokenId), tokenId, ReferenceNow, machineLimit: null, CancellationToken.None);
        await Assert.That(first).IsNotNull();

        (Machine? second, string? secondKey) = await repo.CreateMachineWithKeyAsync(
            BuildMachine(tenantId, tokenId), tokenId, ReferenceNow, machineLimit: null, CancellationToken.None);

        await Assert.That(second).IsNull();
        await Assert.That(secondKey).IsNull();

        int machineCount = await db.Machines.CountAsync(m => m.RegistrationTokenId == tokenId);
        await Assert.That(machineCount).IsEqualTo(1);
    }

    [Test]
    public async Task ExpiredToken_IsRejected_AndCreatesNoMachine()
    {
        // Intent: a token whose ExpiresAt is in the past relative to the supplied instant must be
        // rejected by the conditional consume even though the pre-check is bypassed here.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);

        (int tenantId, long tokenId) = await SeedTenantAndTokenAsync(db, expiresAt: ReferenceNow.AddDays(-1));

        (Machine? created, string? apiKey) = await repo.CreateMachineWithKeyAsync(
            BuildMachine(tenantId, tokenId), tokenId, ReferenceNow, machineLimit: null, CancellationToken.None);

        await Assert.That(created).IsNull();
        await Assert.That(apiKey).IsNull();

        int machineCount = await db.Machines.CountAsync(m => m.RegistrationTokenId == tokenId);
        await Assert.That(machineCount).IsEqualTo(0);
    }

    [Test]
    public async Task RevokedToken_IsRejected_AndCreatesNoMachine()
    {
        // Intent: a revoked token must be rejected by the conditional consume.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);

        (int tenantId, long tokenId) = await SeedTenantAndTokenAsync(db, revoked: true);

        (Machine? created, string? apiKey) = await repo.CreateMachineWithKeyAsync(
            BuildMachine(tenantId, tokenId), tokenId, ReferenceNow, machineLimit: null, CancellationToken.None);

        await Assert.That(created).IsNull();
        await Assert.That(apiKey).IsNull();

        int machineCount = await db.Machines.CountAsync(m => m.RegistrationTokenId == tokenId);
        await Assert.That(machineCount).IsEqualTo(0);
    }

    [Test]
    public async Task DoubleConsume_AffectedRowsSemantics_OnlyOneSucceeds()
    {
        // Intent: exercise the conditional-update affected-rows contract that makes the consume
        // safe, driving the two consumes sequentially rather than truly concurrently. Against a
        // token already stamped ConsumedAt the consume must affect zero rows (loser); against a
        // fresh token it must affect exactly one (winner). This affected-rows guarantee is what
        // ensures two registrations against the same token can never both win.
        await using DatabaseContext db = CreateContext();

        (int _, long consumedTokenId) = await SeedTenantAndTokenAsync(db, consumedAt: ReferenceNow);
        (int _, long freshTokenId) = await SeedTenantAndTokenAsync(db);

        int loserRows = await db.RegistrationTokens
            .Where(t => (t.Id == consumedTokenId) &&
                        (t.ConsumedAt == null) &&
                        (t.IsRevoked == false) &&
                        (t.ExpiresAt > ReferenceNow))
            .Set(t => t.ConsumedAt, ReferenceNow)
            .Set(t => t.ConsumedByMachineId, 42L)
            .UpdateAsync();

        int winnerRows = await db.RegistrationTokens
            .Where(t => (t.Id == freshTokenId) &&
                        (t.ConsumedAt == null) &&
                        (t.IsRevoked == false) &&
                        (t.ExpiresAt > ReferenceNow))
            .Set(t => t.ConsumedAt, ReferenceNow)
            .Set(t => t.ConsumedByMachineId, 43L)
            .UpdateAsync();

        await Assert.That(loserRows).IsEqualTo(0);
        await Assert.That(winnerRows).IsEqualTo(1);
    }
}
