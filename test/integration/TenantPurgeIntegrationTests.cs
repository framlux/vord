// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Jobs;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Integration;

/// <summary>
/// End-to-end live test for the tenant-deletion teardown against a real Postgres backend
/// (Testcontainers). This is the mandatory correctness gate for <see cref="TenantDeletionHandler"/>
/// (Phase 1) and <see cref="TenantPurgeJob"/> (Phase 2): it proves the purge deletes every
/// tenant-scoped row — including the partitioned <c>MachineTelemetry</c>, <c>AlertEvents</c>, and
/// <c>RemoteCommands</c> tables — under real foreign-key enforcement, which the in-memory SQLite
/// used by the unit tests does not provide.
/// </summary>
public sealed class TenantPurgeIntegrationTests
{
    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    // A non-null JSON payload seeded into RemoteCommand.Params (a jsonb column). Inserting this
    // through LinqToDB exercises the DataType.BinaryJson mapping — without it the parameter is sent
    // as text and Postgres rejects it, so this doubles as the regression guard for that annotation.
    private const string RemoteCommandParamsJson = """{"target":"reboot"}""";

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

    private static DatabaseRepository CreateRepository(DatabaseContext db)
    {
        return new DatabaseRepository(db, NullLogger<DatabaseRepository>.Instance);
    }

    [Test]
    public async Task EndToEndPurge_RealPostgres_DeletesEveryTenantScopedRowAndMasksOrphanedUsers()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepository(db);

        // ----- seed: creator/admin user, then tenant A and tenant B -----
        UserAccount admin = await SeedUserAsync(db, "admin");
        Tenant tenantA = await SeedTenantAsync(db, admin.Id, "Tenant A");
        Tenant tenantB = await SeedTenantAsync(db, admin.Id, "Tenant B");

        // U is active in both A and B; V is active only in A. Both must retain their real
        // membership rows and PII prior to the purge running.
        UserAccount userU = await SeedUserAsync(db, "user-u");
        UserAccount userV = await SeedUserAsync(db, "user-v");
        await SeedUserTenantRoleAsync(db, userU.Id, tenantA.Id, admin.Id);
        await SeedUserTenantRoleAsync(db, userU.Id, tenantB.Id, admin.Id);
        await SeedUserTenantRoleAsync(db, userV.Id, tenantA.Id, admin.Id);

        // ----- seed: tenant A's full operational graph -----
        RegistrationToken tokenA = await SeedRegistrationTokenAsync(db, tenantA.Id, admin.Id);
        Machine machineA = await SeedMachineAsync(db, tenantA.Id, tokenA.Id);
        await SeedMachineStateDetailAsync(db, machineA.Id);
        await SeedMachineStateSummaryAsync(db, machineA.Id, tenantA.Id);
        await SeedMachineTelemetryAsync(db, machineA.Id, tenantA.Id);

        UserSigningKey signingKeyA = await SeedUserSigningKeyAsync(db, userU.Id, tenantA.Id);
        await SeedMachineAuthorizedKeyAsync(db, machineA.Id, signingKeyA.Id, tenantA.Id, admin.Id);
        await SeedRemoteCommandAsync(db, tenantA.Id, machineA.Id, userU.Id, signingKeyA.Id);

        // The jsonb Params round-trips through LinqToDB (proves the BinaryJson mapping — the insert
        // would fail with a 42804 type error without it). Postgres jsonb is semantic-preserving, not
        // byte-preserving (it re-serializes, e.g. adding a space after the colon), so compare with
        // insignificant whitespace removed rather than byte-for-byte.
        RemoteCommand? seededCommand = await db.RemoteCommands.FirstOrDefaultAsync(c => c.TenantId == tenantA.Id);
        await Assert.That(seededCommand).IsNotNull();
        await Assert.That(seededCommand!.Params).IsNotNull();
        await Assert.That(seededCommand.Params!.Replace(" ", "")).IsEqualTo(RemoteCommandParamsJson);

        AlertRule alertRuleA = await SeedAlertRuleAsync(db, tenantA.Id, admin.Id);
        await SeedAlertRuleMachineAsync(db, alertRuleA.Id, machineA.Id);
        await SeedAlertConditionStateAsync(db, alertRuleA.Id, machineA.Id);
        AlertEvent alertEventA = await SeedAlertEventAsync(db, alertRuleA.Id, tenantA.Id, machineA.Id);
        await SeedAlertEmailDeliveryAttemptAsync(db, alertEventA.Id);

        IntegrationEndpoint integrationA = await SeedIntegrationEndpointAsync(db, tenantA.Id, admin.Id);
        await SeedIntegrationDeliveryAttemptAsync(db, alertEventA.Id, integrationA.Id);

        await SeedDataExportJobAsync(db, tenantA.Id, admin.Id);
        await SeedTenantOidcConfigurationAsync(db, tenantA.Id);
        await SeedTenantInvitationAsync(db, tenantA.Id, admin.Id);
        await SeedTenantSubscriptionAsync(db, tenantA.Id);
        await SeedTenantSubscriptionOverrideAsync(db, tenantA.Id);

        // A pre-existing audit entry for tenant A that must survive the purge (audit history is
        // never deleted, only the operational data and PII).
        await db.InsertAsync(new AuditLogEntry
        {
            TenantId = tenantA.Id,
            UserId = admin.Id,
            MachineId = null,
            Action = AuditAction.TenantCreated,
            ResourceType = AuditResourceType.Tenant,
            ResourceId = tenantA.Id.ToString(),
            Details = null,
            IpAddress = null,
            Timestamp = DateTimeOffset.UtcNow,
        });

        // ----- seed: tenant B's data, which must remain completely untouched -----
        RegistrationToken tokenB = await SeedRegistrationTokenAsync(db, tenantB.Id, admin.Id);
        Machine machineB = await SeedMachineAsync(db, tenantB.Id, tokenB.Id);
        await SeedMachineStateSummaryAsync(db, machineB.Id, tenantB.Id);
        AlertRule alertRuleB = await SeedAlertRuleAsync(db, tenantB.Id, admin.Id);
        await SeedTenantSubscriptionAsync(db, tenantB.Id);

        // ----- Phase 1: drive the real handler against Postgres. Uses requestedByUserId: 0 (the
        // admin-panel operator sentinel) to prove the 0->null FK mapping does not violate the real
        // FK constraints on Tenants.DisabledByUserId / AuditLog.UserId. -----
        FakeTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        billingApiClient.CancelSubscriptionImmediateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        billingApiClient.DeleteCustomerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        TenantDeletionHandler handler = new(
            repo, repo, repo, repo, billingApiClient, timeProvider, NullLogger<TenantDeletionHandler>.Instance);

        TenantDeletionResult requestResult = await handler.RequestDeletionAsync(
            tenantA.Id, requestedByUserId: 0, reason: "integration test", CancellationToken.None);

        await Assert.That(requestResult.Success).IsTrue();

        Tenant? deactivatedTenantA = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantA.Id);
        await Assert.That(deactivatedTenantA).IsNotNull();
        await Assert.That(deactivatedTenantA!.IsActive).IsFalse();
        await Assert.That(deactivatedTenantA.DisabledByUserId).IsNull();

        AuditLogEntry? deactivationAudit = await db.AuditLog
            .Where(e => (e.TenantId == tenantA.Id) && (e.Action == AuditAction.TenantDeletionRequested))
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync();
        await Assert.That(deactivationAudit).IsNotNull();
        await Assert.That(deactivationAudit!.UserId).IsNull();

        // ----- Phase 2: advance past the grace window and run the purge job -----
        timeProvider.Advance(TenantDeletionHandler.GraceWindow + TimeSpan.FromDays(1));

        TenantPurgeJob job = new(
            repo, repo, repo, billingApiClient, timeProvider, NullLogger<TenantPurgeJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        // ----- assert: every tenant-A-scoped table is empty -----
        await AssertZeroRowsForTenantAAsync(db, tenantA.Id, machineA.Id, alertRuleA.Id, alertEventA.Id, integrationA.Id);

        // ----- assert: Tenants(A) kept, disabled; original audit kept; new TenantPurged audit exists -----
        Tenant? tenantAAfterPurge = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantA.Id);
        await Assert.That(tenantAAfterPurge).IsNotNull();
        await Assert.That(tenantAAfterPurge!.IsActive).IsFalse();

        int originalAuditCount = await db.AuditLog.CountAsync(e =>
            (e.TenantId == tenantA.Id) && (e.Action == AuditAction.TenantCreated));
        await Assert.That(originalAuditCount).IsEqualTo(1);

        AuditLogEntry? purgedAudit = await db.AuditLog
            .Where(e => (e.Action == AuditAction.TenantPurged) && (e.ResourceId == tenantA.Id.ToString()))
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync();
        await Assert.That(purgedAudit).IsNotNull();
        await Assert.That(purgedAudit!.TenantId).IsNull();

        // ----- assert: the deletion row transitioned to Purged with PurgedAt set -----
        TenantDeletion? deletionRow = await db.TenantDeletions
            .Where(d => d.TenantId == tenantA.Id)
            .OrderByDescending(d => d.Id)
            .FirstOrDefaultAsync();
        await Assert.That(deletionRow).IsNotNull();
        await Assert.That(deletionRow!.Status).IsEqualTo(TenantDeletionStatus.Purged);
        await Assert.That(deletionRow.PurgedAt).IsNotNull();

        // ----- assert: V (orphaned) is masked; U (still active in B) is untouched -----
        UserAccount? maskedV = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == userV.Id);
        await Assert.That(maskedV).IsNotNull();
        await Assert.That(maskedV!.Id).IsEqualTo(userV.Id);
        await Assert.That(maskedV.Username).IsEqualTo($"deleted-user-{userV.Id}");
        await Assert.That(maskedV.ExternalId.StartsWith($"deleted-user-{userV.Id}:", StringComparison.Ordinal)).IsTrue();

        UserAccount? untouchedU = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == userU.Id);
        await Assert.That(untouchedU).IsNotNull();
        await Assert.That(untouchedU!.Username).IsEqualTo(userU.Username);
        await Assert.That(untouchedU.ExternalId).IsEqualTo(userU.ExternalId);

        bool userUStillActiveInB = await db.UserTenantRoles.AnyAsync(r =>
            (r.UserId == userU.Id) && (r.AssignedTenantId == tenantB.Id) && (r.IsActive == true));
        await Assert.That(userUStillActiveInB).IsTrue();

        // ----- assert: tenant B's data (and the B row itself) is completely intact -----
        Tenant? tenantBAfterPurge = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantB.Id);
        await Assert.That(tenantBAfterPurge).IsNotNull();
        await Assert.That(tenantBAfterPurge!.IsActive).IsTrue();

        await Assert.That(await db.Machines.AnyAsync(m => m.Id == machineB.Id)).IsTrue();
        await Assert.That(await db.MachineStateSummaries.AnyAsync(s => s.MachineId == machineB.Id)).IsTrue();
        await Assert.That(await db.AlertRules.AnyAsync(r => r.Id == alertRuleB.Id)).IsTrue();
        await Assert.That(await db.RegistrationTokens.AnyAsync(t => t.Id == tokenB.Id)).IsTrue();
        await Assert.That(await db.TenantSubscriptions.AnyAsync(s => s.TenantId == tenantB.Id)).IsTrue();

        // ----- idempotency: a second RunAsync (A now Purged, so no longer due) is a no-op -----
        int purgedAuditCountBeforeSecondRun = await db.AuditLog.CountAsync(e => e.Action == AuditAction.TenantPurged);
        await job.RunAsync(CancellationToken.None);
        int purgedAuditCountAfterSecondRun = await db.AuditLog.CountAsync(e => e.Action == AuditAction.TenantPurged);
        await Assert.That(purgedAuditCountAfterSecondRun).IsEqualTo(purgedAuditCountBeforeSecondRun);

        TenantDeletion? deletionRowAfterSecondRun = await db.TenantDeletions
            .Where(d => d.TenantId == tenantA.Id)
            .OrderByDescending(d => d.Id)
            .FirstOrDefaultAsync();
        await Assert.That(deletionRowAfterSecondRun!.Status).IsEqualTo(TenantDeletionStatus.Purged);
        await Assert.That(deletionRowAfterSecondRun.PurgedAt).IsEqualTo(deletionRow.PurgedAt);

        // ----- idempotency: a direct second PurgeTenantOperationalDataAsync does not throw and
        // leaves the already-zero counts at zero -----
        await repo.PurgeTenantOperationalDataAsync(tenantA.Id, CancellationToken.None);
        await AssertZeroRowsForTenantAAsync(db, tenantA.Id, machineA.Id, alertRuleA.Id, alertEventA.Id, integrationA.Id);
    }

    private static async Task AssertZeroRowsForTenantAAsync(
        DatabaseContext db, int tenantAId, long machineAId, int alertRuleAId, long alertEventAId, int integrationAId)
    {
        await Assert.That(await db.MachineStateDetails.CountAsync(x => x.MachineId == machineAId)).IsEqualTo(0);
        await Assert.That(await db.AlertConditionStates.CountAsync(x => x.AlertRuleId == alertRuleAId)).IsEqualTo(0);
        await Assert.That(await db.AlertRuleMachines.CountAsync(x => x.AlertRuleId == alertRuleAId)).IsEqualTo(0);
        await Assert.That(await db.IntegrationDeliveryAttempts.CountAsync(x => x.IntegrationEndpointId == integrationAId)).IsEqualTo(0);
        await Assert.That(await db.AlertEmailDeliveryAttempts.CountAsync(x => x.AlertEventId == alertEventAId)).IsEqualTo(0);

        await Assert.That(await db.MachineAuthorizedKeys.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.MachineStateSummaries.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.MachineTelemetry.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.AlertEvents.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.RemoteCommands.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.AlertRules.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.IntegrationEndpoints.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.RegistrationTokens.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.UserSigningKeys.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.DataExportJobs.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.Machines.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.TenantOidcConfigurations.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.TenantInvitations.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.TenantSubscriptionOverrides.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.TenantSubscriptions.CountAsync(x => x.TenantId == tenantAId)).IsEqualTo(0);
        await Assert.That(await db.UserTenantRoles.CountAsync(x => x.AssignedTenantId == tenantAId)).IsEqualTo(0);
    }

    // ----- seed helpers -----

    private static async Task<UserAccount> SeedUserAsync(DatabaseContext db, string label)
    {
        // The migration seeds a system user at Id 1 (see InitialMigration2's deferred self-FK), which
        // satisfies the Users.CreatedByUserId FK for every user created in these tests.
        UserAccount user = new()
        {
            ExternalId = $"ext-{label}-{Guid.NewGuid():N}",
            Username = $"{label}-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
            AuthProvider = AuthProviderType.Google,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        return user;
    }

    private static async Task<Tenant> SeedTenantAsync(DatabaseContext db, int createdByUserId, string name)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"{name} {Guid.NewGuid():N}"[..40],
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = createdByUserId,
            IsActive = true,
            LogoUrl = "",
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        return tenant;
    }

    private static async Task SeedUserTenantRoleAsync(DatabaseContext db, int userId, int tenantId, int assignedByUserId)
    {
        await db.InsertAsync(new UserTenantRole
        {
            UserId = userId,
            AssignedTenantId = tenantId,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = assignedByUserId,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        });
    }

    private static async Task<RegistrationToken> SeedRegistrationTokenAsync(DatabaseContext db, int tenantId, int createdByUserId)
    {
        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N").PadRight(64, '0')[..64],
            Name = "Integration Test Token",
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            IsRevoked = false,
        };
        token.Id = await db.InsertWithInt64IdentityAsync(token);

        return token;
    }

    private static async Task<Machine> SeedMachineAsync(DatabaseContext db, int tenantId, long registrationTokenId)
    {
        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N").PadRight(64, '0')[..64],
            Name = "Integration Test Machine",
            SerialNumber = Guid.NewGuid().ToString("N")[..32],
            SystemId = Guid.NewGuid().ToString("N")[..32],
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = registrationTokenId,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId,
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        return machine;
    }

    private static async Task SeedMachineStateDetailAsync(DatabaseContext db, long machineId)
    {
        await db.InsertAsync(new MachineStateDetail
        {
            MachineId = machineId,
        });
    }

    private static async Task SeedMachineStateSummaryAsync(DatabaseContext db, long machineId, int tenantId)
    {
        await db.InsertAsync(new MachineStateSummary
        {
            MachineId = machineId,
            TenantId = tenantId,
            Name = "Integration Test Machine",
            OperatingSystem = (byte)OperatingSystems.Ubuntu,
            MachineType = (byte)MachineTypes.BareMetalServer,
            HealthStatus = 0,
        });
    }

    private static async Task SeedMachineTelemetryAsync(DatabaseContext db, long machineId, int tenantId)
    {
        await db.InsertAsync(new MachineTelemetry
        {
            MachineId = machineId,
            TenantId = tenantId,
            RetentionClass = RetentionClass.Short,
            TelemetryType = 1,
            Payload = """{"cpuUsage":10}""",
            ReceivedAt = DateTimeOffset.UtcNow,
            ServerReceivedAt = DateTimeOffset.UtcNow,
            SourceEventId = Guid.NewGuid().ToString("N"),
        });
    }

    private static async Task<UserSigningKey> SeedUserSigningKeyAsync(DatabaseContext db, int userId, int tenantId)
    {
        UserSigningKey key = new()
        {
            UserId = userId,
            TenantId = tenantId,
            Label = "Integration Test Key",
            PublicKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            PublicKeyFingerprint = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        key.Id = await db.InsertWithInt32IdentityAsync(key);

        return key;
    }

    private static async Task SeedMachineAuthorizedKeyAsync(
        DatabaseContext db, long machineId, int signingKeyId, int tenantId, int authorizedByUserId)
    {
        await db.InsertAsync(new MachineAuthorizedKey
        {
            MachineId = machineId,
            SigningKeyId = signingKeyId,
            TenantId = tenantId,
            AuthorizedAt = DateTimeOffset.UtcNow,
            AuthorizedByUserId = authorizedByUserId,
        });
    }

    private static async Task SeedRemoteCommandAsync(DatabaseContext db, int tenantId, long machineId, int userId, int signingKeyId)
    {
        // Insert through LinqToDB with a non-null Params (jsonb) payload. This exercises the model's
        // DataType.BinaryJson mapping under real Postgres; if that annotation regresses, this insert
        // fails and the test catches it.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await db.InsertAsync(new RemoteCommand
        {
            CommandId = Guid.NewGuid().ToString(),
            TenantId = tenantId,
            MachineId = machineId,
            UserId = userId,
            SigningKeyId = signingKeyId,
            CommandType = "reboot",
            Params = RemoteCommandParamsJson,
            Nonce = Guid.NewGuid().ToString("N")[..32],
            Signature = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            CanonicalPayload = """{"command":"reboot"}""",
            Timestamp = now,
            ExpiresAt = now.AddMinutes(5),
            Status = RemoteCommandStatus.Pending,
            CreatedAt = now,
        });
    }

    private static async Task<AlertRule> SeedAlertRuleAsync(DatabaseContext db, int tenantId, int createdByUserId)
    {
        AlertRule rule = new()
        {
            TenantId = tenantId,
            Name = $"Integration Test Rule {Guid.NewGuid():N}",
            Description = "Integration test rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80m,
            DurationMinutes = 0,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = true,
            IsCustom = true,
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        return rule;
    }

    private static async Task SeedAlertRuleMachineAsync(DatabaseContext db, int alertRuleId, long machineId)
    {
        await db.InsertAsync(new AlertRuleMachine
        {
            AlertRuleId = alertRuleId,
            MachineId = machineId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task SeedAlertConditionStateAsync(DatabaseContext db, int alertRuleId, long machineId)
    {
        await db.InsertAsync(new AlertConditionState
        {
            AlertRuleId = alertRuleId,
            MachineId = machineId,
            FirstTriggeredAt = DateTimeOffset.UtcNow,
            LastObservedAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task<AlertEvent> SeedAlertEventAsync(DatabaseContext db, int alertRuleId, int tenantId, long machineId)
    {
        AlertEvent alertEvent = new()
        {
            AlertRuleId = alertRuleId,
            TenantId = tenantId,
            MachineId = machineId,
            Severity = AlertSeverity.Warning,
            Message = "Integration test alert event",
            Details = null,
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
        alertEvent.Id = await db.InsertWithInt64IdentityAsync(alertEvent);

        return alertEvent;
    }

    private static async Task SeedAlertEmailDeliveryAttemptAsync(DatabaseContext db, long alertEventId)
    {
        await db.InsertAsync(new AlertEmailDeliveryAttempt
        {
            AlertEventId = alertEventId,
            Recipient = "ops@example.com",
            Status = EmailDeliveryAttemptStatus.Succeeded,
            AttemptedAt = DateTimeOffset.UtcNow,
            SucceededAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task<IntegrationEndpoint> SeedIntegrationEndpointAsync(DatabaseContext db, int tenantId, int createdByUserId)
    {
        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Custom,
            Name = $"Integration Test Integration {Guid.NewGuid():N}",
            Configuration = """{"url":"https://hooks.example.com/test","secret":"test"}""",
            IsEnabled = true,
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        integration.Id = await db.InsertWithInt32IdentityAsync(integration);

        return integration;
    }

    private static async Task SeedIntegrationDeliveryAttemptAsync(DatabaseContext db, long alertEventId, int integrationEndpointId)
    {
        await db.InsertAsync(new IntegrationDeliveryAttempt
        {
            AlertEventId = alertEventId,
            IntegrationEndpointId = integrationEndpointId,
            Status = IntegrationDeliveryAttemptStatus.Succeeded,
            AttemptedAt = DateTimeOffset.UtcNow,
            SucceededAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task SeedDataExportJobAsync(DatabaseContext db, int tenantId, int requestedByUserId)
    {
        await db.InsertAsync(new DataExportJob
        {
            TenantId = tenantId,
            Status = DataExportJobStatus.Complete,
            RequestedByUserId = requestedByUserId,
            RequestedAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            ObjectKey = $"exports/{Guid.NewGuid():N}.zip",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            DownloadToken = Guid.NewGuid().ToString("N"),
        });
    }

    private static async Task SeedTenantOidcConfigurationAsync(DatabaseContext db, int tenantId)
    {
        await db.InsertAsync(new TenantOidcConfiguration
        {
            TenantId = tenantId,
            Authority = "https://idp.example.com",
            ClientId = "client-id",
            ClientSecret = "encrypted-secret",
            EmailDomain = "example.com",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task SeedTenantInvitationAsync(DatabaseContext db, int tenantId, int invitedByUserId)
    {
        await db.InsertAsync(new TenantInvitation
        {
            TenantId = tenantId,
            Email = "invitee@example.com",
            TokenHash = Guid.NewGuid().ToString("N"),
            Role = UserAccountRoles.Viewer,
            Status = InvitationStatus.Pending,
            InvitedByUserId = invitedByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });
    }

    private static async Task SeedTenantSubscriptionAsync(DatabaseContext db, int tenantId)
    {
        await db.InsertAsync(new TenantSubscription
        {
            TenantId = tenantId,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(30),
            CancelAtPeriodEnd = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task SeedTenantSubscriptionOverrideAsync(DatabaseContext db, int tenantId)
    {
        await db.InsertAsync(new TenantSubscriptionOverride
        {
            TenantId = tenantId,
            MachineLimit = 50,
            RetentionDays = 90,
            AlertRuleLimit = 20,
            WebhookLimit = 10,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }
}
