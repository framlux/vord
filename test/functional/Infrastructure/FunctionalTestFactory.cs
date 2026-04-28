// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner.Generators.SQLite;
using FluentMigrator.Runner;
using FluentMigrator;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Alerts;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Commands;
using Framlux.FleetManagement.Server.Services.DataExport;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.Telemetry;
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

namespace Framlux.FleetManagement.FunctionalTest.Infrastructure;

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
        ApplyMigrations();
        DisableForeignKeys();
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

            // Disable rate limiting — replace the Redis-backed global limiter with a no-op.
            // The original limiter captures a disconnected Redis multiplexer; overriding
            // GlobalLimiter avoids connection errors during test requests.
            services.Configure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(options =>
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                    _ => RateLimitPartition.GetNoLimiter("test"));
            });

            // Remove hosted services that depend on Redis, distributed locking, or billing gRPC.
            // All background services must be removed to prevent concurrent SQLite access on the
            // shared in-memory connection, which causes intermittent "database is locked" errors.
            RemoveHostedService<AlertEvaluationService>(services);
            RemoveHostedService<CommandExpiryBackgroundService>(services);
            RemoveHostedService<DataExportBackgroundService>(services);
            RemoveHostedService<DataExportCleanupService>(services);
            RemoveHostedService<HealthSweepService>(services);
            RemoveHostedService<MachineStateStreamingService>(services);
            RemoveHostedService<PartitionManagementService>(services);
            RemoveHostedService<StripeSyncService>(services);

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

    private void DisableForeignKeys()
    {
        using SqliteCommand cmd = _dbConnection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = OFF";
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
