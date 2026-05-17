// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
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
                    EnableTransactionScopeEnlistment = false,
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
            options.ServerName = $"vord-worker-{Environment.MachineName}";
            options.ShutdownTimeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }

    /// <summary>
    /// Mounts the Hangfire dashboard at /admin/hangfire behind the Admin-only authorization filter.
    /// Call this only in the server process and only after authentication middleware is configured.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseHangfireAdminDashboard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        DashboardOptions options = new()
        {
            Authorization = new[] { new HangfireDashboardAuthorizationFilter() },
            DashboardTitle = "Vord Background Jobs",
            DisplayStorageConnectionString = false,
            IgnoreAntiforgeryToken = false,
        };

        app.UseHangfireDashboard("/admin/hangfire", options);

        return app;
    }
}
