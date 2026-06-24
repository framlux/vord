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
/// Live tests for the composite identity constraint on the Users table. Identity is keyed on the
/// pair (AuthProvider, ExternalId), not the external id alone: the same subject value under two
/// different providers must both persist, while a duplicate (provider, externalId) pair must be
/// rejected by the unique index. These run against a real Postgres built from the consolidated
/// migrations so the constraint is verified against the shipped schema, not an in-memory analogue.
/// </summary>
public sealed class CompositeExternalIdentityConstraintLiveTests
{
    // Provider discriminator values mirror Database.Enums.AuthProviderType: GitHub = 1, Google = 2.
    private const short GitHubProvider = 1;
    private const short GoogleProvider = 2;

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
    public async Task SameSubjectAcrossTwoProviders_BothPersist()
    {
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        int gitHubId = await InsertUserAsync(connStr, GitHubProvider, "shared-sub");
        int googleId = await InsertUserAsync(connStr, GoogleProvider, "shared-sub");

        await Assert.That(gitHubId).IsNotEqualTo(0);
        await Assert.That(googleId).IsNotEqualTo(0);
        await Assert.That(googleId).IsNotEqualTo(gitHubId);
    }

    [Test]
    public async Task DuplicateProviderAndExternalIdPair_IsRejected()
    {
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        await InsertUserAsync(connStr, GoogleProvider, "dup-sub");

        await Assert.That(async () => await InsertUserAsync(connStr, GoogleProvider, "dup-sub"))
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

    private static async Task<int> InsertUserAsync(string connStr, short authProvider, string externalId)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO ""UserAccounts""
            (""ExternalId"", ""Username"", ""CreatedAt"", ""CreatedByUserId"", ""IsActive"", ""IsSystem"", ""IsGlobalAdmin"", ""AuthProvider"")
            VALUES (@externalId, @username, @createdAt, @createdBy, true, false, false, @authProvider)
            RETURNING ""Id""";
        cmd.Parameters.AddWithValue("@externalId", externalId);
        cmd.Parameters.AddWithValue("@username", $"{externalId}-{authProvider}@example.com");
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow);
        // The seeded system user (Id 1) satisfies the CreatedByUserId foreign key.
        cmd.Parameters.AddWithValue("@createdBy", 1);
        cmd.Parameters.AddWithValue("@authProvider", authProvider);
        object? result = await cmd.ExecuteScalarAsync();

        return Convert.ToInt32(result);
    }
}
