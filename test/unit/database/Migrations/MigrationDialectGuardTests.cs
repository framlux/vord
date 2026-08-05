// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;
using FluentMigrator.Infrastructure;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Processors;
using Framlux.FleetManagement.Database.Migrations;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Framlux.FleetManagement.Test.Migrations;

/// <summary>
/// Guards the invariant that no migration is silently a no-op on the production dialect.
/// </summary>
/// <remarks>
/// A migration whose statements are all guarded by a dialect string the processor does not
/// recognise runs nothing and still records itself as applied — it throws nothing, so no
/// schema test and no smoke test can see it. That is how a partitioning migration in
/// vord-internal shipped and did nothing for four months.
///
/// The trap is that <c>IfDatabase("Postgres")</c> looks right: it is the name of the base
/// <c>PostgresProcessor</c>. But <c>AddPostgres()</c> resolves to <c>Postgres15_0Processor</c>,
/// whose type is <c>PostgreSQL15_0</c> with aliases <c>PostgreSQL15_0</c> and <c>PostgreSQL</c>.
/// Only the latter two match.
///
/// This builds the same processor production builds, so it tracks whatever
/// <c>AddPostgres()</c> resolves to rather than hard-coding the alias list under test.
///
/// The reflection-based guard below only detects a migration where EVERY statement is
/// mis-guarded — a single mis-guarded statement among many still leaves other expressions in
/// the collection, so the guard stays green. <see cref="IfDatabaseLiterals_MatchDialectsThisRepoUses"/>
/// closes that gap with a source-text scan that checks every individual <c>IfDatabase("X")</c>
/// literal, not just whether a migration produced anything at all.
/// </remarks>
public class MigrationDialectGuardTests
{
    /// <summary>
    /// Matches an <c>IfDatabase("X")</c> call and captures the dialect literal <c>X</c>.
    /// Migration source only ever calls this with a plain quoted string, never a verbatim or
    /// raw string, so a simple quoted-string capture is sufficient.
    /// </summary>
    private static readonly Regex IfDatabaseLiteralPattern = new(
        "IfDatabase\\(\\s*\"([^\"]*)\"\\s*\\)",
        RegexOptions.Compiled);

    [Test]
    public async Task EveryMigration_ProducesAtLeastOneExpression_OnPostgres()
    {
        using ServiceProvider services = BuildPostgresServices();
        using IServiceScope scope = services.CreateScope();
        IMigrationProcessor processor = scope.ServiceProvider.GetRequiredService<IMigrationProcessor>();

        List<string> silentMigrations = [];

        // Migrations tagged "Export" never run against the production Postgres database — they
        // build portable SQLite export files — so they are out of scope for this guard. Their
        // IfDatabase literals are still covered by the source-text scan below.
        foreach (Type migrationType in GetProductionMigrationTypes())
        {
            IMigration migration = (IMigration)Activator.CreateInstance(migrationType)!;
            MigrationContext context = new(processor, scope.ServiceProvider, connection: null);

            migration.GetUpExpressions(context);

            if (context.Expressions.Count == 0)
            {
                silentMigrations.Add(migrationType.Name);
            }
        }

        await Assert.That(silentMigrations).IsEmpty();
    }

    [Test]
    public async Task MigrationAssembly_ContainsMigrations()
    {
        // IsNotEmpty alone would pass vacuously if discovery partially broke and only
        // ExportInitialMigration survived, silently dropping the real migrations from coverage.
        // Naming the two production migrations explicitly closes that gap.
        List<string> migrationNames = [.. GetMigrationTypes().Select(t => t.Name)];

        await Assert.That(migrationNames).IsNotEmpty();
        await Assert.That(migrationNames).Contains(nameof(InitialMigration));
        await Assert.That(migrationNames).Contains("InitialMigration2");
    }

    [Test]
    public async Task EmbeddedMigrationSources_ContainIfDatabaseLiterals()
    {
        // Without this, IfDatabaseLiterals_MatchDialectsThisRepoUses passes vacuously if the
        // embedded-resource wiring or the regex ever stops finding files or literals.
        List<string> sourceTexts = GetEmbeddedMigrationSourceTexts();

        await Assert.That(sourceTexts).IsNotEmpty();

        int totalLiterals = sourceTexts.Sum(text => IfDatabaseLiteralPattern.Matches(StripComments(text)).Count);

        await Assert.That(totalLiterals).IsGreaterThan(0);
    }

    [Test]
    public async Task IfDatabaseLiterals_MatchDialectsThisRepoUses()
    {
        HashSet<string> acceptedDialects = BuildAcceptedDialects();
        List<string> unrecognizedLiterals = [];

        foreach (string sourceText in GetEmbeddedMigrationSourceTexts())
        {
            string stripped = StripComments(sourceText);

            foreach (Match match in IfDatabaseLiteralPattern.Matches(stripped))
            {
                string dialect = match.Groups[1].Value;

                if (acceptedDialects.Contains(dialect) == false)
                {
                    unrecognizedLiterals.Add(dialect);
                }
            }
        }

        await Assert.That(unrecognizedLiterals).IsEmpty();
    }

    private static ServiceProvider BuildPostgresServices()
    {
        // Mirrors src/migrationRunner/Program.cs. Resolving the processor opens no connection,
        // and IfDatabase is evaluated from the processor's declared dialect alone, so the
        // connection string is never used.
        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString("Host=127.0.0.1;Database=unused")
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations());

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds the set of dialect strings <c>IfDatabase</c> actually recognises in this repo —
    /// the declared <see cref="ProcessorBase.DatabaseType"/> plus every
    /// <see cref="ProcessorBase.DatabaseTypeAliases"/> entry for both the Postgres and SQLite
    /// processors this repo's runner configures. Derived from the resolved processors rather
    /// than hard-coded, so it tracks whatever FluentMigrator ships next.
    /// </summary>
    private static HashSet<string> BuildAcceptedDialects()
    {
        HashSet<string> dialects = new(StringComparer.Ordinal);

        AddProcessorDialects(dialects, rb => rb.AddPostgres().WithGlobalConnectionString("Host=127.0.0.1;Database=unused"));
        AddProcessorDialects(dialects, rb => rb.AddSQLite().WithGlobalConnectionString("Data Source=:memory:"));

        return dialects;
    }

    private static void AddProcessorDialects(HashSet<string> dialects, Action<IMigrationRunnerBuilder> configure)
    {
        ServiceCollection services = new();
        services.AddFluentMigratorCore().ConfigureRunner(configure);

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        ProcessorBase processor = (ProcessorBase)scope.ServiceProvider.GetRequiredService<IMigrationProcessor>();

        dialects.Add(processor.DatabaseType);

        foreach (string alias in processor.DatabaseTypeAliases)
        {
            dialects.Add(alias);
        }
    }

    /// <summary>
    /// Reads every migration source file embedded as a resource by
    /// <c>test/unit/database/unit.database.csproj</c>. Embedding the sources (rather than
    /// reading them off disk by relative path) means the scan does not depend on the current
    /// working directory and survives being run from any directory or CI runner.
    /// </summary>
    private static List<string> GetEmbeddedMigrationSourceTexts()
    {
        Assembly assembly = typeof(MigrationDialectGuardTests).Assembly;
        List<string> texts = [];

        foreach (string resourceName in assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("MigrationSource.", StringComparison.Ordinal)))
        {
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            using StreamReader reader = new(stream!);

            texts.Add(reader.ReadToEnd());
        }

        return texts;
    }

    /// <summary>
    /// Strips C# comments (<c>//</c> line comments and <c>/* */</c> block comments) from
    /// migration source while leaving string content untouched, so a doc comment that merely
    /// mentions <c>IfDatabase("Postgres")</c> in prose is not mistaken for a real literal.
    /// Recognises plain <c>"..."</c> strings, verbatim <c>@"..."</c> strings, and triple-quoted
    /// raw strings (<c>"""..."""</c>), all of which appear in this repo's migrations.
    /// </summary>
    private static string StripComments(string source)
    {
        System.Text.StringBuilder result = new(source.Length);
        int index = 0;
        int length = source.Length;

        while (index < length)
        {
            if ((source[index] == '"') && (index + 2 < length) && (source[index + 1] == '"') && (source[index + 2] == '"'))
            {
                int start = index;
                int closing = source.IndexOf("\"\"\"", index + 3, StringComparison.Ordinal);
                int end = (closing == -1) ? length : (closing + 3);

                result.Append(source, start, end - start);
                index = end;

                continue;
            }

            if ((source[index] == '@') && (index + 1 < length) && (source[index + 1] == '"'))
            {
                int start = index;

                index += 2;

                while (index < length)
                {
                    if (source[index] != '"')
                    {
                        index++;

                        continue;
                    }

                    if ((index + 1 < length) && (source[index + 1] == '"'))
                    {
                        index += 2;

                        continue;
                    }

                    index++;

                    break;
                }

                result.Append(source, start, index - start);

                continue;
            }

            if (source[index] == '"')
            {
                int start = index;

                index++;

                while ((index < length) && (source[index] != '"'))
                {
                    index += ((source[index] == '\\') && (index + 1 < length)) ? 2 : 1;
                }

                if (index < length)
                {
                    index++;
                }

                result.Append(source, start, index - start);

                continue;
            }

            if ((source[index] == '/') && (index + 1 < length) && (source[index + 1] == '/'))
            {
                while ((index < length) && (source[index] != '\n'))
                {
                    index++;
                }

                continue;
            }

            if ((source[index] == '/') && (index + 1 < length) && (source[index + 1] == '*'))
            {
                int closing = source.IndexOf("*/", index + 2, StringComparison.Ordinal);

                index = (closing == -1) ? length : (closing + 2);

                continue;
            }

            result.Append(source[index]);
            index++;
        }

        return result.ToString();
    }

    private static bool IsExportTagged(Type migrationType)
    {
        return migrationType.GetCustomAttributes<TagsAttribute>()
            .SelectMany(tags => tags.TagNames)
            .Contains("Export");
    }

    private static List<Type> GetMigrationTypes()
    {
        return [.. typeof(InitialMigration).Assembly.GetTypes()
            .Where(t => typeof(IMigration).IsAssignableFrom(t)
                && t.IsAbstract == false
                && t.GetCustomAttribute<MigrationAttribute>() is not null)
            .OrderBy(t => t.GetCustomAttribute<MigrationAttribute>()!.Version)];
    }

    private static List<Type> GetProductionMigrationTypes()
    {
        return [.. GetMigrationTypes().Where(t => IsExportTagged(t) == false)];
    }
}
