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
using LinqToDB.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Integration.Repositories;

/// <summary>
/// Verifies the ON CONFLICT upsert against real Postgres: the first observation inserts and returns
/// its timestamp; a later observation for the same (rule, machine) keeps the original
/// FirstTriggeredAt while advancing LastObservedAt. ON CONFLICT semantics differ on SQLite, so the
/// single-statement Postgres path is exercised here.
/// </summary>
public sealed class AlertConditionStateUpsertLiveTests
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
    public async Task Upsert_SecondObservation_PreservesFirstTriggeredAt()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);

        Tenant tenant = await SeedTenantAsync(db);
        AlertRule rule = await SeedAlertRuleAsync(db, tenant.Id);
        Machine machine = await SeedMachineAsync(db, tenant.Id);

        DateTimeOffset first = DateTimeOffset.UnixEpoch.AddHours(1);
        DateTimeOffset later = DateTimeOffset.UnixEpoch.AddHours(2);

        DateTimeOffset r1 = await repo.UpsertObservationAsync(rule.Id, machine.Id, first, CancellationToken.None);
        DateTimeOffset r2 = await repo.UpsertObservationAsync(rule.Id, machine.Id, later, CancellationToken.None);

        // FirstTriggeredAt is stable across observations; the upsert anchors duration windows to it.
        await Assert.That(r1).IsEqualTo(first);
        await Assert.That(r2).IsEqualTo(first);

        // The surviving row advanced its LastObservedAt to the later observation.
        AlertConditionState? stored = await repo.GetAsync(rule.Id, machine.Id, CancellationToken.None);
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.FirstTriggeredAt).IsEqualTo(first);
        await Assert.That(stored.LastObservedAt).IsEqualTo(later);
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

    private static async Task<Tenant> SeedTenantAsync(DatabaseContext db)
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
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        return tenant;
    }

    private static async Task<AlertRule> SeedAlertRuleAsync(DatabaseContext db, int tenantId)
    {
        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = $"Test Rule {Guid.NewGuid():N}",
            Description = "Integration test rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80m,
            DurationMinutes = 0,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            NotifyEmail = false,
            NotifyWebhook = true,
            IsCustom = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        return rule;
    }

    private static async Task<Machine> SeedMachineAsync(DatabaseContext db, int tenantId)
    {
        Machine machine = new()
        {
            TenantId = tenantId,
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = $"Test Machine {Guid.NewGuid():N}",
            SerialNumber = Guid.NewGuid().ToString("N"),
            SystemId = Guid.NewGuid().ToString("N"),
            MachineType = MachineTypes.Unknown,
            OperatingSystem = OperatingSystems.Unknown,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        return machine;
    }
}
