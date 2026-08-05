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
    /// <remarks>
    /// Only recognises a single-literal call: <c>IfDatabase("Postgres")</c>. A future
    /// multi-argument call such as <c>IfDatabase("Postgres", "SQLite")</c>, or one passing a
    /// constant instead of a string literal, does not match this pattern and is silently not
    /// scanned at all rather than being flagged as unrecognised. <see cref="IfDatabaseCallSitePattern"/>
    /// and <see cref="IfDatabaseCallSites_AreAllMatchedByLiteralPattern"/> guard against that:
    /// they count every occurrence of the bare <c>IfDatabase(</c> call token and fail if that
    /// count disagrees with how many this pattern matched, so an unscannable call form fails
    /// loudly instead of being skipped.
    /// </remarks>
    private static readonly Regex IfDatabaseLiteralPattern = new(
        "IfDatabase\\(\\s*\"([^\"]*)\"\\s*\\)",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches every <c>IfDatabase(</c> call token, regardless of its arguments. Used only to
    /// count call sites so <see cref="IfDatabaseCallSites_AreAllMatchedByLiteralPattern"/> can
    /// detect a call shape <see cref="IfDatabaseLiteralPattern"/> would otherwise silently skip.
    /// </summary>
    private static readonly Regex IfDatabaseCallSitePattern = new(
        "IfDatabase\\(",
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
    public async Task ProductionMigrations_ContainInitialMigrationAndInitialMigration2()
    {
        // This is the set EveryMigration_ProducesAtLeastOneExpression_OnPostgres actually
        // iterates (Export-tagged migrations excluded). Asserting names against the unfiltered
        // GetMigrationTypes() would not catch it: if InitialMigration or InitialMigration2 were
        // ever mistakenly tagged "Export", the reflection guard's loop body would stop running
        // for it, silentMigrations would stay empty, and an assertion against the unfiltered
        // list would still pass — the exact silent-pass failure this whole guard exists to
        // prevent, one level down. The assertion has to be against the set the guard iterates.
        List<string> productionMigrationNames = [.. GetProductionMigrationTypes().Select(t => t.Name)];

        await Assert.That(productionMigrationNames).Contains(nameof(InitialMigration));
        await Assert.That(productionMigrationNames).Contains("InitialMigration2");
    }

    [Test]
    public async Task EmbeddedMigrationSources_CoverEveryMigrationType()
    {
        // Naming InitialMigration.cs and InitialMigration2.cs explicitly (below) only proves those
        // two specific files stay embedded. Once the first production release ships, those two
        // files freeze forever and every schema change becomes a NEW migration file — exactly the
        // kind of file the name-based assertions do not cover. A glob narrowing in
        // unit.database.csproj, or a migration placed outside src/database/Migrations/, would drop
        // that new file from the resource set with no test failure, and any mis-guarded statement
        // in it would be invisible to the reflection guard too. This asserts by construction that
        // the embedded set covers every migration type the reflection guard's discovery finds,
        // rather than a fixed list of today's file names, so a future migration is covered
        // automatically.
        //
        // Covers ALL migration types, including Export-tagged ones: the reflection guard
        // (EveryMigration_ProducesAtLeastOneExpression_OnPostgres) deliberately excludes those, but
        // the source-text scan (IfDatabaseLiterals_MatchDialectsThisRepoUses) deliberately includes
        // them, and it is the source scan this assertion protects.
        HashSet<string> embeddedFileNames = [.. GetEmbeddedMigrationResourceNames().Select(GetSourceFileNameWithoutExtension)];
        List<string> missingTypeNames = [.. GetMigrationTypes()
            .Select(t => t.Name)
            .Where(name => embeddedFileNames.Contains(name) == false)];

        await Assert.That(missingTypeNames).IsEmpty();
    }

    [Test]
    public async Task EmbeddedMigrationSources_ContainIfDatabaseLiterals()
    {
        // Without this, IfDatabaseLiterals_MatchDialectsThisRepoUses passes vacuously if the
        // embedded-resource wiring or the regex ever stops finding files or literals.
        //
        // The overall file/literal counts alone would still pass vacuously if a future glob
        // change embedded some files but silently dropped others with a large share of the
        // literals (e.g. keeping InitialMigration2.cs's 4 but dropping InitialMigration.cs's 51
        // — the total would stay > 0 while 93% of the literals went unscanned). Naming the two
        // production migration source files explicitly closes that gap.
        List<string> resourceNames = [.. GetEmbeddedMigrationResourceNames()];
        List<string> sourceTexts = GetEmbeddedMigrationSourceTexts(resourceNames);

        await Assert.That(sourceTexts).IsNotEmpty();
        await Assert.That(resourceNames).Contains("MigrationSource.InitialMigration.cs");
        await Assert.That(resourceNames).Contains("MigrationSource.InitialMigration2.cs");

        int totalLiterals = sourceTexts.Sum(text => IfDatabaseLiteralPattern.Matches(StripComments(text)).Count);

        await Assert.That(totalLiterals).IsGreaterThan(0);
    }

    [Test]
    public async Task IfDatabaseLiterals_MatchDialectsThisRepoUses()
    {
        HashSet<string> acceptedDialects = BuildAcceptedDialects();
        List<string> unrecognizedLiterals = [];

        foreach (string sourceText in GetEmbeddedMigrationSourceTexts(GetEmbeddedMigrationResourceNames()))
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

    [Test]
    public async Task IfDatabaseCallSites_AreAllMatchedByLiteralPattern()
    {
        // IfDatabaseLiteralPattern only recognises a single quoted-string argument. A call shape
        // it cannot parse (a multi-argument call, or one passing a constant) would otherwise be
        // silently excluded from every guard above rather than failing. Counting the bare
        // "IfDatabase(" token separately and comparing counts catches that: a mismatch means some
        // call site did not produce a literal match.
        List<string> resourceNames = [.. GetEmbeddedMigrationResourceNames()];
        List<string> sourceTexts = GetEmbeddedMigrationSourceTexts(resourceNames);
        List<string> unmatchedResourceNames = [];

        for (int index = 0; index < resourceNames.Count; index++)
        {
            string stripped = StripComments(sourceTexts[index]);
            int callSiteCount = IfDatabaseCallSitePattern.Matches(stripped).Count;
            int literalMatchCount = IfDatabaseLiteralPattern.Matches(stripped).Count;

            if (callSiteCount != literalMatchCount)
            {
                unmatchedResourceNames.Add(resourceNames[index]);
            }
        }

        await Assert.That(unmatchedResourceNames).IsEmpty();
    }

    /// <summary>
    /// <see cref="StripComments"/> is a hand-rolled scanner with no test coverage of its own in
    /// this repo, because nothing under <c>src/database/Migrations/</c> today has a comment that
    /// mentions <c>IfDatabase</c> — so the green runs above only prove it preserves real literals
    /// through verbatim and raw strings, never that it actually discards a commented-out one.
    /// vord-internal's migrations DO carry such a doc comment, and this helper is written to be
    /// reused there (Task 5), so the discard path is exercised directly here against synthetic
    /// source covering a <c>//</c> line comment, an XML doc (<c>///</c>) comment, and a
    /// <c>/* block */</c> comment — each mentioning a dialect string that must never surface as a
    /// literal once stripped.
    /// </summary>
    [Test]
    [Arguments("// IfDatabase(\"Postgres\") is a decoy in a line comment")]
    [Arguments("/// <remarks>See IfDatabase(\"Postgres\") for background.</remarks>")]
    [Arguments("/* IfDatabase(\"Postgres\") is a decoy in a block comment */")]
    public async Task StripComments_DiscardsCommentedOutIfDatabaseMentions(string commentOnlySource)
    {
        string stripped = StripComments(commentOnlySource);

        await Assert.That(IfDatabaseLiteralPattern.Matches(stripped)).IsEmpty();
    }

    [Test]
    public async Task StripComments_PreservesRealCallsAndStringContent()
    {
        // A real call sits alongside a decoy in a line comment, plus a verbatim string and a
        // triple-quoted raw string that both contain quoted SQL identifiers (the same shape as
        // this repo's migrations) — none of that string content should be mistaken for a
        // comment or be altered by stripping. Built with an escaped (non-raw) string literal
        // rather than a C# raw string, since the synthetic source itself needs to contain a
        // triple-quote sequence and nesting that inside a matching raw-string delimiter would
        // only obscure what is being tested.
        string source = "// decoy: IfDatabase(\"Postgres\") should not appear below\n"
            + "IfDatabase(\"PostgreSQL\").Execute.Sql(@\"ALTER TABLE \"\"Machines\"\" ADD COLUMN \"\"X\"\" text;\");\n"
            + "IfDatabase(\"SQLite\").Execute.Sql(\"\"\"\n"
            + "    CREATE TABLE \"Machines\" (\"Id\" INTEGER PRIMARY KEY);\n"
            + "    \"\"\");\n";

        string stripped = StripComments(source);
        List<string> dialects = [.. IfDatabaseLiteralPattern.Matches(stripped).Select(m => m.Groups[1].Value)];

        await Assert.That(dialects).IsEquivalentTo(["PostgreSQL", "SQLite"]);
        await Assert.That(stripped).Contains("\"Machines\"");
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
    /// Lists the manifest resource names of every migration source file embedded by
    /// <c>test/unit/database/unit.database.csproj</c> (logical names prefixed
    /// <c>MigrationSource.</c>, one per file under <c>src/database/Migrations/</c>, including
    /// the <c>Export</c> subfolder). Embedding the sources (rather than reading them off disk by
    /// relative path) means the scan does not depend on the current working directory and
    /// survives being run from any directory or CI runner.
    /// </summary>
    private static List<string> GetEmbeddedMigrationResourceNames()
    {
        Assembly assembly = typeof(MigrationDialectGuardTests).Assembly;

        return [.. assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("MigrationSource.", StringComparison.Ordinal))];
    }

    /// <summary>
    /// Recovers the source file name (without directory or extension) a manifest resource was
    /// embedded from, so it can be compared against a migration type's <see cref="Type.Name"/>.
    /// LogicalName carries <c>%(RecursiveDir)</c>, so a file under the <c>Export</c> subfolder
    /// embeds as <c>MigrationSource.Export/ExportInitialMigration.cs</c> — the directory segment
    /// is separated by <c>/</c>, not <c>.</c>, so a simple prefix/suffix trim (rather than a
    /// dotted-suffix match) is required to isolate the file name correctly for both top-level and
    /// subfolder resources.
    /// </summary>
    private static string GetSourceFileNameWithoutExtension(string resourceName)
    {
        const string prefix = "MigrationSource.";
        const string extension = ".cs";

        string withoutPrefix = resourceName.StartsWith(prefix, StringComparison.Ordinal)
            ? resourceName[prefix.Length..]
            : resourceName;
        string withoutExtension = withoutPrefix.EndsWith(extension, StringComparison.Ordinal)
            ? withoutPrefix[..^extension.Length]
            : withoutPrefix;
        int lastSeparator = withoutExtension.LastIndexOf('/');

        return (lastSeparator == -1) ? withoutExtension : withoutExtension[(lastSeparator + 1)..];
    }

    private static List<string> GetEmbeddedMigrationSourceTexts(IEnumerable<string> resourceNames)
    {
        Assembly assembly = typeof(MigrationDialectGuardTests).Assembly;
        List<string> texts = [];

        foreach (string resourceName in resourceNames)
        {
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            using StreamReader reader = new(stream!);

            texts.Add(reader.ReadToEnd());
        }

        return texts;
    }

    /// <summary>
    /// Strips C# comments (<c>//</c> line comments, XML doc <c>///</c> comments — which this
    /// scanner treats the same as a plain line comment — and <c>/* */</c> block comments) from
    /// migration source while leaving string content untouched, so a doc comment that merely
    /// mentions <c>IfDatabase("Postgres")</c> in prose is not mistaken for a real literal.
    /// Recognises plain <c>"..."</c> strings, verbatim <c>@"..."</c> strings, and triple-quoted
    /// raw strings (<c>"""..."""</c>), all of which appear in this repo's migrations. Covered
    /// directly by <see cref="StripComments_DiscardsCommentedOutIfDatabaseMentions"/> and
    /// <see cref="StripComments_PreservesRealCallsAndStringContent"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not a full C# lexer. It does not recognise character literals
    /// (<c>'"'</c> would be misread as the start of a string) or interpolated strings with a
    /// quote inside an interpolation hole (<c>$"{Foo("x")}"</c>), and a raw-string SQL body that
    /// happens to contain the literal characters <c>IfDatabase("...")</c> would produce a false
    /// positive rather than being recognised as inert string content. None of these patterns
    /// exist in either this repo's or vord-internal's migrations today; if one is ever
    /// introduced, this scanner needs to grow to match it.
    /// </remarks>
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
