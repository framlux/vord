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
/// Live tests for the backfill that gives already-provisioned tenants the stale-telemetry built-in
/// rule. Built-in rules are seeded at tenant creation and on entitled tier transitions and nothing
/// reconciles them on a schedule, so without this statement a tenant created before the rule existed
/// would never receive it — and this change moves a wedged-collector machine from Offline to
/// Warning, silently removing the alerting that ships today.
/// </summary>
/// <remarks>
/// The statement under test is database-wide rather than tenant-scoped — that is what a migration
/// is — so two tests replaying it at once would each insert the other's row and collide on the
/// partial unique index. The tests share one database, so they run one at a time.
/// </remarks>
[NotInParallel]
public sealed class TelemetryStaleBuiltInAlertRuleTests
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
    public async Task Backfill_GivesAnAlreadyProvisionedTenantTheRule()
    {
        // A migration runs once, and on a fresh database it runs before any tenant exists, so the
        // statement under test is replayed here against a seeded tenant. The text is the migration's
        // own constant, not a copy, so the shipped statement and the tested statement cannot drift.
        await using DatabaseContext db = CreateContext();
        int tenantId = await SeedTenantAsync(db);
        await InsertBuiltInRuleAsync(db, tenantId, AlertMetric.MachineOffline, isEnabled: true);

        await ReplayBackfillAsync();

        AlertRule? rule = await db.AlertRules.FirstOrDefaultAsync(r =>
            (r.TenantId == tenantId) && (r.Metric == AlertMetric.TelemetryStale) && (r.IsCustom == false));

        await Assert.That(rule).IsNotNull();
        await Assert.That(rule!.Name).IsEqualTo("Telemetry stopped");
        await Assert.That(rule.Operator).IsEqualTo(AlertOperator.EqualTo);
        await Assert.That(rule.Threshold).IsEqualTo(1m);
        await Assert.That(rule.DurationMinutes).IsEqualTo(10);
        await Assert.That(rule.Severity).IsEqualTo(AlertSeverity.Warning);
        await Assert.That(rule.NotifyEmail).IsTrue();
        await Assert.That(rule.NotifyWebhook).IsFalse();
        await Assert.That(rule.CreatedByUserId).IsEqualTo(SystemUserId);
        await Assert.That(rule.IsEnabled).IsTrue();
    }

    [Test]
    public async Task Backfill_MatchesTheTenantsExistingEntitlement()
    {
        // Built-in rules are seeded disabled for an unentitled tenant and turned on by the tier
        // transition. Backfilling enabled would page a Free tenant for a rule it never chose and
        // that nothing would ever turn off again.
        await using DatabaseContext db = CreateContext();
        int tenantId = await SeedTenantAsync(db);
        await InsertBuiltInRuleAsync(db, tenantId, AlertMetric.MachineOffline, isEnabled: false);

        await ReplayBackfillAsync();

        AlertRule? rule = await db.AlertRules.FirstOrDefaultAsync(r =>
            (r.TenantId == tenantId) && (r.Metric == AlertMetric.TelemetryStale) && (r.IsCustom == false));

        await Assert.That(rule).IsNotNull();
        await Assert.That(rule!.IsEnabled).IsFalse();
    }

    [Test]
    public async Task Backfill_IsIdempotent()
    {
        // The partial unique index refuses a second built-in rule per metric, so a replayed or
        // re-run statement must select nothing rather than throw.
        await using DatabaseContext db = CreateContext();
        int tenantId = await SeedTenantAsync(db);
        await InsertBuiltInRuleAsync(db, tenantId, AlertMetric.MachineOffline, isEnabled: true);

        await ReplayBackfillAsync();
        await ReplayBackfillAsync();

        int count = await db.AlertRules.CountAsync(r =>
            (r.TenantId == tenantId) && (r.Metric == AlertMetric.TelemetryStale) && (r.IsCustom == false));

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task Backfill_SkipsATenantThatWasNeverProvisioned()
    {
        // A tenant with no built-in rules at all is not a tenant that lost one — provisioning seeds
        // the whole set on its next entitled transition, and inventing a lone rule here would give
        // it a set no code path ever produced.
        await using DatabaseContext db = CreateContext();
        int tenantId = await SeedTenantAsync(db);

        await ReplayBackfillAsync();

        int count = await db.AlertRules.CountAsync(r => r.TenantId == tenantId);

        await Assert.That(count).IsEqualTo(0);
    }

    private static async Task ReplayBackfillAsync()
    {
        await using NpgsqlConnection connection = new(_migratedConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = new(AddTelemetryStaleBuiltInAlertRule.BackfillSql, connection);
        await command.ExecuteNonQueryAsync();
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
            Name = $"stale-{Guid.NewGuid():N}"[..24],
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = SystemUserId,
            IsActive = true,
            LogoUrl = "",
        };

        return await db.InsertWithInt32IdentityAsync(tenant);
    }

    private static async Task InsertBuiltInRuleAsync(
        DatabaseContext db,
        int tenantId,
        AlertMetric metric,
        bool isEnabled)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = $"{metric} rule",
            Description = null,
            Metric = metric,
            Operator = AlertOperator.EqualTo,
            Threshold = 1m,
            DurationMinutes = 10,
            Severity = AlertSeverity.Critical,
            IsEnabled = isEnabled,
            NotifyEmail = true,
            NotifyWebhook = false,
            IsCustom = false,
            CreatedByUserId = SystemUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await db.InsertAsync(rule);
    }
}
