// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// DI extensions that register Hangfire client services, the Hangfire processing server,
/// and the Hangfire dashboard.
/// </summary>
public static class HangfireRegistration
{
    /// <summary>
    /// Registers Hangfire client services (storage, IBackgroundJobClient, IRecurringJobManager).
    /// Call this in every process that enqueues jobs OR mounts the dashboard.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="postgresConnectionString">The Postgres connection string for Hangfire storage.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHangfireClient(this IServiceCollection services, string postgresConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(postgresConnectionString);

        services.AddHangfire((sp, config) =>
        {
            HangfireOptions options = sp.GetRequiredService<IOptions<HangfireOptions>>().Value;
            config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UsePostgreSqlStorage(opts =>
                {
                    opts.UseNpgsqlConnection(postgresConnectionString);
                },
                new PostgreSqlStorageOptions
                {
                    SchemaName = options.SchemaName,
                    // Hangfire.PostgreSql 1.20.12 does not expose a public InstallSchema entry point,
                    // so we let the storage prepare its own tables on first connection. The schema
                    // itself ("hangfire") is created by HangfireSchemaMigration before this runs, so
                    // Hangfire's per-table DDL operates inside the already-existing schema.
                    PrepareSchemaIfNecessary = true,
                    // Required by Hangfire.PostgreSql 1.20.12 — the storage validates this in its
                    // connection-string setup and throws ArgumentException if set to false. The
                    // setting controls whether Hangfire participates in ambient System.Transactions
                    // scopes; we don't use TransactionScope, but we still must set this to true.
                    EnableTransactionScopeEnlistment = true,                    // Long-running jobs (partition management, data export processing) hold
                    // [DisableConcurrentExecution] for up to 1800 seconds. The default
                    // InvisibilityTimeout is 30 minutes, which equals the lock — so the storage
                    // watchdog can re-queue a still-running job. Defaults to 2 hours; tunable
                    // via HangfireOptions.InvisibilityTimeoutMinutes (M12).
                    InvisibilityTimeout = TimeSpan.FromMinutes(options.InvisibilityTimeoutMinutes),
                });
        });

        return services;
    }

    /// <summary>
    /// Registers the Hangfire processing server. Call this only in the services.worker process.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHangfireServerForWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHangfireServer((sp, options) =>
        {
            HangfireOptions hangfireOptions = sp.GetRequiredService<IOptions<HangfireOptions>>().Value;
            options.WorkerCount = hangfireOptions.WorkerCount;
            options.ServerName = BuildServerName();
            options.ShutdownTimeout = TimeSpan.FromSeconds(30);
            // Queue priority: critical first (per-minute jobs that must not be starved),
            // then default (admin/UI-initiated work), then long (multi-minute jobs that
            // would otherwise hog all workers and delay critical work).
            options.Queues = new[] { "critical", "default", "long" };
        });

        return services;
    }

    /// <summary>
    /// Builds the Hangfire server-name string used to identify this worker in storage.
    /// Combines the host name (unique-per-pod in Kubernetes) with the process id so that
    /// multiple replicas sharing a host (docker-compose, local dev) cannot collide on the
    /// same Hangfire server registration.
    /// </summary>
    /// <returns>A stable per-process server name.</returns>
    internal static string BuildServerName()
    {
        return $"vord-worker-{Environment.MachineName}-{Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Mounts the Hangfire dashboard at /admin/hangfire behind the Admin-only authorization filter,
    /// IF <see cref="HangfireOptions.DashboardEnabled"/> is <c>true</c>. Call this only in the server
    /// process and only after authentication middleware is configured. When the option is disabled
    /// the dashboard route is NOT registered — requests to /admin/hangfire/* will return 404.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseHangfireAdminDashboard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        HangfireOptions hangfireOptions = app.ApplicationServices
            .GetRequiredService<IOptions<HangfireOptions>>().Value;
        ILogger logger = app.ApplicationServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(HangfireRegistration).FullName!);

        if (hangfireOptions.DashboardEnabled == false)
        {
            logger.LogInformation("Hangfire dashboard disabled by configuration (Hangfire:DashboardEnabled=false)");

            return app;
        }

        DashboardOptions options = new()
        {
            Authorization = new IDashboardAuthorizationFilter[] { new HangfireDashboardAuthorizationFilter() },
            DashboardTitle = "Vord Background Jobs",
            DisplayStorageConnectionString = false,
            IgnoreAntiforgeryToken = false,
        };

        app.UseHangfireDashboard("/admin/hangfire", options);
        logger.LogInformation("Hangfire dashboard mounted at /admin/hangfire");

        return app;
    }
}
