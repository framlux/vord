// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Framlux.FleetManagement.Test.Integration.Migrations;

/// <summary>
/// Live tests for the partial unique index that keeps a tenant to at most one built-in alert rule
/// per metric. Provisioning is idempotent in application code, but that check cannot be atomic
/// against a concurrently replayed billing webhook, so the database constraint is what makes a
/// double-seed impossible rather than merely unlikely. The predicate matters just as much in the
/// other direction: custom rules may freely share a metric, and several per tenant is the expected
/// shape of the Team tier.
/// </summary>
public sealed class AlertRulesBuiltInUniqueIndexTests
{
    // The system user seeded by the initial migration; AlertRules.CreatedByUserId is a real FK.
    private const int SystemUserId = 1;

    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    /// <summary>Starts the Postgres container once and runs migrations for all tests in this class.</summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _migratedConnectionString = _fixture.ConnectionString;
        await RunMigrationsAsync(_migratedConnectionString);
    }

    /// <summary>Stops the Postgres container after all tests in the class.</summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task BuiltInRules_CannotDuplicateMetricWithinTenant()
    {
        await using DatabaseContext db = CreateContext();
        int tenantId = await SeedTenantAsync(db);

        await InsertBuiltInRuleAsync(db, tenantId, AlertMetric.DiskUsage);

        PostgresException? exception = await Assert.ThrowsAsync<PostgresException>(
            async () => await InsertBuiltInRuleAsync(db, tenantId, AlertMetric.DiskUsage));

        await Assert.That(exception).IsNotNull();

        // Asserting on the SQL state rather than the exception type: an unqualified type assertion
        // goes green on a foreign-key violation and would mask a missing index entirely.
        await Assert.That(exception!.SqlState).IsEqualTo("23505");
    }

    [Test]
    public async Task BuiltInRules_MayShareAMetricAcrossTenants()
    {
        await using DatabaseContext db = CreateContext();
        int firstTenantId = await SeedTenantAsync(db);
        int secondTenantId = await SeedTenantAsync(db);

        await InsertBuiltInRuleAsync(db, firstTenantId, AlertMetric.MemoryUsage);
        await InsertBuiltInRuleAsync(db, secondTenantId, AlertMetric.MemoryUsage);

        int count = await db.AlertRules.CountAsync(r =>
            ((r.TenantId == firstTenantId) || (r.TenantId == secondTenantId)) && (r.IsCustom == false));

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task CustomRules_MayShareAMetricWithinTenant()
    {
        await using DatabaseContext db = CreateContext();
        int tenantId = await SeedTenantAsync(db);

        await InsertCustomRuleAsync(db, tenantId, AlertMetric.CpuUsage);
        await InsertCustomRuleAsync(db, tenantId, AlertMetric.CpuUsage);

        int count = await db.AlertRules.CountAsync(r => (r.TenantId == tenantId) && (r.IsCustom == true));

        await Assert.That(count).IsEqualTo(2);
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

    // AlertRules carries foreign keys to Tenants and Users and Postgres enforces both, so a tenant
    // row must exist before any rule. The system user seeded at Id 1 satisfies CreatedByUserId.
    private static async Task<int> SeedTenantAsync(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"idx-{Guid.NewGuid():N}"[..24],
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = SystemUserId,
            IsActive = true,
            LogoUrl = "",
        };

        return await db.InsertWithInt32IdentityAsync(tenant);
    }

    private static Task InsertBuiltInRuleAsync(DatabaseContext db, int tenantId, AlertMetric metric)
    {
        return InsertRuleAsync(db, tenantId, metric, isCustom: false);
    }

    private static Task InsertCustomRuleAsync(DatabaseContext db, int tenantId, AlertMetric metric)
    {
        return InsertRuleAsync(db, tenantId, metric, isCustom: true);
    }

    private static async Task InsertRuleAsync(DatabaseContext db, int tenantId, AlertMetric metric, bool isCustom)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = $"{metric} rule {Guid.NewGuid():N}",
            Description = null,
            Metric = metric,
            Operator = AlertOperator.GreaterThan,
            Threshold = 90m,
            DurationMinutes = 5,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            IsCustom = isCustom,
            CreatedByUserId = SystemUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await db.InsertAsync(rule);
    }
}
