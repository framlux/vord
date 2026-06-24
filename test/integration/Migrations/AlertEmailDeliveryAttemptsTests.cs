// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Framlux.FleetManagement.Test.Integration.Migrations;

/// <summary>
/// Live tests for the AlertEmailDeliveryAttempts idempotency table. The unique index on
/// (AlertEventId, Recipient) is what guarantees a single email per (event, recipient) across
/// Hangfire retries, so it is verified against a real Postgres built from the consolidated
/// migrations rather than an in-memory analogue. On Postgres AlertEvents is range-partitioned
/// and the table carries no foreign key to it (the application enforces the relationship), so
/// arbitrary AlertEventId values insert freely and the only thing that rejects a duplicate is the
/// unique index.
/// </summary>
public sealed class AlertEmailDeliveryAttemptsTests
{
    private static PostgresFixture _fixture = default!;

    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();
    }

    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task InsertDuplicate_EventRecipient_ViolatesUniqueIndex()
    {
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        await InsertAttemptAsync(connStr, alertEventId: 1, recipient: "a@example.com");

        await Assert.That(async () => await InsertAttemptAsync(connStr, alertEventId: 1, recipient: "a@example.com"))
            .Throws<PostgresException>();
    }

    private static ServiceProvider BuildMigrationServices(string connectionString)
    {
        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Information));

        return services.BuildServiceProvider();
    }

    private static string BuildIsolatedDatabaseConnectionString()
    {
        string baseConn = _fixture.ConnectionString;
        string dbName = $"it_{Guid.NewGuid():N}".ToLowerInvariant();
        NpgsqlConnectionStringBuilder template = new(baseConn);

        using NpgsqlConnection admin = new(baseConn);
        admin.Open();
        using (NpgsqlCommand cmd = admin.CreateCommand())
        {
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            cmd.ExecuteNonQuery();
        }
        admin.Close();

        template.Database = dbName;

        return template.ConnectionString;
    }

    private static async Task InsertAttemptAsync(string connStr, long alertEventId, string recipient)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO ""AlertEmailDeliveryAttempts""
            (""AlertEventId"", ""Recipient"", ""Status"", ""AttemptedAt"")
            VALUES (@alertEventId, @recipient, 0, @attemptedAt)";
        cmd.Parameters.AddWithValue("@alertEventId", alertEventId);
        cmd.Parameters.AddWithValue("@recipient", recipient);
        cmd.Parameters.AddWithValue("@attemptedAt", DateTimeOffset.UtcNow);
        await cmd.ExecuteNonQueryAsync();
    }
}
