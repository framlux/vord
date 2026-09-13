// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Test.Integration.Services.Machines;

/// <summary>
/// Pins the two hand-written copies of the health rule to each other.
/// </summary>
/// <remarks>
/// <see cref="PostgresSqlDialect.HealthSweepForTenant"/> is the only copy that reaches production,
/// but the unit and functional suites run against <see cref="SqliteSqlDialect"/>. Drift in either
/// direction is invisible from inside one suite: a PostgreSQL change leaves the SQLite-backed
/// suites green while production is wrong, and a SQLite change leaves them testing a rule nobody
/// ships. Asserting the two against each other over one matrix is what closes that, and it has to
/// run here because this is the only project with a real PostgreSQL.
/// </remarks>
public sealed class HealthRuleDialectAgreementLiveTests
{
    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    /// <summary>
    /// Starts the Postgres database once and runs migrations so the schema is ready.
    /// </summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _migratedConnectionString = _fixture.ConnectionString;
        await RunMigrationsAsync(_migratedConnectionString);
    }

    /// <summary>
    /// Releases the Postgres database after all tests in the class.
    /// </summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
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

    [Test]
    [MethodDataSource(typeof(HealthRuleCases), nameof(HealthRuleCases.All))]
    public async Task HealthRule_ForTheSameInputs_PostgresAndSqliteWriteTheSameStatus(HealthRuleCase testCase)
    {
        short postgres = await HealthRuleEvaluation.EvaluateInPostgresAsync(_migratedConnectionString, testCase);
        short sqlite = await HealthRuleEvaluation.EvaluateInSqliteAsync(testCase);

        // Deliberately not asserted against the case's expected value — that is
        // HealthSweepThresholdLiveTests' job. This test only answers whether the two copies say
        // the same thing, so a drift shows up here even if both sides were changed together in a
        // way that also moved the expectation.
        await Assert.That(sqlite).IsEqualTo(postgres);
    }
}
