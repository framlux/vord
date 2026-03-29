// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.MigrationRunner.Options;
using Framlux.FleetManagement.MigrationRunner.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection("Database"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

DatabaseOptions dbOpts = builder.Configuration.GetSection("Database").Get<DatabaseOptions>()
    ?? throw new InvalidOperationException("Database configuration section is missing.");

string connString = (new NpgsqlConnectionStringBuilder()
{
    ApplicationName = "Framlux.FleetManagement.MigrationRunner",
    GssEncryptionMode = GssEncryptionMode.Disable,
    Database = dbOpts.Db,
    Username = dbOpts.User,
    Password = dbOpts.Password,
    Host = dbOpts.Hostname,
}).ConnectionString;

if (string.IsNullOrWhiteSpace(connString))
{
    // Connection string missing; readiness health check will remain unhealthy.
    Console.WriteLine("WARNING: Database connection string not found for migration runner. Health check will remain unhealthy.");
    throw new InvalidOperationException("Database connection string not found for migration runner.");
}

// FluentMigrator runner configuration
builder.Services
    .AddFluentMigratorCore()
    .ConfigureRunner(rb => rb
        .AddPostgres()
        .WithGlobalConnectionString(connString)
        .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
    .AddLogging(lb => lb.AddFluentMigratorConsole());

// Migration orchestration services
builder.Services.AddSingleton<MigrationState>();
builder.Services.AddSingleton<IDbMigrator, DbMigrator>();
builder.Services.AddHostedService<MigrationHostedService>();

// Add migration completion readiness health check.
// Default liveness "self" check already added by ServiceDefaults; we only add readiness gating here.
builder.Services.AddHealthChecks()
    .AddCheck<MigrationCompletionHealthCheck>("migrations", failureStatus: HealthStatus.Unhealthy, tags: new[] { "ready" });

WebApplication app = builder.Build();

app.MapHealthChecks("/healthz");
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();
