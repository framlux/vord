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
/// Verifies against real Postgres that the conditional override writes refuse an Enterprise tenant
/// inside the statement itself, including the INSERT-from-SELECT path SQLite shares only in spirit,
/// and that racing first writes for a new tenant both succeed through the unique-constraint retry.
/// </summary>
public sealed class TenantSubscriptionOverrideEnterpriseGuardLiveTests
{
    private static readonly DateTimeOffset TermEnd = new(2027, 10, 4, 23, 59, 59, TimeSpan.Zero);

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
    public async Task Upsert_TeamTenant_InsertsThenUpdates()
    {
        using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);
        int tenantId = await SeedTenantAsync(db);
        await SeedSubscriptionAsync(db, tenantId, SubscriptionTier.Team);

        bool inserted = await repo.UpsertOverrideUnlessEnterpriseAsync(tenantId, 10, 20, 30, 40, 50, CancellationToken.None);
        bool updated = await repo.UpsertOverrideUnlessEnterpriseAsync(tenantId, 11, null, 31, null, null, CancellationToken.None);

        TenantSubscriptionOverride? row = await repo.GetOverrideForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(inserted).IsTrue();
        await Assert.That(updated).IsTrue();
        await Assert.That(row!.MachineLimit).IsEqualTo(11);
        await Assert.That(row.RetentionDays).IsNull();
        await Assert.That(row.AlertRuleLimit).IsEqualTo(31);
        await Assert.That(row.MemberLimit).IsNull();
    }

    [Test]
    public async Task Upsert_EnterpriseTenant_RefusesBothTheInsertAndTheUpdate()
    {
        using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);
        int withoutOverride = await SeedTenantAsync(db);
        int withOverride = await SeedTenantAsync(db);
        await repo.ApplyEnterpriseSubscriptionAsync(withoutOverride, 1, TermEnd);
        await repo.ApplyEnterpriseSubscriptionAsync(withOverride, 1, TermEnd);
        await repo.UpsertOverrideAsync(withOverride, 500, 180, 40, 20, int.MaxValue, CancellationToken.None);

        bool insertRefused = await repo.UpsertOverrideUnlessEnterpriseAsync(withoutOverride, 1, 1, 1, 1, null, CancellationToken.None);
        bool updateRefused = await repo.UpsertOverrideUnlessEnterpriseAsync(withOverride, 1, 1, 1, 1, null, CancellationToken.None);

        TenantSubscriptionOverride? kept = await repo.GetOverrideForTenantAsync(withOverride, CancellationToken.None);
        await Assert.That(insertRefused).IsFalse();
        await Assert.That(updateRefused).IsFalse();
        await Assert.That(await repo.GetOverrideForTenantAsync(withoutOverride, CancellationToken.None)).IsNull();
        await Assert.That(kept!.MachineLimit).IsEqualTo(500);
        await Assert.That(kept.MemberLimit).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task Remove_EnterpriseTenant_RefusesAndTeamTenantDeletes()
    {
        using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);
        int enterpriseTenant = await SeedTenantAsync(db);
        int teamTenant = await SeedTenantAsync(db);
        await repo.ApplyEnterpriseSubscriptionAsync(enterpriseTenant, 1, TermEnd);
        await SeedSubscriptionAsync(db, teamTenant, SubscriptionTier.Team);
        await repo.UpsertOverrideAsync(enterpriseTenant, 500, 180, 40, 20, int.MaxValue, CancellationToken.None);
        await repo.UpsertOverrideAsync(teamTenant, 50, null, null, null, null, CancellationToken.None);

        bool enterpriseRemoved = await repo.RemoveOverrideUnlessEnterpriseAsync(enterpriseTenant, CancellationToken.None);
        bool teamRemoved = await repo.RemoveOverrideUnlessEnterpriseAsync(teamTenant, CancellationToken.None);

        await Assert.That(enterpriseRemoved).IsFalse();
        await Assert.That(teamRemoved).IsTrue();
        await Assert.That(await repo.GetOverrideForTenantAsync(enterpriseTenant, CancellationToken.None)).IsNotNull();
        await Assert.That(await repo.GetOverrideForTenantAsync(teamTenant, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task Upsert_RacingFirstWritesForANewTenant_AllSucceedAndLeaveOneRow()
    {
        int tenantId;
        using (DatabaseContext seedDb = CreateContext())
        {
            tenantId = await SeedTenantAsync(seedDb);
            await SeedSubscriptionAsync(seedDb, tenantId, SubscriptionTier.Team);
        }

        Task<bool> Write(int machineLimit) => Task.Run(async () =>
        {
            using DatabaseContext db = CreateContext();
            bool written = await new DatabaseRepository(db, NullLogger<DatabaseRepository>.Instance)
                .UpsertOverrideUnlessEnterpriseAsync(tenantId, machineLimit, null, null, null, null, CancellationToken.None);

            return written;
        });

        bool[] results = await Task.WhenAll(Write(1), Write(2), Write(3), Write(4), Write(5), Write(6));

        using DatabaseContext readDb = CreateContext();
        int rows = await readDb.TenantSubscriptionOverrides.CountAsync(o => o.TenantId == tenantId);
        await Assert.That(results.All(r => r)).IsTrue();
        await Assert.That(rows).IsEqualTo(1);
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
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Live Test Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        };
        int tenantId = await db.InsertWithInt32IdentityAsync(tenant);

        return tenantId;
    }

    private static async Task SeedSubscriptionAsync(DatabaseContext db, int tenantId, SubscriptionTier tier)
    {
        await db.InsertAsync(new TenantSubscription
        {
            TenantId = tenantId,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }
}
