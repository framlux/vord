// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Extensions;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Options;
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
builder.Services.AddCoreServices(billingOpts, objectStorageOpts);

// Background workers — the primary purpose of this process
builder.Services.AddBackgroundWorkers(billingOpts, objectStorageOpts);

WebApplication app = builder.Build();

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
