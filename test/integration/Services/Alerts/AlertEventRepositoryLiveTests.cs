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

namespace Framlux.FleetManagement.Test.Integration.Services.Alerts;

/// <summary>
/// Live integration tests for <see cref="IAlertEventRepository.GetTriggeredEventsWithoutDeliveryAttemptsAsync"/>
/// against a real Postgres backend (Testcontainers). This method uses an anti-join that is
/// semantically different on Postgres vs. SQLite, so a live test is necessary to verify the
/// query behaviour that the re-drive reconciliation pass in <c>AlertEvaluationJob</c> depends on.
/// </summary>
public sealed class AlertEventRepositoryLiveTests
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

        // Run migrations once against the shared container so all tests share the schema.
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

    private static IAlertEventRepository CreateRepo(DatabaseContext db)
    {
        return new DatabaseRepository(db, NullLogger<DatabaseRepository>.Instance);
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

    private static async Task<AlertEvent> SeedAlertEventAsync(
        DatabaseContext db,
        int alertRuleId,
        int tenantId,
        DateTimeOffset triggeredAt,
        AlertEventStatus status = AlertEventStatus.Triggered)
    {
        AlertEvent alertEvent = new()
        {
            AlertRuleId = alertRuleId,
            TenantId = tenantId,
            MachineId = 1,
            Severity = AlertSeverity.Warning,
            Message = "Test alert event",
            Details = null,
            Status = status,
            TriggeredAt = triggeredAt,
        };
        alertEvent.Id = await db.InsertWithInt64IdentityAsync(alertEvent);

        return alertEvent;
    }

    private static async Task<IntegrationEndpoint> SeedIntegrationAsync(DatabaseContext db, int tenantId)
    {
        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = $"Test Integration {Guid.NewGuid():N}",
            Configuration = """{"url":"https://hooks.example.com/test","secret":"test"}""",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        integration.Id = await db.InsertWithInt32IdentityAsync(integration);

        return integration;
    }

    private static async Task SeedDeliveryAttemptAsync(
        DatabaseContext db,
        long alertEventId,
        int integrationEndpointId,
        IntegrationDeliveryAttemptStatus status = IntegrationDeliveryAttemptStatus.Succeeded)
    {
        await db.InsertAsync(new IntegrationDeliveryAttempt
        {
            AlertEventId = alertEventId,
            IntegrationEndpointId = integrationEndpointId,
            Status = status,
            AttemptedAt = DateTimeOffset.UtcNow,
            SucceededAt = status == IntegrationDeliveryAttemptStatus.Succeeded ? DateTimeOffset.UtcNow : null,
        });
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_OrphanInWindow_ReturnsIt()
    {
        // Intent: the primary re-drive scenario — a Triggered event committed to the DB but whose
        // Hangfire enqueue was lost before any delivery attempt. The anti-join must surface it.
        await using DatabaseContext db = CreateContext();
        IAlertEventRepository repo = CreateRepo(db);

        Tenant tenant = await SeedTenantAsync(db);
        AlertRule rule = await SeedAlertRuleAsync(db, tenant.Id);

        DateTimeOffset triggeredAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        AlertEvent orphan = await SeedAlertEventAsync(db, rule.Id, tenant.Id, triggeredAt);

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-15);
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.AddMinutes(-2);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == orphan.Id)).IsTrue();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_EventTooRecent_ExcludesIt()
    {
        // Intent: an event created after the notAfter upper bound (less than RedriveMinAgeMinutes
        // old) must be excluded so re-drive does not race with the live Hangfire enqueue path.
        await using DatabaseContext db = CreateContext();
        IAlertEventRepository repo = CreateRepo(db);

        Tenant tenant = await SeedTenantAsync(db);
        AlertRule rule = await SeedAlertRuleAsync(db, tenant.Id);

        // Triggered 30 seconds ago — within the "still in flight" zone.
        DateTimeOffset triggeredAt = DateTimeOffset.UtcNow.AddSeconds(-30);
        AlertEvent recent = await SeedAlertEventAsync(db, rule.Id, tenant.Id, triggeredAt);

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-15);
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.AddMinutes(-2);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == recent.Id)).IsFalse();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_EventTooOld_ExcludesIt()
    {
        // Intent: an event older than the notBefore lower bound (more than RedriveMaxAgeMinutes
        // old) must be excluded so perpetual re-driving of tenants with no integrations is bounded.
        await using DatabaseContext db = CreateContext();
        IAlertEventRepository repo = CreateRepo(db);

        Tenant tenant = await SeedTenantAsync(db);
        AlertRule rule = await SeedAlertRuleAsync(db, tenant.Id);

        // Triggered 30 minutes ago — beyond the 15-minute history window.
        DateTimeOffset triggeredAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        AlertEvent ancient = await SeedAlertEventAsync(db, rule.Id, tenant.Id, triggeredAt);

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-15);
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.AddMinutes(-2);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == ancient.Id)).IsFalse();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_EventWithAttempt_ExcludesIt()
    {
        // Intent: a Triggered event with an existing IntegrationDeliveryAttempt row (regardless
        // of status) must be excluded — it has already been claimed by the delivery job and
        // re-enqueueing could cause a duplicate notification on a Pending (in-flight) attempt.
        await using DatabaseContext db = CreateContext();
        IAlertEventRepository repo = CreateRepo(db);

        Tenant tenant = await SeedTenantAsync(db);
        AlertRule rule = await SeedAlertRuleAsync(db, tenant.Id);
        IntegrationEndpoint integration = await SeedIntegrationAsync(db, tenant.Id);

        DateTimeOffset triggeredAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        AlertEvent delivered = await SeedAlertEventAsync(db, rule.Id, tenant.Id, triggeredAt);
        await SeedDeliveryAttemptAsync(db, delivered.Id, integration.Id, IntegrationDeliveryAttemptStatus.Succeeded);

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-15);
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.AddMinutes(-2);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == delivered.Id)).IsFalse();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_PendingAttempt_AlsoExcludesEvent()
    {
        // Intent: a Pending delivery attempt (claim written but HTTP response not yet received)
        // must also block re-drive. The claim is the mutex; re-driving while Pending would cause
        // two workers to fight over the same (event, integration) pair.
        await using DatabaseContext db = CreateContext();
        IAlertEventRepository repo = CreateRepo(db);

        Tenant tenant = await SeedTenantAsync(db);
        AlertRule rule = await SeedAlertRuleAsync(db, tenant.Id);
        IntegrationEndpoint integration = await SeedIntegrationAsync(db, tenant.Id);

        DateTimeOffset triggeredAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        AlertEvent inFlight = await SeedAlertEventAsync(db, rule.Id, tenant.Id, triggeredAt);
        await SeedDeliveryAttemptAsync(db, inFlight.Id, integration.Id, IntegrationDeliveryAttemptStatus.Pending);

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-15);
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.AddMinutes(-2);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == inFlight.Id)).IsFalse();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_NonTriggeredStatus_ExcludesIt()
    {
        // Intent: Resolved and Acknowledged events must never be re-driven regardless of whether
        // they have a delivery attempt. The status filter is the outer guard.
        await using DatabaseContext db = CreateContext();
        IAlertEventRepository repo = CreateRepo(db);

        Tenant tenant = await SeedTenantAsync(db);
        AlertRule rule = await SeedAlertRuleAsync(db, tenant.Id);

        DateTimeOffset triggeredAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        AlertEvent resolved = await SeedAlertEventAsync(db, rule.Id, tenant.Id, triggeredAt, AlertEventStatus.Resolved);
        AlertEvent acknowledged = await SeedAlertEventAsync(db, rule.Id, tenant.Id, triggeredAt, AlertEventStatus.Acknowledged);

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-15);
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.AddMinutes(-2);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        await Assert.That(results.Any(e => e.Id == resolved.Id)).IsFalse();
        await Assert.That(results.Any(e => e.Id == acknowledged.Id)).IsFalse();
    }

    [Test]
    public async Task GetTriggeredEventsWithoutDeliveryAttemptsAsync_OrderedByTriggeredAt()
    {
        // Intent: the result set is ordered oldest-first so the re-drive pass processes events in
        // insertion order, making re-drive predictable and easier to reason about under partial
        // delivery (i.e. the job can track "delivered up to X" if needed).
        await using DatabaseContext db = CreateContext();
        IAlertEventRepository repo = CreateRepo(db);

        Tenant tenant = await SeedTenantAsync(db);
        AlertRule rule = await SeedAlertRuleAsync(db, tenant.Id);

        DateTimeOffset older = DateTimeOffset.UtcNow.AddMinutes(-12);
        DateTimeOffset newer = DateTimeOffset.UtcNow.AddMinutes(-8);

        AlertEvent olderEvent = await SeedAlertEventAsync(db, rule.Id, tenant.Id, older);
        AlertEvent newerEvent = await SeedAlertEventAsync(db, rule.Id, tenant.Id, newer);

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddMinutes(-15);
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.AddMinutes(-2);

        List<AlertEvent> results = await repo.GetTriggeredEventsWithoutDeliveryAttemptsAsync(notAfter, notBefore, CancellationToken.None);

        // Filter to only the events seeded by this test.
        List<AlertEvent> myResults = results.Where(e => (e.Id == olderEvent.Id) || (e.Id == newerEvent.Id)).ToList();
        await Assert.That(myResults.Count).IsEqualTo(2);
        await Assert.That(myResults[0].Id).IsEqualTo(olderEvent.Id);
        await Assert.That(myResults[1].Id).IsEqualTo(newerEvent.Id);
    }
}
