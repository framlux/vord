// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Hangfire;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.FleetManagement.Test.Integration;
using Framlux.Vord.BillingGrpc;
using Grpc.Core;
using Grpc.Core.Testing;
using Hangfire;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Integration.Repositories;

/// <summary>
/// Verifies against real Postgres that the subscription row lock does what the in-process tests can
/// only assume: a writer on the row waits for the transaction holding it, a duplicate agreement apply
/// that loses the race is told it was already applied, an override edit that arrives while an
/// agreement is being applied is refused once the apply commits, and a downgrade and an agreement
/// apply on the same tenant run one after the other in either order. Two connections are interleaved
/// by watching the server's lock waits, never by sleeping.
/// </summary>
public sealed class SubscriptionRowLockLiveTests
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
    public async Task GetSubscriptionForUpdate_KeepsAWriterWaitingUntilTheTransactionEnds()
    {
        using CancellationTokenSource failsafe = NewFailsafe();
        string writerName = NewApplicationName("writer");
        using DatabaseContext holderDb = CreateContext();
        int tenantId = await SeedTenantAsync(holderDb, SubscriptionTier.Team);
        DatabaseRepository holder = new(holderDb, NullLogger<DatabaseRepository>.Instance);

        Task<int> writer;
        using (IDatabaseTransaction transaction = await holder.BeginTransactionAsync(CancellationToken.None))
        {
            await holder.GetSubscriptionForUpdateAsync(tenantId, CancellationToken.None);
            writer = Task.Run(async () =>
            {
                using DatabaseContext writerDb = CreateContext(writerName);

                return await new DatabaseRepository(writerDb, NullLogger<DatabaseRepository>.Instance)
                    .UpdateSubscriptionStateAsync(tenantId, SubscriptionTier.Pro, SubscriptionStatus.Active);
            });

            bool blocked = await PostgresLockProbe.WaitUntilBlockedAsync(
                _migratedConnectionString, writerName, writer, failsafe.Token);

            await Assert.That(blocked).IsTrue();
            await Assert.That(writer.IsCompleted).IsFalse();
            await transaction.CommitAsync(CancellationToken.None);
        }

        int updated = await writer;
        TenantSubscription? row = await holder.GetSubscriptionForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(updated).IsEqualTo(1);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Pro);
    }

    [Test]
    public async Task DuplicateApply_LosingTheGuardedUpdateToItsTwin_ReportsAlreadyApplied()
    {
        using CancellationTokenSource failsafe = NewFailsafe();
        string twinName = NewApplicationName("twin");
        using DatabaseContext firstDb = CreateContext();
        int tenantId = await SeedTenantAsync(firstDb, SubscriptionTier.Team);
        DatabaseRepository first = new(firstDb, NullLogger<DatabaseRepository>.Instance);
        await first.ApplyEnterpriseSubscriptionAsync(tenantId, 1, TermEnd);

        EnterpriseApplyOutcome firstOutcome;
        Task<EnterpriseApplyOutcome> twin;
        using (IDatabaseTransaction transaction = await first.BeginTransactionAsync(CancellationToken.None))
        {
            firstOutcome = await first.ApplyEnterpriseSubscriptionAsync(tenantId, 2, TermEnd.AddDays(2));

            // The twin reads the row at revision 1 and then waits on the guarded update behind the
            // transaction above, which is the interleaving two deliveries of one revision produce.
            twin = Task.Run(async () =>
            {
                using DatabaseContext twinDb = CreateContext(twinName);

                return await new DatabaseRepository(twinDb, NullLogger<DatabaseRepository>.Instance)
                    .ApplyEnterpriseSubscriptionAsync(tenantId, 2, TermEnd.AddDays(2));
            });
            bool blocked = await PostgresLockProbe.WaitUntilBlockedAsync(
                _migratedConnectionString, twinName, twin, failsafe.Token);

            await Assert.That(blocked).IsTrue();
            await transaction.CommitAsync(CancellationToken.None);
        }

        EnterpriseApplyOutcome twinOutcome = await twin;
        await Assert.That(firstOutcome).IsEqualTo(EnterpriseApplyOutcome.Applied);
        await Assert.That(twinOutcome).IsEqualTo(EnterpriseApplyOutcome.AlreadyApplied);
    }

    [Test]
    public async Task SetTenantOverride_WhileAnAgreementIsBeingApplied_WaitsThenIsRefusedAndWritesNothing()
    {
        using CancellationTokenSource failsafe = NewFailsafe();
        string editorName = NewApplicationName("editor");
        using DatabaseContext applierDb = CreateContext();
        int tenantId = await SeedTenantAsync(applierDb, SubscriptionTier.Team);
        string tenantExternalId = (await applierDb.Tenants.FirstAsync(t => t.Id == tenantId)).ExternalId;
        DatabaseRepository applier = new(applierDb, NullLogger<DatabaseRepository>.Instance);
        FleetAdminService service = BuildFleetAdminService(editorName);

        Task<SetTenantOverrideResponse> edit;
        using (IDatabaseTransaction transaction = await applier.BeginTransactionAsync(CancellationToken.None))
        {
            await applier.ApplyEnterpriseSubscriptionAsync(tenantId, 1, TermEnd);

            // The editor's up-front tier check still reads Team, because the apply has not committed.
            // Only the row lock stands between its guarded write and a successful override on a tenant
            // that is about to be Enterprise.
            edit = Task.Run(() => service.SetTenantOverride(
                new SetTenantOverrideRequest { TenantExternalId = tenantExternalId, MachineLimit = 7 },
                NewCallContext()));
            bool blocked = await PostgresLockProbe.WaitUntilBlockedAsync(
                _migratedConnectionString, editorName, edit, failsafe.Token);

            await Assert.That(blocked).IsTrue();
            await transaction.CommitAsync(CancellationToken.None);
        }

        RpcException? refusal = await Assert.ThrowsAsync<RpcException>(() => edit);
        TenantSubscriptionOverride? written = await applierDb.TenantSubscriptionOverrides
            .FirstOrDefaultAsync(o => o.TenantId == tenantId);
        await Assert.That(refusal!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
        await Assert.That(written).IsNull();
    }

    [Test]
    public async Task RemoveTenantOverride_WhileAnAgreementIsBeingApplied_WaitsThenIsRefusedAndKeepsTheAgreementLimits()
    {
        using CancellationTokenSource failsafe = NewFailsafe();
        string editorName = NewApplicationName("remover");
        using DatabaseContext applierDb = CreateContext();
        int tenantId = await SeedTenantAsync(applierDb, SubscriptionTier.Team);
        string tenantExternalId = (await applierDb.Tenants.FirstAsync(t => t.Id == tenantId)).ExternalId;
        DatabaseRepository applier = new(applierDb, NullLogger<DatabaseRepository>.Instance);
        await applier.UpsertOverrideAsync(tenantId, 3, 3, 3, 3, 3, CancellationToken.None);
        FleetAdminService service = BuildFleetAdminService(editorName);

        Task<RemoveTenantOverrideResponse> removal;
        using (IDatabaseTransaction transaction = await applier.BeginTransactionAsync(CancellationToken.None))
        {
            await applier.ApplyEnterpriseSubscriptionAsync(tenantId, 1, TermEnd);
            await applier.UpsertOverrideAsync(tenantId, 500, 180, 40, 20, int.MaxValue, CancellationToken.None);

            removal = Task.Run(() => service.RemoveTenantOverride(
                new RemoveTenantOverrideRequest { TenantExternalId = tenantExternalId },
                NewCallContext()));
            bool blocked = await PostgresLockProbe.WaitUntilBlockedAsync(
                _migratedConnectionString, editorName, removal, failsafe.Token);

            await Assert.That(blocked).IsTrue();
            await transaction.CommitAsync(CancellationToken.None);
        }

        RpcException? refusal = await Assert.ThrowsAsync<RpcException>(() => removal);
        TenantSubscriptionOverride? kept = await applierDb.TenantSubscriptionOverrides
            .FirstOrDefaultAsync(o => o.TenantId == tenantId);
        await Assert.That(refusal!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
        await Assert.That(kept!.MachineLimit).IsEqualTo(500);
    }

    /// <summary>
    /// A deletion that is committing when an agreement arrives: the apply must wait for the deletion's
    /// cleanup, then see the tier the deletion left (Free) rather than the Team it started from.
    /// </summary>
    [Test]
    public async Task ApplyArrivingDuringADeletion_WaitsForItsCleanupAndSeesTheFreeTierItLeft()
    {
        using CancellationTokenSource failsafe = NewFailsafe();
        string applyName = NewApplicationName("apply");
        using DatabaseContext deletionDb = CreateContext();
        int tenantId = await SeedTenantWithResourcesAsync(deletionDb, SubscriptionTier.Team, machines: 5);
        DatabaseRepository deletionRepo = new(deletionDb, NullLogger<DatabaseRepository>.Instance);
        CommitGatedTransactionProvider gate = new(deletionRepo);
        BillingWebhookHandler webhook = BuildWebhookHandler(deletionRepo, gate);
        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();

        Task deletion = Task.Run(() => webhook.HandleSubscriptionDeletedAsync(tenantId, CancellationToken.None));
        await Task.WhenAny(gate.ReachedCommit, deletion);
        await Assert.That(gate.ReachedCommit.IsCompleted).IsTrue();

        Task<EnterpriseApplyOutcome> apply = Task.Run(async () =>
        {
            using DatabaseContext applyDb = CreateContext(applyName);
            DatabaseRepository applyRepo = new(applyDb, NullLogger<DatabaseRepository>.Instance);

            return await BuildAgreementHandler(applyRepo, applyRepo, provisioner)
                .ApplyAsync(Terms(tenantId, revision: 1), CancellationToken.None);
        });
        bool blocked = await PostgresLockProbe.WaitUntilBlockedAsync(
            _migratedConnectionString, applyName, apply, failsafe.Token);
        await Assert.That(blocked).IsTrue();
        gate.Release();
        await deletion;
        EnterpriseApplyOutcome outcome = await apply;

        await Assert.That(outcome).IsEqualTo(EnterpriseApplyOutcome.Applied);
        await provisioner.Received(1).RestoreForTierAsync(
            tenantId,
            Arg.Is<TenantSubscription?>(p => (p != null) && (p.Tier == SubscriptionTier.Free)),
            SubscriptionTier.Enterprise,
            SubscriptionStatus.Active,
            Arg.Any<CancellationToken>());
        TenantSubscription? row = await deletionRepo.GetSubscriptionForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Enterprise);
    }

    /// <summary>
    /// The case the cleanup-before-commit ordering exists for: an agreement is being applied when the
    /// Stripe deletion for the same tenant arrives. The deletion waits, then finds an Enterprise row,
    /// changes nothing and trims nothing.
    /// </summary>
    [Test]
    public async Task DeletionArrivingDuringAnApply_WaitsThenLeavesTheEnterpriseTenantUntouched()
    {
        using CancellationTokenSource failsafe = NewFailsafe();
        string deletionName = NewApplicationName("deletion");
        using DatabaseContext applyDb = CreateContext();
        int tenantId = await SeedTenantWithResourcesAsync(applyDb, SubscriptionTier.Team, machines: 5);
        DatabaseRepository applyRepo = new(applyDb, NullLogger<DatabaseRepository>.Instance);
        CommitGatedTransactionProvider gate = new(applyRepo);
        EnterpriseAgreementHandler agreement = BuildAgreementHandler(gate, applyRepo, Substitute.For<IBuiltInAlertRuleProvisioner>());

        Task<EnterpriseApplyOutcome> apply = Task.Run(() => agreement.ApplyAsync(Terms(tenantId, revision: 1), CancellationToken.None));
        await Task.WhenAny(gate.ReachedCommit, apply);
        await Assert.That(gate.ReachedCommit.IsCompleted).IsTrue();

        Task deletion = Task.Run(async () =>
        {
            using DatabaseContext deletionDb = CreateContext(deletionName);
            DatabaseRepository deletionRepo = new(deletionDb, NullLogger<DatabaseRepository>.Instance);

            await BuildWebhookHandler(deletionRepo, deletionRepo).HandleSubscriptionDeletedAsync(tenantId, CancellationToken.None);
        });
        bool blocked = await PostgresLockProbe.WaitUntilBlockedAsync(
            _migratedConnectionString, deletionName, deletion, failsafe.Token);
        await Assert.That(blocked).IsTrue();
        gate.Release();
        await apply;
        await deletion;

        TenantSubscription? row = await applyRepo.GetSubscriptionForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await Assert.That(await applyDb.Machines.CountAsync(m => (m.TenantId == tenantId) && (m.IsDeleted == false))).IsEqualTo(5);
        await Assert.That((await applyDb.TenantOidcConfigurations.FirstAsync(c => c.TenantId == tenantId)).IsEnabled).IsTrue();
        await Assert.That(await applyDb.AlertRules.CountAsync(r => (r.TenantId == tenantId) && (r.IsEnabled == false))).IsEqualTo(0);
        await Assert.That(await applyDb.AuditLog.CountAsync(a => (a.TenantId == tenantId) && (a.Action == AuditAction.SubscriptionDowngraded))).IsEqualTo(0);
    }

    /// <summary>
    /// The provisioner tolerates a concurrent seed's unique violation, but inside the apply's
    /// transaction that violation aborts it, and PostgreSQL then lets COMMIT return as though it had
    /// succeeded. The audit row, written after the restore, is what makes the apply fail instead.
    /// </summary>
    [Test]
    public async Task Apply_WhenTheRestoreSwallowsAUniqueViolation_FailsRatherThanCommittingASilentRollback()
    {
        using DatabaseContext db = CreateContext();
        int tenantId = await SeedTenantAsync(db, SubscriptionTier.Team);
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);
        IBuiltInAlertRuleProvisioner provisioner = Substitute.For<IBuiltInAlertRuleProvisioner>();
        provisioner.RestoreForTierAsync(
                Arg.Any<int>(), Arg.Any<TenantSubscription?>(), Arg.Any<SubscriptionTier>(), Arg.Any<SubscriptionStatus>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await repo.InsertAlertRulesAsync([BuiltInRule(tenantId)], CancellationToken.None);
                try
                {
                    await repo.InsertAlertRulesAsync([BuiltInRule(tenantId)], CancellationToken.None);
                }
                catch (PostgresException ex) when (ex.SqlState == "23505")
                {
                    // Swallowed exactly as the provisioner swallows it.
                }
            });
        EnterpriseAgreementHandler handler = BuildAgreementHandler(repo, repo, provisioner);

        PostgresException? failure = await Assert.ThrowsAsync<PostgresException>(
            () => handler.ApplyAsync(Terms(tenantId, revision: 1), CancellationToken.None));

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(failure!.SqlState).IsEqualTo("25P02");
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Team);
        await Assert.That(row.AppliedAgreementRevision).IsNull();
        await Assert.That(await repo.GetOverrideForTenantAsync(tenantId, CancellationToken.None)).IsNull();
        await Assert.That(await db.AlertRules.CountAsync(r => r.TenantId == tenantId)).IsEqualTo(0);
    }

    // ========== Helpers ==========

    private static AlertRule BuiltInRule(int tenantId)
    {
        return new AlertRule
        {
            TenantId = tenantId,
            Name = "Live built-in rule",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 90m,
            DurationMinutes = 0,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            NotifyEmail = true,
            NotifyWebhook = false,
            IsCustom = false,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static CancellationTokenSource NewFailsafe()
    {
        return new CancellationTokenSource(TimeSpan.FromMinutes(2));
    }

    private static string NewApplicationName(string role)
    {
        return $"vord-test-{role}-{Guid.NewGuid():N}";
    }

    private static EnterpriseAgreementTerms Terms(int tenantId, int revision)
    {
        return new EnterpriseAgreementTerms(tenantId, 42, revision, 500, 180, int.MaxValue, 40, 20, TermEnd);
    }

    private static EnterpriseAgreementHandler BuildAgreementHandler(
        IDatabaseTransactionProvider transactionProvider, DatabaseRepository repo, IBuiltInAlertRuleProvisioner provisioner)
    {
        return new EnterpriseAgreementHandler(
            transactionProvider,
            repo,
            repo,
            repo,
            provisioner,
            new RetentionReclassifyDispatcher(Substitute.For<IBackgroundJobClient>(), NullLogger<RetentionReclassifyDispatcher>.Instance),
            NullLogger<EnterpriseAgreementHandler>.Instance);
    }

    private static BillingWebhookHandler BuildWebhookHandler(DatabaseRepository repo, IDatabaseTransactionProvider transactionProvider)
    {
        DowngradeCleanupService cleanup = new(
            repo, repo, repo, repo, repo, repo,
            Substitute.For<IApiKeyCacheInvalidator>(),
            NullLogger<DowngradeCleanupService>.Instance);

        return new BillingWebhookHandler(
            transactionProvider,
            repo,
            repo,
            Substitute.For<IBuiltInAlertRuleProvisioner>(),
            cleanup,
            new RetentionReclassifyDispatcher(Substitute.For<IBackgroundJobClient>(), NullLogger<RetentionReclassifyDispatcher>.Instance),
            NullLogger<BillingWebhookHandler>.Instance);
    }

    /// <summary>
    /// A fleet admin service over real repositories whose every database session carries the given
    /// application name, so the test can see when its transaction is waiting on a lock.
    /// </summary>
    private static FleetAdminService BuildFleetAdminService(string applicationName)
    {
        ServiceCollection services = new();
        services.AddScoped(_ => CreateContext(applicationName));
        services.AddScoped(sp => new DatabaseRepository(sp.GetRequiredService<DatabaseContext>(), NullLogger<DatabaseRepository>.Instance));
        services.AddScoped<ITenantRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<ISubscriptionRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<ITenantSubscriptionOverrideRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IDatabaseTransactionProvider>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IAuditLogRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped(_ => new RetentionReclassifyDispatcher(
            Substitute.For<IBackgroundJobClient>(), NullLogger<RetentionReclassifyDispatcher>.Instance));
        ServiceProvider provider = services.BuildServiceProvider();

        return new FleetAdminService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IInternalCallerAuthorizer>(),
            Substitute.For<IOidcSecretProtector>(),
            NullLogger<FleetAdminService>.Instance,
            Substitute.For<IConnectionMultiplexer>(),
            Substitute.For<JobStorage>(),
            TimeProvider.System,
            Substitute.For<IBackgroundJobClientV2>(),
            Options.Create(new HangfireOptions()));
    }

    private static ServerCallContext NewCallContext()
    {
        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None,
            peer: "127.0.0.1",
            authContext: null,
            contextPropagationToken: null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => new WriteOptions(),
            writeOptionsSetter: _ => { });
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

    private static DatabaseContext CreateContext(string? applicationName = null)
    {
        string connectionString = (applicationName is null)
            ? _migratedConnectionString
            : PostgresLockProbe.WithApplicationName(_migratedConnectionString, applicationName);
        DataOptions<DatabaseContext> options = new(new DataOptions().UsePostgreSQL(connectionString));

        return new DatabaseContext(options);
    }

    private static async Task<int> SeedTenantAsync(DatabaseContext db, SubscriptionTier tier)
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
        await db.InsertAsync(new TenantSubscription
        {
            TenantId = tenantId,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        return tenantId;
    }

    /// <summary>
    /// Seeds a tenant with the resources the Free cleanup acts on: machines beyond the Free limit,
    /// an enabled custom OIDC configuration, and enabled built-in and custom alert rules.
    /// </summary>
    private static async Task<int> SeedTenantWithResourcesAsync(DatabaseContext db, SubscriptionTier tier, int machines)
    {
        int tenantId = await SeedTenantAsync(db, tier);
        for (int i = 0; i < machines; i++)
        {
            await db.InsertWithInt64IdentityAsync(new Machine
            {
                TenantId = tenantId,
                ApiKeyHash = Guid.NewGuid().ToString("N"),
                Name = $"Live Machine {i}",
                SerialNumber = $"serial-{Guid.NewGuid():N}",
                SystemId = $"system-{Guid.NewGuid():N}",
                AssetTagNumber = null,
                MachineType = MachineTypes.Unknown,
                OperatingSystem = OperatingSystems.Unknown,
                RegistrationTokenId = 0,
                RegisteredOn = DateTimeOffset.UtcNow.AddMinutes(i),
                IsDeleted = false,
            });
        }

        await db.InsertAsync(new TenantOidcConfiguration
        {
            TenantId = tenantId,
            Authority = "https://login.example.com",
            ClientId = "live-client",
            ClientSecret = "encrypted",
            MetadataAddress = null,
            EmailDomain = "example.com",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        foreach (bool isCustom in new[] { false, true })
        {
            await db.InsertAsync(new AlertRule
            {
                TenantId = tenantId,
                Name = isCustom ? "Live custom rule" : "Live built-in rule",
                Metric = isCustom ? AlertMetric.CpuUsage : AlertMetric.MemoryUsage,
                Operator = AlertOperator.GreaterThan,
                Threshold = 90m,
                DurationMinutes = 0,
                Severity = AlertSeverity.Warning,
                IsEnabled = true,
                NotifyEmail = false,
                NotifyWebhook = false,
                IsCustom = isCustom,
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        return tenantId;
    }
}
