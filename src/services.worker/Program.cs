// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Extensions;
using Framlux.FleetManagement.Services.Core.Hangfire;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Security;
using Hangfire;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8080);
});

string environment = builder.Environment.EnvironmentName;
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Host.AddCoreSerilog();

// Bind shared configuration options
builder.Services.AddCoreOptions(builder.Configuration);

// Read typed config for use during service registration (before DI container is built)
DatabaseOptions dbOpts = builder.Configuration.GetSection("Database").Get<DatabaseOptions>()
    ?? throw new InvalidOperationException("Database configuration section is missing.");
RedisOptions redisOpts = builder.Configuration.GetSection("Redis").Get<RedisOptions>()
    ?? throw new InvalidOperationException("Redis configuration section is missing.");
BillingOptions billingOpts = builder.Configuration.GetSection("Billing").Get<BillingOptions>() ?? new();
ObjectStorageOptions objectStorageOpts = builder.Configuration.GetSection("ObjectStorage").Get<ObjectStorageOptions>() ?? new();

// Shared infrastructure: database, repositories, Redis, Polly, health checks
string connectionString = ServiceCollectionExtensions.BuildConnectionString(dbOpts, "Framlux.FleetManagement.ServicesWorker");
builder.Services.AddRepositories(dbOpts, "Framlux.FleetManagement.ServicesWorker");
builder.Services.AddCoreInfrastructure(redisOpts, connectionString);
// Intentionally shares the "Framlux.FleetManagement.Web" application name with the server process
// so the worker uses the same Data Protection key ring. The worker decrypts artifacts encrypted by
// the server (e.g., OIDC client secrets via OidcSecretProtector, custom alert webhook payloads via
// CustomPayloadFormatter) when running scheduled jobs, so the protector purposes must resolve to
// the same key material in both processes.
builder.Services.AddCoreDataProtection("Framlux.FleetManagement.Web");
builder.Services.AddCoreServices(billingOpts, objectStorageOpts);

// Background workers — the primary purpose of this process
builder.Services.AddBackgroundWorkers(billingOpts, objectStorageOpts);

// Gate worker startup on Hangfire schema readiness. migration-runner installs the
// hangfire schema and depends_on:service_healthy provides the ordering in compose/ArgoCD,
// but the poll is a cheap defense against pod-start ordering races on a cold cluster.
string hangfireConnString = Framlux.FleetManagement.Services.Core.Extensions.ServiceCollectionExtensions
    .BuildConnectionString(dbOpts, "Framlux.FleetManagement.Services.Worker.HangfireProbe");
await Framlux.FleetManagement.Services.Core.Hangfire.HangfireSchemaReadinessProbe
    .WaitForHangfireSchemaAsync(hangfireConnString, TimeSpan.FromSeconds(60));

// One-shot startup task — keep singleton; it has no per-request state.
builder.Services.AddSingleton<LegacyRedisKeyCleanup>();

// One-shot legacy OIDC secret migration job. Scoped because it depends on the scoped
// ITenantRepository.
builder.Services.AddScoped<EncryptLegacyTenantOidcSecretsJob>();

// Hangfire processing server — runs the recurring jobs registered by RecurringJobRegistry
builder.Services.AddHangfireServerForWorker();

WebApplication app = builder.Build();

// Register all recurring jobs with Hangfire. Hangfire.PostgreSql prepares its own schema on first
// connection (PrepareSchemaIfNecessary=true in HangfireRegistration); the "hangfire" schema itself
// is created earlier by HangfireSchemaMigration in the migrationRunner pipeline (ArgoCD sync-wave
// guarantees migration-runner becomes Healthy before this pod starts).
using (IServiceScope startupScope = app.Services.CreateScope())
{
    IRecurringJobManager recurringJobs = startupScope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    RecurringJobRegistry.RegisterAll(
        recurringJobs,
        billingEnabled: billingOpts.Enabled,
        objectStorageEnabled: string.IsNullOrEmpty(objectStorageOpts.BucketName) == false);
}

// One-time idempotent flush of Redis keys left behind by the pre-Hangfire background services
// and the former Redis-list delivery queue. Fire-and-forget with a 60-second outer timeout so
// worker readiness is NEVER blocked on Redis availability or scan duration. The cleanup is
// idempotent and sentinel-gated; partial failure is acceptable.
LegacyRedisKeyCleanup legacyCleanup = app.Services.GetRequiredService<LegacyRedisKeyCleanup>();
_ = Task.Run(async () =>
{
    using CancellationTokenSource cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
    cleanupCts.CancelAfter(TimeSpan.FromSeconds(60));
    try
    {
        await legacyCleanup.RunAsync(cleanupCts.Token);
    }
    catch (OperationCanceledException)
    {
        Log.Information("Legacy Redis key cleanup cancelled by timeout or shutdown");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Legacy Redis key cleanup failed; worker boot continues");
    }
});

// One-time encryption of legacy plaintext OIDC client secrets. Idempotent (rows already
// carrying the protected-marker prefix are skipped). Fire-and-forget with a 60s timeout so
// worker readiness is not blocked.
_ = Task.Run(async () =>
{
    using CancellationTokenSource oidcCts = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
    oidcCts.CancelAfter(TimeSpan.FromSeconds(60));
    try
    {
        using IServiceScope oidcScope = app.Services.CreateScope();
        EncryptLegacyTenantOidcSecretsJob oidcMigration =
            oidcScope.ServiceProvider.GetRequiredService<EncryptLegacyTenantOidcSecretsJob>();
        await oidcMigration.RunAsync(oidcCts.Token);
    }
    catch (OperationCanceledException)
    {
        Log.Information("Legacy OIDC secret migration cancelled by timeout or shutdown");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Legacy OIDC secret migration failed; worker boot continues");
    }
});

// Health check endpoint for Kubernetes probes
app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    Predicate = _ => false, // Liveness: no dependency checks
    ResponseWriter = HealthCheckResponseWriter.WriteMinimal
});
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    ResponseWriter = HealthCheckResponseWriter.WriteMinimal
});

// Serilog structured request logging — suppress health check noise
app.UseSerilogRequestLogging(options =>
{
    options.GetLevel = (httpContext, elapsed, ex) =>
    {
        if (httpContext.Request.Path.StartsWithSegments("/healthz") ||
            httpContext.Request.Path.StartsWithSegments("/readyz"))
        {
            return Serilog.Events.LogEventLevel.Verbose;
        }

        return Serilog.Events.LogEventLevel.Information;
    };
});

// Graceful shutdown: allow in-flight background work to complete during Kubernetes rolling updates.
IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    Log.Information("Services worker stopping — waiting for background tasks to complete");
});

await app.RunAsync();
