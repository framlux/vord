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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Integration.Repositories;

/// <summary>
/// Verifies the Enterprise guarantees against real Postgres: no Stripe-shaped write changes an
/// Enterprise row, and concurrent agreement applies converge on the newest revision. The tier filter
/// and the revision guard are both evaluated by the database, so only a real server proves that a
/// racing pass cannot get around them.
/// </summary>
public sealed class EnterpriseSubscriptionWriteLiveTests
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
    public async Task StripeWrites_NeverChangeAnEnterpriseRow()
    {
        using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);
        int tenantId = await SeedTenantAsync(db);
        await repo.ApplyEnterpriseSubscriptionAsync(tenantId, 1, TermEnd);

        int a = await repo.UpdateSubscriptionStateAsync(tenantId, SubscriptionTier.Free, SubscriptionStatus.Active, clearCurrentPeriodEnd: true);
        int b = await repo.UpdateSubscriptionStateAsync(tenantId, tier: null, SubscriptionStatus.Canceled);
        int c = await repo.UpdateSubscriptionPeriodEndAsync(tenantId, TermEnd.AddDays(-300));
        int d = await repo.SetCancelAtPeriodEndAsync(tenantId, true);

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(a + b + c + d).IsEqualTo(0);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await Assert.That(row.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(row.CurrentPeriodEnd).IsEqualTo(TermEnd);
        await Assert.That(row.CancelAtPeriodEnd).IsFalse();
    }

    [Test]
    public async Task ConcurrentApplies_TheNewerRevisionAlwaysWins()
    {
        int tenantId;
        using (DatabaseContext seedDb = CreateContext())
        {
            tenantId = await SeedTenantAsync(seedDb);
            await new DatabaseRepository(seedDb, NullLogger<DatabaseRepository>.Instance)
                .ApplyEnterpriseSubscriptionAsync(tenantId, 1, TermEnd);
        }

        Task Apply(int revision) => Task.Run(async () =>
        {
            using DatabaseContext db = CreateContext();
            await new DatabaseRepository(db, NullLogger<DatabaseRepository>.Instance)
                .ApplyEnterpriseSubscriptionAsync(tenantId, revision, TermEnd.AddDays(revision));
        });

        await Task.WhenAll(Apply(3), Apply(2), Apply(3), Apply(2));

        using DatabaseContext readDb = CreateContext();
        TenantSubscription? row = await new DatabaseRepository(readDb, NullLogger<DatabaseRepository>.Instance)
            .GetSubscriptionForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(row!.AppliedAgreementRevision).IsEqualTo(3);
        await Assert.That(row.CurrentPeriodEnd).IsEqualTo(TermEnd.AddDays(3));
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
}
