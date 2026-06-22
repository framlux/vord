// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Generators.SQLite;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Commands;
using Framlux.FleetManagement.Services.Core.DataExport;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Telemetry;
using Framlux.Vord.BillingGrpc;
using Hangfire;
using Hangfire.InMemory;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using System.Data;
using System.Reflection;
using System.Threading.RateLimiting;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that boots the full server with
/// SQLite in place of PostgreSQL and in-memory fakes in place of Redis.
/// Each instance owns an isolated in-memory SQLite database with all migrations applied.
/// </summary>
public class FunctionalTestFactory : WebApplicationFactory<Program>
{
    // FastEndpoints modifies a static JsonSerializerOptions during UseFastEndpoints.
    // The options become read-only after first serialization use, so concurrent host
    // creation causes InvalidOperationException. Serialize all host creation to prevent this.
    private static readonly object HostCreationLock = new();

    private readonly SqliteConnection _dbConnection;
    private string? _internalApiKey;

    /// <summary>
    /// NSubstitute mock for <see cref="IBillingApiClient"/> that replaces the real
    /// gRPC-based client in functional tests. Tests can configure return values on this
    /// mock before making HTTP/gRPC requests.
    /// Defaults match the behavior of <see cref="NoOpBillingApiClient"/>: all operations
    /// succeed and subscription status shows no pending cancellation.
    /// </summary>
    public IBillingApiClient BillingApiClientMock { get; } = CreateDefaultBillingApiClientMock();

    /// <summary>
    /// NSubstitute mock for <see cref="IBackgroundJobClient"/> that replaces the real
    /// Hangfire client in functional tests. Tests that exercise endpoints which enqueue
    /// Hangfire jobs (e.g., on-demand data-export processing) can assert against this
    /// mock instead of running the InMemory pipeline.
    /// </summary>
    public IBackgroundJobClient BackgroundJobClientMock { get; } = Substitute.For<IBackgroundJobClient>();

    /// <summary>
    /// When set, the factory skips replacing <see cref="IBackgroundJobClient"/> with a mock
    /// and registers a real Hangfire <c>BackgroundJobServer</c> against the InMemory storage.
    /// Used exclusively by the Hangfire end-to-end smoke test so that enqueued jobs are
    /// actually picked up and executed by a processing server inside the test host.
    /// </summary>
    public bool EnableHangfireProcessingServer { get; set; }

    /// <summary>
    /// Optional hook that runs at the end of <see cref="ConfigureWebHost"/> so individual
    /// tests can register extra services (e.g., a test sink, a job type) without subclassing
    /// the factory. Invoked after all default test-time overrides have been applied, so
    /// callers can both register new services and override factory defaults.
    /// </summary>
    public Action<IServiceCollection>? AdditionalTestServices { get; set; }

    /// <summary>
    /// Creates a new functional test factory with an isolated in-memory SQLite database.
    /// </summary>
    public FunctionalTestFactory()
    {
        // Provide config values that Program.cs reads before ConfigureTestServices can run.
        // These are required to prevent KeyNotFoundException during startup.
        Environment.SetEnvironmentVariable("Database__HOSTNAME", "localhost");
        Environment.SetEnvironmentVariable("Database__USER", "test");
        Environment.SetEnvironmentVariable("Database__PASSWORD", "test");
        Environment.SetEnvironmentVariable("Database__DB", "test");
        // abortConnect=false prevents ConnectionMultiplexer.Connect from throwing when Redis
        // is not available. The multiplexer is immediately replaced in ConfigureTestServices.
        Environment.SetEnvironmentVariable("Redis__ConnectionString", "localhost,abortConnect=false,connectTimeout=1");

        // Provide dummy OAuth client IDs/secrets to prevent OAuthOptions.Validate() from
        // throwing. The OAuth providers are never exercised in functional tests.
        Environment.SetEnvironmentVariable("Authentication__GitHub__ClientId", "test-github-id");
        Environment.SetEnvironmentVariable("Authentication__GitHub__ClientSecret", "test-github-secret");
        Environment.SetEnvironmentVariable("Authentication__Google__ClientId", "test-google-id");
        Environment.SetEnvironmentVariable("Authentication__Google__ClientSecret", "test-google-secret");
        Environment.SetEnvironmentVariable("Authentication__Microsoft__ClientId", "test-microsoft-id");
        Environment.SetEnvironmentVariable("Authentication__Microsoft__ClientSecret", "test-microsoft-secret");

        // Enable billing integration for functional tests so billing endpoints and gRPC are mapped
        Environment.SetEnvironmentVariable("Billing__Enabled", "true");
        Environment.SetEnvironmentVariable("Billing__GrpcUrl", "http://localhost:12235");

        _dbConnection = new SqliteConnection("Data Source=:memory:");
        _dbConnection.Open();

        // Enforce foreign keys for the duration of the connection. SQLite defaults to OFF,
        // so cascade/restrict semantics only fire when this PRAGMA is explicitly ON.
        using (SqliteCommand fkCmd = _dbConnection.CreateCommand())
        {
            fkCmd.CommandText = "PRAGMA foreign_keys = ON";
            fkCmd.ExecuteNonQuery();
        }

        ApplyMigrations();
        SeedTierFeatureLimits();
        SeedSystemUser();

        // Catches the case where a future migration change drops the UserAccounts table,
        // renames its columns, or otherwise causes the manual seed above to silently no-op.
        using SqliteCommand probe = _dbConnection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM UserAccounts WHERE Id = 1 AND IsSystem = 1";
        object? probeResult = probe.ExecuteScalar();
        long seededCount = probeResult is long l ? l : 0L;
        if (seededCount != 1)
        {
            throw new InvalidOperationException(
                $"FunctionalTestFactory failed to seed the system user (Id=1). Found {seededCount} matching rows. " +
                "Check that SeedSystemUser() column list matches the UserAccounts table schema.");
        }
    }

    /// <summary>
    /// Creates a new <see cref="DatabaseContext"/> backed by the in-memory SQLite database.
    /// Use this to seed test data before making requests.
    /// </summary>
    public DatabaseContext CreateDbContext()
    {
        DataOptions<DatabaseContext> dataOptions = new(
            new DataOptions()
                .UseSQLite(SQLiteProvider.Microsoft)
                .UseConnection(
                    SQLiteTools.GetDataProvider(SQLiteProvider.Microsoft),
                    _dbConnection,
                    false));

        return new DatabaseContext(dataOptions);
    }

    /// <summary>
    /// Sets the internal API key for billing gRPC authentication.
    /// Must be called before the first HTTP request.
    /// </summary>
    public void WithInternalApiKey(string key)
    {
        _internalApiKey = key;
    }

    /// <inheritdoc/>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        lock (HostCreationLock)
        {
            return base.CreateHost(builder);
        }
    }

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        if (_internalApiKey is not null)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["InternalApi:Key"] = _internalApiKey
                });
            });
        }

        builder.ConfigureTestServices(services =>
        {
            // Replace DatabaseContext with SQLite-backed version
            services.RemoveAll<DatabaseContext>();
            services.RemoveAll<DataOptions<DatabaseContext>>();
            services.AddScoped(_ => CreateDbContext());

            // Replace Redis with in-memory fake
            services.RemoveAll<IConnectionMultiplexer>();
            services.AddSingleton(FakeRedisConnection.Create());

            // Replace Redis-dependent services with in-memory implementations
            services.RemoveAll<IMachinePingService>();
            services.AddSingleton<IMachinePingService, InMemoryMachinePingService>();

            services.RemoveAll<ITelemetryDeduplicationService>();
            services.AddSingleton<ITelemetryDeduplicationService, InMemoryTelemetryDeduplicationService>();

            // Replace PostgreSQL-specific SQL dialect with SQLite-compatible version for tests
            services.RemoveAll<ISqlDialect>();
            services.AddSingleton<ISqlDialect, NoOpSqlDialect>();

            // Replace Postgres-backed Hangfire storage with Hangfire.InMemory for tests.
            //
            // Hangfire's `AddHangfire(...)` registers BOTH `JobStorage` (TryAddSingleton) AND an
            // `IGlobalConfiguration` factory that, when resolved, runs the storage callback (which
            // in production calls `UsePostgreSqlStorage(...)` and opens a Postgres connection).
            // Removing `JobStorage` alone is insufficient — the dashboard's
            // `ThrowIfNotConfigured` triggers the production `IGlobalConfiguration` factory.
            // Strip every Hangfire-namespaced registration, then re-call AddHangfire with
            // InMemory storage so all of Hangfire's TryAddSingleton calls land cleanly.
            ServiceDescriptor[] hangfireDescriptors = services
                .Where(s => s.ServiceType.Namespace?.StartsWith("Hangfire", StringComparison.Ordinal) == true)
                .ToArray();
            foreach (ServiceDescriptor descriptor in hangfireDescriptors)
            {
                services.Remove(descriptor);
            }
            services.AddHangfire(config => config.UseInMemoryStorage());

            if (EnableHangfireProcessingServer == false)
            {
                // Replace IBackgroundJobClient with an NSubstitute mock so endpoint enqueue calls
                // can be asserted in functional tests. AddHangfire above registers the real
                // BackgroundJobClient against InMemory storage; we swap it for the mock here.
                services.RemoveAll<IBackgroundJobClient>();
                services.AddSingleton(BackgroundJobClientMock);
            }
            else
            {
                // Hangfire end-to-end smoke test path: keep the real BackgroundJobClient that
                // AddHangfire registered against InMemory storage, and stand up a processing
                // server in-process so enqueued jobs actually run.
                services.AddHangfireServer(options =>
                {
                    options.WorkerCount = 1;
                    options.ServerName = $"vord-functional-{Guid.NewGuid():N}";
                    options.ShutdownTimeout = TimeSpan.FromSeconds(5);
                });
            }

            // PostgresAdvisoryLockProvider needs a real Postgres connection; tests run on SQLite,
            // so swap it for a no-op that always grants the lock. No multi-replica concurrency
            // exists in the in-process functional fixture.
            services.RemoveAll<IAdvisoryLockProvider>();
            services.AddSingleton<IAdvisoryLockProvider, NoOpAdvisoryLockProvider>();

            // Disable rate limiting — replace the Redis-backed global limiter with a no-op.
            // The original limiter captures a disconnected Redis multiplexer; overriding
            // GlobalLimiter avoids connection errors during test requests.
            services.Configure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(options =>
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                    _ => RateLimitPartition.GetNoLimiter("test"));
            });

            // Relax the antiforgery cookie's SecurePolicy in tests. Production uses
            // CookieSecurePolicy.Always so the Secure flag is set whenever Traefik + ForwardedHeaders
            // report Request.Scheme = "https"; functional tests run over HTTP, where
            // ASP.NET Core's CookieManager refuses to emit a Secure cookie and Hangfire's
            // dashboard antiforgery mint then fails. SameAsRequest keeps the mint working in
            // tests without weakening the production setting.
            services.Configure<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>(options =>
            {
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });

            // Remove hosted services that depend on Redis, distributed locking, or billing gRPC.
            // All background services must be removed to prevent concurrent SQLite access on the
            // shared in-memory connection, which causes intermittent "database is locked" errors.
            RemoveHostedService<MachineStateStreamingService>(services);

            // Replace BillingApiClient with an NSubstitute mock.
            // The real BillingApiClient requires a live billing gRPC endpoint
            // which is not available in functional tests.
            // Tests can configure specific mock behavior via BillingApiClientMock.
            services.RemoveAll<IBillingApiClient>();
            services.AddSingleton(BillingApiClientMock);

            // Replace object storage with a fake for tests
            services.RemoveAll<IObjectStorageService>();
            services.AddSingleton(NSubstitute.Substitute.For<IObjectStorageService>());

            // Replace cookie auth handler with TestAuthHandler so REST endpoints
            // can be tested without a real OAuth flow. The MultiAuth policy scheme
            // still routes API key requests to the API key handler.
            services.RemoveAll<CookiePrincipalValidator>();
            services.PostConfigure<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme, options =>
                {
                    // Prevent the cookie handler from trying to resolve
                    // CookiePrincipalValidator (which hits Redis/DB).
                    options.EventsType = null;
                });

            // Register TestAuthHandler in DI so it can be resolved when the scheme provider maps to it
            services.AddTransient<TestAuthHandler>();

            // Wrap the default scheme provider so the cookie scheme maps to TestAuthHandler
            services.RemoveAll<IAuthenticationSchemeProvider>();
            services.AddSingleton<IAuthenticationSchemeProvider>(sp =>
            {
                IOptions<AuthenticationOptions> authOptions = sp.GetRequiredService<IOptions<AuthenticationOptions>>();

                return new TestAuthSchemeProvider(authOptions);
            });

            // Apply per-test service overrides last so callers can both register new
            // services and replace any defaults wired above.
            AdditionalTestServices?.Invoke(services);
        });
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dbConnection.Close();
            _dbConnection.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ApplyMigrations()
    {
        // Same pattern as TestDatabaseFactory: run FluentMigrator on a temp file, then copy
        // the schema to the in-memory database. FluentMigrator manages its own connections,
        // so we cannot point it directly at an in-memory SQLite connection.
        string tempFile = Path.Combine(Path.GetTempPath(), $"functional_{Guid.NewGuid():N}.db");

        ServiceCollection migrationServices = new();
        migrationServices.AddFluentMigratorCore()
            .ConfigureRunner(r =>
            {
                r.AddSQLite()
                 .WithGlobalConnectionString($"Data Source={tempFile}")
                 .ScanIn(typeof(MigrationVersion).Assembly);
            });

        ServiceProvider migrationProvider = migrationServices.BuildServiceProvider();
        using (IServiceScope scope = migrationProvider.CreateScope())
        {
            SQLiteGenerator generator = scope.ServiceProvider.GetRequiredService<SQLiteGenerator>();
            generator.CompatibilityMode = CompatibilityMode.LOOSE;
            PatchSqliteTypeMap(generator);

            IMigrationRunner runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateUp();
        }

        migrationProvider.Dispose();

        // Copy schema from temp file to in-memory database
        using (SqliteConnection tempConn = new($"Data Source={tempFile};Mode=ReadOnly"))
        {
            tempConn.Open();
            using SqliteCommand cmd = tempConn.CreateCommand();
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY rowid";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string sql = reader.GetString(0);
                using SqliteCommand createCmd = _dbConnection.CreateCommand();
                createCmd.CommandText = sql;
                try
                {
                    createCmd.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    // Some statements may conflict (e.g., VersionInfo table), skip them
                }
            }
        }

        try { File.Delete(tempFile); } catch { /* best effort */ }
    }

    /// <summary>
    /// Seeds the TierFeatureLimits table with the default tier values.
    /// The migration runner copies only DDL (CREATE TABLE) statements from the temp
    /// database, so the INSERT seed data from <see cref="InitialMigration"/>
    /// must be applied manually.
    /// </summary>
    private void SeedTierFeatureLimits()
    {
        string now = DateTimeOffset.UtcNow.ToString("o");

        // Free tier: MachineLimit=3, RetentionDays=1, AlertRuleLimit=0, WebhookLimit=0
        ExecuteSql($@"INSERT INTO TierFeatureLimits (Tier, MachineLimit, RetentionDays, AlertRuleLimit, WebhookLimit, UpdatedAt)
            VALUES ({(int)SubscriptionTier.Free}, 3, 1, 0, 0, '{now}')");

        // Pro tier: MachineLimit=1000, RetentionDays=60, AlertRuleLimit=10, WebhookLimit=5
        ExecuteSql($@"INSERT INTO TierFeatureLimits (Tier, MachineLimit, RetentionDays, AlertRuleLimit, WebhookLimit, UpdatedAt)
            VALUES ({(int)SubscriptionTier.Pro}, 1000, 60, 10, 5, '{now}')");

        // Team tier: MachineLimit=10000, RetentionDays=365, AlertRuleLimit=25, WebhookLimit=15
        ExecuteSql($@"INSERT INTO TierFeatureLimits (Tier, MachineLimit, RetentionDays, AlertRuleLimit, WebhookLimit, UpdatedAt)
            VALUES ({(int)SubscriptionTier.Team}, 10000, 365, 25, 15, '{now}')");
    }

    /// <summary>
    /// Seeds the system user (Id=1) into the in-memory UserAccounts table. The schema-copy
    /// step in <see cref="ApplyMigrations"/> only carries DDL — the system-user row that
    /// <see cref="InitialMigration"/> inserts is not preserved. Test data builders default
    /// CreatedByUserId/AssignedByUserId to 1, so this row must exist before any seed is
    /// attempted with foreign keys enforced.
    /// Mirrors the column list and values from InitialMigration.cs (UserAccounts insert block).
    /// </summary>
    private void SeedSystemUser()
    {
        string now = DateTimeOffset.UtcNow.ToString("o");
        ExecuteSql(
            $@"INSERT INTO UserAccounts
                (Id, ExternalId, Username, CreatedAt, CreatedByUserId, IsActive, IsSystem, IsGlobalAdmin, AuthProvider)
                VALUES (1, 'system', 'system', '{now}', 1, 1, 1, 1, 0)");
    }

    private void ExecuteSql(string sql)
    {
        using SqliteCommand cmd = _dbConnection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Patches the SQLiteTypeMap on the generator to support DateTimeOffset.
    /// FluentMigrator's SQLite type map does not include DateTimeOffset by default.
    /// </summary>
    private static void PatchSqliteTypeMap(SQLiteGenerator generator)
    {
        object column = GetFieldValue(generator, "_column")
            ?? throw new InvalidOperationException("Could not find _column field on SQLiteGenerator hierarchy");

        object typeMap = GetFieldValue(column, "_typeMap")
            ?? throw new InvalidOperationException("Could not find _typeMap field on column hierarchy");

        MethodInfo? setMethod = null;
        Type? setType = typeMap.GetType();
        while (setType is not null && setMethod is null)
        {
            setMethod = setType.GetMethod("SetTypeMap", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(DbType), typeof(string)]);
            setType = setType.BaseType;
        }

        if (setMethod is null)
        {
            throw new InvalidOperationException("Could not find SetTypeMap(DbType, string) method on type map hierarchy");
        }

        setMethod.Invoke(typeMap, [DbType.DateTimeOffset, "TEXT"]);
    }

    private static object? GetFieldValue(object target, string fieldName)
    {
        Type? type = target.GetType();
        while (type is not null)
        {
            FieldInfo? field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                return field.GetValue(target);
            }

            type = type.BaseType;
        }

        return null;
    }

    /// <summary>
    /// Creates an NSubstitute mock for <see cref="IBillingApiClient"/> with default
    /// return values that match the no-op behavior (all operations succeed, no pending cancellation).
    /// </summary>
    private static IBillingApiClient CreateDefaultBillingApiClientMock()
    {
        IBillingApiClient mock = Substitute.For<IBillingApiClient>();

        mock.UpdateQuantityAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        mock.ReportMachineUsageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        mock.CancelSubscriptionAsync(Arg.Any<string>(), Arg.Any<PendingActionType>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        mock.GetSubscriptionStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StripeSubscriptionStatus(false, "none", string.Empty, 0, null, BillingTier.Unspecified)));
        mock.SwapSubscriptionPriceAsync(Arg.Any<string>(), Arg.Any<BillingTier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        mock.ResumeSubscriptionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        mock.GetUpcomingInvoiceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UpcomingInvoiceResult?>(null));
        mock.ListInvoicesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<List<InvoiceResult>>([]));

        return mock;
    }

    private static void RemoveHostedService<T>(IServiceCollection services) where T : class
    {
        ServiceDescriptor? descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(T));

        if (descriptor is not null)
        {
            services.Remove(descriptor);
        }
    }
}
