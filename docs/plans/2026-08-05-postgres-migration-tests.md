# Postgres-Backed Migration Tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Execute the full migration set against a real PostgreSQL instance in CI for both repos, asserting schema shape rather than merely that nothing threw.

**Architecture:** Three independent mechanisms. A database-free reflection guard asserts no migration is silently a no-op on the production dialect. Testcontainers-backed live tests run the chain against real Postgres and assert the Postgres-only artifacts SQLite structurally cannot reach. CI wiring makes both gate merges and releases.

**Tech Stack:** .NET 10, TUnit, FluentMigrator 8.0.1 (vord) / 7.2.0 (vord-internal), Testcontainers.PostgreSql, Npgsql, `postgres:18-alpine`.

**Spec:** `vord/docs/specs/2026-08-05-postgres-migration-tests-design.md`

**Beads:** vord-rz6 (Task 1), vord-4gj (Task 2), vord-y3w (Tasks 3-4), vord-djv (Task 5), vord-tpg (Task 6). Parent: vord-7pe.

## Global Constraints

- Every new `.cs` file starts with the three-line Framlux copyright header used by every existing file in both repos (see any file under `test/`).
- No AI attribution in commit messages. No review IDs, "Fix N", phase or task numbers in code or comments.
- Never depend on wall-clock time in a test. Where a migration computes dates, read them from the database's own `now()`.
- Tests must fail loudly, never skip, when a container runtime is absent.
- `dotnet run --no-build` can replay stale TUnit results. When verifying a change, build with `--no-incremental` or run the compiled executable directly if results look suspicious.
- vord test namespaces: `Framlux.FleetManagement.Test.*`. vord-internal: `Framlux.Billing.Api.Tests.*`.

## Verified API facts

These were confirmed empirically against both resolved package versions before this plan was written. Do not re-derive them.

- `FluentMigrator.Infrastructure.MigrationContext` is public, in the `FluentMigrator` assembly, with a single constructor: `MigrationContext(IQuerySchema querySchema, IServiceProvider serviceProvider, string connection)`. Identical in 7.2.0 and 8.0.1.
- `IMigrationContext` exposes `Expressions` (`ICollection<IMigrationExpression>`), `QuerySchema`, `ServiceProvider`, `Connection`.
- `FluentMigrator.IMigrationProcessor` implements `IQuerySchema`, so the DI-resolved processor can be passed straight into `MigrationContext`.
- `AddPostgres()` resolves to `Postgres15_0Processor`, whose `DatabaseType` is `PostgreSQL15_0` and whose `DatabaseTypeAliases` are `["PostgreSQL15_0", "PostgreSQL"]`. `"Postgres"` is not among them. (`PostgresProcessor`, the base class, does report `"Postgres"` — which is why the string looks plausible.)
- Resolving the processor does **not** open a connection. A bogus connection string is fine.
- Spiked against vord's real migrations: `ExportInitialMigration` → 11 expressions, `InitialMigration` → 188, `InitialMigration2` → 2. A probe migration using `IfDatabase("Postgres")` → **0 expressions**; using `IfDatabase("PostgreSQL")` → 1. The guard catches the real defect.

---

## File Structure

**vord**
- Create: `test/unit/database/Migrations/MigrationDialectGuardTests.cs` — the no-op guard.
- Modify: `test/unit/database/unit.database.csproj` — add `FluentMigrator.Runner.Postgres`.
- Modify: `test/integration/Migrations/MigrationRunnerLiveTests.cs` — add Postgres-only shape assertions and their helpers.

**vord-internal**
- Create: `test/billing.integration/billing.integration.csproj`
- Create: `test/billing.integration/AssemblyAttributes.cs`
- Create: `test/billing.integration/Infrastructure/PostgresFixture.cs`
- Create: `test/billing.integration/Infrastructure/MigrationTestDatabase.cs` — per-test database creation and the schema-introspection helpers.
- Create: `test/billing.integration/Migrations/MigrationRunnerLiveTests.cs`
- Create: `test/billing.integration/Migrations/MigrationDialectGuardTests.cs`
- Modify: `vord-internal.slnx`, `.github/workflows/ci.yaml`, `.github/workflows/prod.yaml`

Introspection helpers live in `MigrationTestDatabase` rather than on the test class so both test classes share them without inheritance.

---

## Task 1: vord no-op dialect guard

**Bead:** vord-rz6. Independent — no other task depends on it.

**Files:**
- Create: `vord/test/unit/database/Migrations/MigrationDialectGuardTests.cs`
- Modify: `vord/test/unit/database/unit.database.csproj`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: the guard pattern Task 5 reproduces for vord-internal. No shared code across repos.

- [ ] **Step 1: Add the Postgres runner package**

In `vord/test/unit/database/unit.database.csproj`, add to the existing `PackageReference` group, keeping alphabetical order next to the SQLite entry:

```xml
    <PackageReference Include="FluentMigrator.Runner.Postgres" Version="8.0.1" />
```

- [ ] **Step 2: Write the failing test**

Create `vord/test/unit/database/Migrations/MigrationDialectGuardTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;
using FluentMigrator.Infrastructure;
using FluentMigrator.Runner;
using Framlux.FleetManagement.Database.Migrations;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

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
/// </remarks>
public class MigrationDialectGuardTests
{
    [Test]
    public async Task EveryMigration_ProducesAtLeastOneExpression_OnPostgres()
    {
        using ServiceProvider services = BuildPostgresServices();
        using IServiceScope scope = services.CreateScope();
        IMigrationProcessor processor = scope.ServiceProvider.GetRequiredService<IMigrationProcessor>();

        List<string> silentMigrations = [];

        foreach (Type migrationType in GetMigrationTypes())
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
        // Without this, the guard above passes vacuously if migration discovery ever breaks.
        await Assert.That(GetMigrationTypes()).IsNotEmpty();
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

    private static List<Type> GetMigrationTypes()
    {
        return [.. typeof(InitialMigration).Assembly.GetTypes()
            .Where(t => typeof(IMigration).IsAssignableFrom(t)
                && t.IsAbstract == false
                && t.GetCustomAttribute<MigrationAttribute>() is not null)
            .OrderBy(t => t.GetCustomAttribute<MigrationAttribute>()!.Version)];
    }
}
```

- [ ] **Step 3: Run and confirm it passes on the current tree**

```bash
cd vord && dotnet build test/unit/database/unit.database.csproj -c Release --no-incremental
dotnet run --project test/unit/database/unit.database.csproj -c Release --no-build --treenode-filter "*MigrationDialectGuardTests*"
```

Expected: 2 passed. All three vord migrations emit expressions on Postgres today.

- [ ] **Step 4: Prove the guard actually fails on the defect**

This is the important step — a guard that cannot fail is worthless. Temporarily change the first `IfDatabase("PostgreSQL")` in `src/database/Migrations/InitialMigration2.cs:35` to `IfDatabase("Postgres")`, rebuild, and re-run the filter above.

Expected: `EveryMigration_ProducesAtLeastOneExpression_OnPostgres` FAILS, reporting `InitialMigration2` in the collection. (`InitialMigration2` emits exactly 2 expressions on Postgres, both of them the deferrable FK statements, so breaking one is not enough — break both if the first alone does not empty the collection.)

**Revert the edit** and re-run to confirm green before continuing.

- [ ] **Step 5: Commit**

```bash
cd vord
git add test/unit/database/Migrations/MigrationDialectGuardTests.cs test/unit/database/unit.database.csproj
git commit -m "test: guard against migrations that silently no-op on Postgres

A migration guarded on an unrecognised dialect string runs nothing, records
itself as applied, and throws nothing for any test to catch."
```

---

## Task 2: vord Postgres-only shape assertions

**Bead:** vord-4gj. Independent of Task 1.

**Files:**
- Modify: `vord/test/integration/Migrations/MigrationRunnerLiveTests.cs`

**Interfaces:**
- Consumes: the file's existing private helpers — `BuildIsolatedDatabaseConnectionString()`, `BuildMigrationServices(string)`, `TableExistsAsync`, `ColumnExistsAsync`.
- Produces: new private helpers `GetRelkindAsync(string connStr, string relName)` → `char?`, `GetIndexDefAsync(string connStr, string indexName)` → `string?`, `ExtensionExistsAsync(string connStr, string extension)` → `bool`, `GetConstraintDefAsync(string connStr, string constraintName)` → `string?`, `GetPartitionNamesAsync(string connStr, string parent)` → `List<string>`, `ForeignKeyIsDeferredAsync(string connStr, string constraintName)` → `bool`.

- [ ] **Step 1: Write the failing partitioning test**

Add to the class, above the `// ----- helpers -----` marker:

```csharp
    [Test]
    public async Task PartitionedTables_ArePartitioned_AfterFullChain()
    {
        // Four tables are partitioned on Postgres and plain on SQLite, so the SQLite runs cannot
        // see this at all. A table that silently stays unpartitioned passes every existence and
        // column check while breaking the partition-maintenance services at runtime.
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        // relkind 'p' is a partitioned table; 'r' is an ordinary one.
        await Assert.That(await GetRelkindAsync(connStr, "MachineTelemetry")).IsEqualTo('p');
        await Assert.That(await GetRelkindAsync(connStr, "AuditLog")).IsEqualTo('p');
        await Assert.That(await GetRelkindAsync(connStr, "AlertEvents")).IsEqualTo('p');
        await Assert.That(await GetRelkindAsync(connStr, "RemoteCommands")).IsEqualTo('p');

        // MachineTelemetry is two-level: LIST("RetentionClass") then RANGE("ReceivedAt") per class,
        // so each retention-class partition must itself be partitioned.
        await Assert.That(await GetRelkindAsync(connStr, "MachineTelemetry_Short")).IsEqualTo('p');
        await Assert.That(await GetRelkindAsync(connStr, "MachineTelemetry_Medium")).IsEqualTo('p');
        await Assert.That(await GetRelkindAsync(connStr, "MachineTelemetry_Long")).IsEqualTo('p');

        // Each range parent carries a default partition so a row outside the current window is
        // never rejected outright.
        await Assert.That(await GetPartitionNamesAsync(connStr, "AuditLog"))
            .Contains("auditlog_default");
        await Assert.That(await GetPartitionNamesAsync(connStr, "AlertEvents"))
            .Contains("alertevents_default");
        await Assert.That(await GetPartitionNamesAsync(connStr, "RemoteCommands"))
            .Contains("remotecommands_default");
    }
```

- [ ] **Step 2: Add the helpers this test needs**

Append to the helpers region:

```csharp
    private static async Task<char?> GetRelkindAsync(string connStr, string relName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.relkind
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema() AND c.relname = @r";
        cmd.Parameters.AddWithValue("@r", relName);
        object? result = await cmd.ExecuteScalarAsync();

        return result is char c ? c : null;
    }

    private static async Task<List<string>> GetPartitionNamesAsync(string connStr, string parent)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT child.relname
            FROM pg_inherits i
            JOIN pg_class parent ON parent.oid = i.inhparent
            JOIN pg_class child ON child.oid = i.inhrelid
            JOIN pg_namespace n ON n.oid = parent.relnamespace
            WHERE n.nspname = current_schema() AND parent.relname = @p
            ORDER BY child.relname";
        cmd.Parameters.AddWithValue("@p", parent);
        List<string> names = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
```

- [ ] **Step 3: Run it**

```bash
cd vord && dotnet build test/integration/integration.csproj -c Release --no-incremental
dotnet run --project test/integration/integration.csproj -c Release --no-build --treenode-filter "*PartitionedTables_ArePartitioned_AfterFullChain*"
```

Expected: PASS. If a `_default` name assertion fails, read the actual names from the failure output — `pg_class.relname` is lower-cased only when the migration created the partition with an unquoted identifier. Correct the expected strings to what the migration actually produces rather than changing the migration.

- [ ] **Step 4: Write the index-definition test**

Existence is not enough: four of these indexes exist under the same name on both dialects with materially different definitions, so an existence check passes on a wrong one.

```csharp
    [Test]
    public async Task PostgresOnlyIndexes_HaveTheirIntendedDefinitions_AfterFullChain()
    {
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        // Case-insensitive uniqueness is LOWER("Name") on Postgres and COLLATE NOCASE on SQLite.
        string? tenantsName = await GetIndexDefAsync(connStr, "IX_Tenants_Name");
        await Assert.That(tenantsName).IsNotNull();
        await Assert.That(tenantsName!).Contains("UNIQUE");
        await Assert.That(tenantsName).Contains("lower");

        // The Postgres definition carries the partition key columns; the SQLite one is single-column.
        string? sourceEventId = await GetIndexDefAsync(connStr, "IX_MachineTelemetry_SourceEventId");
        await Assert.That(sourceEventId).IsNotNull();
        await Assert.That(sourceEventId!).Contains("UNIQUE");
        await Assert.That(sourceEventId).Contains("RetentionClass");
        await Assert.That(sourceEventId).Contains("ReceivedAt");
        await Assert.That(sourceEventId).Contains("WHERE");

        // Partial indexes: the predicate is the whole point, and a dropped WHERE clause changes
        // uniqueness semantics rather than just performance.
        string? activeRoles = await GetIndexDefAsync(connStr, "IX_UserTenantRoles_Active");
        await Assert.That(activeRoles).IsNotNull();
        await Assert.That(activeRoles!).Contains("UNIQUE");
        await Assert.That(activeRoles).Contains("WHERE");

        string? activeMachines = await GetIndexDefAsync(connStr, "IX_Machines_TenantId_Active");
        await Assert.That(activeMachines).IsNotNull();
        await Assert.That(activeMachines!).Contains("WHERE");

        foreach (string indexName in new[]
        {
            "IX_TenantDeletions_ActiveTenant",
            "IX_RemoteCommands_CommandId",
            "IX_IntegrationEndpoints_TenantId",
            "IX_IntegrationEndpoints_TenantId_Provider",
            "IX_MachineStateSummary_Hostname_Trgm",
            "IX_MachineStateSummary_Name_Trgm",
            "IX_MachineStateSummary_HardwareModel_Trgm",
        })
        {
            await Assert.That(await GetIndexDefAsync(connStr, indexName)).IsNotNull();
        }
    }

    [Test]
    public async Task PgTrgmExtension_Installed_AfterFullChain()
    {
        // The three trigram indexes depend on it and it has no SQLite counterpart, so nothing
        // else in the suite would notice if the CREATE EXTENSION stopped running.
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        await Assert.That(await ExtensionExistsAsync(connStr, "pg_trgm")).IsTrue();
    }
```

Helpers:

```csharp
    private static async Task<string?> GetIndexDefAsync(string connStr, string indexName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT indexdef FROM pg_indexes
            WHERE schemaname = current_schema() AND indexname = @i";
        cmd.Parameters.AddWithValue("@i", indexName);
        object? result = await cmd.ExecuteScalarAsync();

        return result is null or DBNull ? null : result.ToString();
    }

    private static async Task<bool> ExtensionExistsAsync(string connStr, string extension)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = @e)";
        cmd.Parameters.AddWithValue("@e", extension);
        object? result = await cmd.ExecuteScalarAsync();

        return result is bool b && b;
    }
```

- [ ] **Step 5: Run both index tests**

```bash
cd vord && dotnet build test/integration/integration.csproj -c Release --no-incremental
dotnet run --project test/integration/integration.csproj -c Release --no-build --treenode-filter "*MigrationRunnerLiveTests*"
```

Expected: all pass. `pg_indexes.indexdef` normalises the definition, so if a `Contains` assertion fails, print the actual `indexdef` and match against what Postgres reports — but only after confirming the migration really does produce the intended shape.

- [ ] **Step 6: Write the constraint and deferrable-FK test**

```csharp
    [Test]
    public async Task CheckConstraints_AndDeferrableForeignKeys_Exist_AfterFullChain()
    {
        string connStr = BuildIsolatedDatabaseConnectionString();
        await using ServiceProvider provider = BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        string? durationCheck = await GetConstraintDefAsync(connStr, "CK_AlertRules_DurationMinutes");
        await Assert.That(durationCheck).IsNotNull();
        await Assert.That(durationCheck!).Contains("CHECK");
        await Assert.That(durationCheck).Contains("DurationMinutes");

        // The UserAccounts self-references are raw SQL on Postgres purely so they can be
        // DEFERRABLE INITIALLY DEFERRED: the System row references itself, and without deferral a
        // --data-only restore requires disabling triggers. SQLite cannot express it, so losing the
        // deferral would be silent everywhere else.
        await Assert.That(await ForeignKeyIsDeferredAsync(connStr, "FK_Users_CreatedBy")).IsTrue();
        await Assert.That(await ForeignKeyIsDeferredAsync(connStr, "FK_Users_DeletedBy")).IsTrue();
    }
```

Helpers:

```csharp
    private static async Task<string?> GetConstraintDefAsync(string connStr, string constraintName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT pg_get_constraintdef(c.oid)
            FROM pg_constraint c
            JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE n.nspname = current_schema() AND c.conname = @c";
        cmd.Parameters.AddWithValue("@c", constraintName);
        object? result = await cmd.ExecuteScalarAsync();

        return result is null or DBNull ? null : result.ToString();
    }

    private static async Task<bool> ForeignKeyIsDeferredAsync(string connStr, string constraintName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.condeferrable AND c.condeferred
            FROM pg_constraint c
            JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE n.nspname = current_schema() AND c.conname = @c AND c.contype = 'f'";
        cmd.Parameters.AddWithValue("@c", constraintName);
        object? result = await cmd.ExecuteScalarAsync();

        return result is bool b && b;
    }
```

- [ ] **Step 7: Run the full live suite**

```bash
cd vord && dotnet build test/integration/integration.csproj -c Release --no-incremental
dotnet run --project test/integration/integration.csproj -c Release --no-build --treenode-filter "*MigrationRunnerLiveTests*"
```

Expected: all pass, including the seven pre-existing tests.

- [ ] **Step 8: Commit**

```bash
cd vord
git add test/integration/Migrations/MigrationRunnerLiveTests.cs
git commit -m "test: assert Postgres-only schema shape after the migration chain

Covers what SQLite cannot reach: partitioned tables, index definitions rather
than existence, pg_trgm, check constraints, and the deferrable UserAccounts FKs."
```

---

## Task 3: vord-internal integration test project and fixture

**Bead:** vord-y3w (first half). Tasks 4, 5 and 6 depend on this.

**Files:**
- Create: `vord-internal/test/billing.integration/billing.integration.csproj`
- Create: `vord-internal/test/billing.integration/AssemblyAttributes.cs`
- Create: `vord-internal/test/billing.integration/Infrastructure/PostgresFixture.cs`
- Create: `vord-internal/test/billing.integration/Infrastructure/MigrationTestDatabase.cs`
- Modify: `vord-internal/vord-internal.slnx`

**Interfaces:**
- Produces, in namespace `Framlux.Billing.Api.Tests.Integration.Infrastructure`:
  - `PostgresFixture` — `Task InitializeAsync()`, `ValueTask DisposeAsync()`, `string ConnectionString { get; }`
  - `static class MigrationTestDatabase` — `string CreateIsolated(string adminConnectionString)`, `ServiceProvider BuildMigrationServices(string connectionString)`, `Task<bool> TableExistsAsync(string, string)`, `Task<char?> GetRelkindAsync(string, string)`, `Task<List<string>> GetPartitionNamesAsync(string, string)`, `Task<int> CountIndexesAsync(string connStr, string indexName)`, `Task<long> CountVersionsAsync(string)`, `Task<string> CurrentMonthPartitionSuffixAsync(string)`

- [ ] **Step 1: Create the project file**

`vord-internal/test/billing.integration/billing.integration.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <SelfContained>true</SelfContained>
    <RootNamespace>Framlux.Billing.Api.Tests.Integration</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="TUnit" Version="1.6.28" />
    <PackageReference Include="Testcontainers.PostgreSql" Version="4.7.0" />
    <PackageReference Include="Npgsql" Version="10.0.1" />
    <PackageReference Include="FluentMigrator.Runner" Version="7.2.0" />
    <PackageReference Include="FluentMigrator.Runner.Postgres" Version="7.2.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../../src/billing-api/api.csproj" />
  </ItemGroup>

</Project>
```

TUnit 1.6.28 matches `test/billing`; FluentMigrator 7.2.0 and Npgsql 10.0.1 match `api.csproj` exactly, so the build does not fan out into a version-unification problem. `Microsoft.Extensions.DependencyInjection` and `.Logging` are deliberately not declared — they arrive transitively through the `api.csproj` reference, exactly as they do for `test/billing`.

- [ ] **Step 2: Register it in the solution**

`vord-internal/vord-internal.slnx` becomes:

```xml
<Solution>
  <Project Path="src/billing-api/api.csproj" />
  <Project Path="test/billing/billing.csproj" />
  <Project Path="test/billing.integration/billing.integration.csproj" />
</Solution>
```

- [ ] **Step 3: Add the assembly timeout attribute**

`vord-internal/test/billing.integration/AssemblyAttributes.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using TUnit.Core;

// Container start plus a full migration chain is slower than a unit test, but a run that
// exceeds this is hung rather than slow.
[assembly: Timeout(300_000)]
```

- [ ] **Step 4: Write the fixture**

`vord-internal/test/billing.integration/Infrastructure/PostgresFixture.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Testcontainers.PostgreSql;

namespace Framlux.Billing.Api.Tests.Integration.Infrastructure;

/// <summary>
/// Provides a per-class Postgres container for migration tests.
/// </summary>
/// <remarks>
/// There is deliberately no skip-when-Docker-absent path. A migration suite that silently
/// skips locally reintroduces exactly the blind spot it exists to close, so a missing
/// container runtime surfaces as a failure.
/// </remarks>
public sealed class PostgresFixture : IAsyncDisposable
{
    private PostgreSqlContainer? _container;

    /// <summary>
    /// The full container connection string including the password. Tests issue their own
    /// CREATE DATABASE, so the password must be present rather than stripped.
    /// </summary>
    public string ConnectionString { get; private set; } = default!;

    /// <summary>
    /// Starts the Postgres container.
    /// </summary>
    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:18-alpine")
            .Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
```

- [ ] **Step 5: Write the database helper**

`vord-internal/test/billing.integration/Infrastructure/MigrationTestDatabase.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.Billing.Api.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Framlux.Billing.Api.Tests.Integration.Infrastructure;

/// <summary>
/// Per-test database creation and schema introspection for the migration tests.
/// </summary>
public static class MigrationTestDatabase
{
    /// <summary>
    /// Creates a fresh database on the fixture's container and returns a connection string for
    /// it, so cases never observe each other's migration state.
    /// </summary>
    public static string CreateIsolated(string adminConnectionString)
    {
        string dbName = $"migtest_{Guid.NewGuid():N}"[..16].ToLowerInvariant();

        using NpgsqlConnection admin = new(adminConnectionString);
        admin.Open();
        using (NpgsqlCommand cmd = admin.CreateCommand())
        {
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            cmd.ExecuteNonQuery();
        }

        NpgsqlConnectionStringBuilder builder = new(adminConnectionString) { Database = dbName };

        return builder.ConnectionString;
    }

    /// <summary>
    /// Builds the migration runner exactly as src/billing-api/Program.cs does. The production
    /// processor resolution is part of what is under test — AddPostgres resolving to
    /// Postgres15_0Processor is why a "Postgres" dialect guard matched nothing — so a test that
    /// configured its own processor differently would not be testing the real thing.
    /// </summary>
    public static ServiceProvider BuildMigrationServices(string connectionString)
    {
        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
            .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

        return services.BuildServiceProvider();
    }

    /// <summary>Whether a table exists in the current schema.</summary>
    public static async Task<bool> TableExistsAsync(string connStr, string tableName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT EXISTS (
            SELECT 1 FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = @t)";
        cmd.Parameters.AddWithValue("@t", tableName);
        object? result = await cmd.ExecuteScalarAsync();

        return result is bool b && b;
    }

    /// <summary>The pg_class relkind of a relation: 'r' ordinary, 'p' partitioned.</summary>
    public static async Task<char?> GetRelkindAsync(string connStr, string relName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.relkind
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema() AND c.relname = @r";
        cmd.Parameters.AddWithValue("@r", relName);
        object? result = await cmd.ExecuteScalarAsync();

        return result is char c ? c : null;
    }

    /// <summary>Names of the partitions attached to a partitioned table.</summary>
    public static async Task<List<string>> GetPartitionNamesAsync(string connStr, string parent)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT child.relname
            FROM pg_inherits i
            JOIN pg_class parent ON parent.oid = i.inhparent
            JOIN pg_class child ON child.oid = i.inhrelid
            JOIN pg_namespace n ON n.oid = parent.relnamespace
            WHERE n.nspname = current_schema() AND parent.relname = @p
            ORDER BY child.relname";
        cmd.Parameters.AddWithValue("@p", parent);
        List<string> names = [];
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// How many indexes carry this name across the whole database. A rename-and-recreate that
    /// leaves the original behind shows up here as more than one.
    /// </summary>
    public static async Task<int> CountIndexesAsync(string connStr, string indexName)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*) FROM pg_indexes
            WHERE schemaname = current_schema() AND indexname = @i";
        cmd.Parameters.AddWithValue("@i", indexName);
        object? result = await cmd.ExecuteScalarAsync();

        return Convert.ToInt32(result);
    }

    /// <summary>Row count of the FluentMigrator VersionInfo table.</summary>
    public static async Task<long> CountVersionsAsync(string connStr)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*) FROM ""VersionInfo""";
        object? result = await cmd.ExecuteScalarAsync();

        return Convert.ToInt64(result);
    }

    /// <summary>
    /// The YYYY_MM suffix of the partition covering the current month, read from the database's
    /// own clock — the migration derives partition names from now() server-side, so the test host
    /// clock is the wrong source and would drift across a month boundary.
    /// </summary>
    public static async Task<string> CurrentMonthPartitionSuffixAsync(string connStr)
    {
        await using NpgsqlConnection conn = new(connStr);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_char(date_trunc('month', now()), 'YYYY_MM')";
        object? result = await cmd.ExecuteScalarAsync();

        return (string)result!;
    }
}
```

- [ ] **Step 6: Verify it builds**

```bash
cd vord-internal && dotnet build test/billing.integration/billing.integration.csproj -c Release --no-incremental
```

Expected: build succeeds. If the `Framlux.Vord.BillingGrpc` restore fails locally, the `framlux` NuGet source needs credentials — see `nuget.config` and the CI auth step; that is an environment issue, not a code one.

- [ ] **Step 7: Commit**

```bash
cd vord-internal
git add test/billing.integration vord-internal.slnx
git commit -m "test: add billing integration test project with a Postgres fixture

Kept separate from test/billing so the fast SQLite suite stays runnable without
a container runtime."
```

---

## Task 4: vord-internal migration chain tests

**Bead:** vord-y3w (second half).

**Files:**
- Create: `vord-internal/test/billing.integration/Migrations/MigrationRunnerLiveTests.cs`

**Interfaces:**
- Consumes: everything `MigrationTestDatabase` and `PostgresFixture` produce in Task 3.

- [ ] **Step 1: Write the fresh-migrate test**

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.Billing.Api.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.Billing.Api.Tests.Integration.Migrations;

/// <summary>
/// Runs the full FluentMigrator chain against a real Postgres.
/// </summary>
/// <remarks>
/// PartitionBillingAuditLogMigration shipped in April 2026 and did nothing on any database for
/// four months while recording itself as applied, surfacing only when the partition service threw
/// 42P17 at runtime on a rebuilt cluster. It threw nothing itself, so these tests assert the
/// resulting schema shape rather than merely that MigrateUp did not throw.
/// </remarks>
public sealed class MigrationRunnerLiveTests
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
    public async Task MigrationChain_AppliesCleanly_OnFreshDatabase()
    {
        string connStr = MigrationTestDatabase.CreateIsolated(_fixture.ConnectionString);
        await using ServiceProvider provider = MigrationTestDatabase.BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        foreach (string table in new[]
        {
            "BillingAuditLog",
            "ContactSubmissions",
            "WaitlistEntries",
            "Products",
            "Prices",
            "TierMappings",
            "PendingActions",
            "StripeCustomers",
            "StripeCanaryHealth",
            "CanaryWebhookReceipts",
            "WebhookEvents",
        })
        {
            await Assert.That(await MigrationTestDatabase.TableExistsAsync(connStr, table)).IsTrue();
        }
    }
}
```

- [ ] **Step 2: Run it**

```bash
cd vord-internal && dotnet build test/billing.integration/billing.integration.csproj -c Release --no-incremental
dotnet run --project test/billing.integration/billing.integration.csproj -c Release --no-build
```

Expected: PASS. A failure naming a missing table means either the name in the list is wrong (check `Create.Table` calls under `src/billing-api/Migrations/`) or a migration genuinely did not run — investigate rather than deleting the assertion.

- [ ] **Step 3: Write the partitioning test**

This is the case that matters: a smoke test asserting only "no exception" passes while the table is still unpartitioned.

```csharp
    [Test]
    public async Task BillingAuditLog_IsPartitioned_WithItsWindowAndIndexes()
    {
        string connStr = MigrationTestDatabase.CreateIsolated(_fixture.ConnectionString);
        await using ServiceProvider provider = MigrationTestDatabase.BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        // relkind 'p' is what the migration was supposed to produce and never did. 'r' here means
        // the migration ran, recorded itself, and changed nothing.
        await Assert.That(await MigrationTestDatabase.GetRelkindAsync(connStr, "BillingAuditLog"))
            .IsEqualTo('p');

        List<string> partitions = await MigrationTestDatabase
            .GetPartitionNamesAsync(connStr, "BillingAuditLog");

        // Current month plus three ahead, plus the default: rows outside the window would
        // otherwise be rejected, and rows landing in the default block a later partition for the
        // same range.
        await Assert.That(partitions.Count).IsEqualTo(5);
        await Assert.That(partitions).Contains("BillingAuditLog_default");

        string suffix = await MigrationTestDatabase.CurrentMonthPartitionSuffixAsync(connStr);
        await Assert.That(partitions).Contains($"BillingAuditLog_{suffix}");

        // Renaming a table in Postgres does not rename its indexes, so recreating them under the
        // same names raised 42P07 until the migration learned to drop the originals. Anything
        // other than exactly one per name means that regressed.
        foreach (string indexName in new[]
        {
            "IX_BillingAuditLog_TenantExternalId_Timestamp",
            "IX_BillingAuditLog_Timestamp",
            "IX_BillingAuditLog_Action_Timestamp",
        })
        {
            await Assert.That(await MigrationTestDatabase.CountIndexesAsync(connStr, indexName))
                .IsEqualTo(1);
        }
    }
```

- [ ] **Step 4: Run it**

```bash
cd vord-internal && dotnet build test/billing.integration/billing.integration.csproj -c Release --no-incremental
dotnet run --project test/billing.integration/billing.integration.csproj -c Release --no-build
```

Expected: PASS.

If the partition-name assertions fail, read the actual names from the failure and check them against the `partition_name` expression in `PartitionBillingAuditLogMigration.cs` — it builds `'BillingAuditLog_' || to_char(range_from,'YYYY') || '_' || to_char(range_from,'MM')` and creates them with `format('%I')`, which preserves case. Correct the expectation to what the migration produces; do not change the migration.

- [ ] **Step 5: Write the idempotency test**

```csharp
    [Test]
    public async Task MigrationChain_IsIdempotent_OnSecondRun()
    {
        string connStr = MigrationTestDatabase.CreateIsolated(_fixture.ConnectionString);
        await using ServiceProvider provider = MigrationTestDatabase.BuildMigrationServices(connStr);
        using IServiceScope scope = provider.CreateScope();
        IMigrationRunner runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        runner.MigrateUp();
        long firstRunVersionCount = await MigrationTestDatabase.CountVersionsAsync(connStr);

        runner.MigrateUp();
        long secondRunVersionCount = await MigrationTestDatabase.CountVersionsAsync(connStr);

        await Assert.That(secondRunVersionCount).IsEqualTo(firstRunVersionCount);

        // Still partitioned: a second pass must not convert the table back or duplicate work.
        await Assert.That(await MigrationTestDatabase.GetRelkindAsync(connStr, "BillingAuditLog"))
            .IsEqualTo('p');
    }
```

- [ ] **Step 6: Run the whole project**

```bash
cd vord-internal && dotnet build test/billing.integration/billing.integration.csproj -c Release --no-incremental
dotnet run --project test/billing.integration/billing.integration.csproj -c Release --no-build
```

Expected: 3 passed, 0 failed.

- [ ] **Step 7: Commit**

```bash
cd vord-internal
git add test/billing.integration/Migrations/MigrationRunnerLiveTests.cs
git commit -m "test: run the billing migration chain against real Postgres

Asserts shape, not just that MigrateUp returned: BillingAuditLog is partitioned
with its window and default partition, each index exists exactly once, and a
second run changes nothing."
```

---

## Task 5: vord-internal no-op dialect guard

**Bead:** vord-djv. Depends on Task 3.

**Files:**
- Create: `vord-internal/test/billing.integration/Migrations/MigrationDialectGuardTests.cs`

**Interfaces:**
- Consumes: nothing. Deliberately self-contained — it must not depend on a container, so it does not use `PostgresFixture`.

- [ ] **Step 1: Write the test**

Same invariant as Task 1, against this repo's migration assembly. It lives in the integration project only because that is where the Postgres runner package is referenced; it opens no connection and needs no container.

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;
using FluentMigrator.Infrastructure;
using FluentMigrator.Runner;
using Framlux.Billing.Api.Migrations;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Framlux.Billing.Api.Tests.Integration.Migrations;

/// <summary>
/// Guards the invariant that no migration is silently a no-op on the production dialect.
/// </summary>
/// <remarks>
/// PartitionBillingAuditLogMigration guarded every statement with IfDatabase("Postgres"), which
/// looks right because it is the name of the base PostgresProcessor. AddPostgres resolves to
/// Postgres15_0Processor, whose type is PostgreSQL15_0 with aliases PostgreSQL15_0 and
/// PostgreSQL — so nothing ran, and the migration still recorded itself as applied.
///
/// This builds the processor the same way production does rather than hard-coding the alias
/// list, and needs no database: IfDatabase is evaluated from the processor's declared dialect.
/// </remarks>
public sealed class MigrationDialectGuardTests
{
    [Test]
    public async Task EveryMigration_ProducesAtLeastOneExpression_OnPostgres()
    {
        using ServiceProvider services = BuildPostgresServices();
        using IServiceScope scope = services.CreateScope();
        IMigrationProcessor processor = scope.ServiceProvider.GetRequiredService<IMigrationProcessor>();

        List<string> silentMigrations = [];

        foreach (Type migrationType in GetMigrationTypes())
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
        // Without this, the guard above passes vacuously if migration discovery ever breaks.
        await Assert.That(GetMigrationTypes()).IsNotEmpty();
    }

    private static ServiceProvider BuildPostgresServices()
    {
        // Resolving the processor opens no connection, and the connection string is never used.
        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddPostgres()
                .WithGlobalConnectionString("Host=127.0.0.1;Database=unused")
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations());

        return services.BuildServiceProvider();
    }

    private static List<Type> GetMigrationTypes()
    {
        return [.. typeof(InitialMigration).Assembly.GetTypes()
            .Where(t => typeof(IMigration).IsAssignableFrom(t)
                && t.IsAbstract == false
                && t.GetCustomAttribute<MigrationAttribute>() is not null)
            .OrderBy(t => t.GetCustomAttribute<MigrationAttribute>()!.Version)];
    }
}
```

- [ ] **Step 2: Run it**

```bash
cd vord-internal && dotnet build test/billing.integration/billing.integration.csproj -c Release --no-incremental
dotnet run --project test/billing.integration/billing.integration.csproj -c Release --no-build --treenode-filter "*MigrationDialectGuardTests*"
```

Expected: 2 passed. All ten migrations emit expressions on Postgres today.

- [ ] **Step 3: Prove it fails on the original defect**

Temporarily change line 37 of `src/billing-api/Migrations/PartitionBillingAuditLogMigration.cs` from `IfDatabase("PostgreSQL")` to `IfDatabase("Postgres")` — and line 128 too, since the migration has two guarded statements. Rebuild and re-run.

Expected: FAILS, reporting `PartitionBillingAuditLogMigration`. This reproduces the exact April defect and demonstrates the guard would have caught it on the day it was written.

**Revert both edits** and re-run to confirm green.

- [ ] **Step 4: Commit**

```bash
cd vord-internal
git add test/billing.integration/Migrations/MigrationDialectGuardTests.cs
git commit -m "test: guard against migrations that silently no-op on Postgres

IfDatabase(\"Postgres\") matches no alias of the processor AddPostgres resolves,
so every statement was skipped and the migration was still marked applied."
```

---

## Task 6: vord-internal CI wiring

**Bead:** vord-tpg. Depends on Tasks 3-5.

**Files:**
- Modify: `vord-internal/.github/workflows/ci.yaml`
- Modify: `vord-internal/.github/workflows/prod.yaml`

- [ ] **Step 1: Add the step to the CI workflow**

In `.github/workflows/ci.yaml`, inside the existing `build-test` job, add after the "Run billing tests" step (and before "Summarise failing billing tests"):

```yaml
      - name: Run billing migration tests
        # Runs the full FluentMigrator chain against a real Postgres via Testcontainers. The
        # SQLite suite above skips every PostgreSQL-guarded statement by design, so this is the
        # only place the production half of the schema is executed.
        run: |
          set -o pipefail
          dotnet run --project test/billing.integration/billing.integration.csproj -c Release --no-build 2>&1 | tee billing-migration-test-output.log
```

It goes in the existing job rather than a new one so it reuses the checkout, the .NET setup, the GitHub Packages authentication, and the `dotnet build` of the whole solution. ubuntu-latest runners provide a Docker daemon, so Testcontainers needs no extra setup.

- [ ] **Step 2: Add the same step to the release workflow**

In `.github/workflows/prod.yaml`, inside the `test-billing` job, add the identical step after its "Run billing tests" step. This is the gate that actually blocks publish; a check present only in `ci.yaml` would let a release through.

- [ ] **Step 3: Verify the workflow files parse**

```bash
cd vord-internal
python3 -c "import yaml,sys; [yaml.safe_load(open(f)) for f in ['.github/workflows/ci.yaml','.github/workflows/prod.yaml']]; print('both parse')"
```

Expected: `both parse`.

- [ ] **Step 4: Confirm the whole solution builds and both test projects pass**

```bash
cd vord-internal
dotnet build vord-internal.slnx -c Release --no-incremental
dotnet run --project test/billing/billing.csproj -c Release --no-build
dotnet run --project test/billing.integration/billing.integration.csproj -c Release --no-build
```

Expected: the solution builds, the SQLite suite passes unchanged, and the integration suite passes with 5 tests.

- [ ] **Step 5: Commit**

```bash
cd vord-internal
git add .github/workflows/ci.yaml .github/workflows/prod.yaml
git commit -m "ci: gate on the Postgres migration tests

Wired into the release job too, since that is what blocks publish."
```

---

## Verification

After all six tasks, both repos green:

```bash
cd vord
dotnet build machine-info.slnx -c Release --no-incremental
dotnet run --project test/unit/database/unit.database.csproj -c Release --no-build
dotnet run --project test/integration/integration.csproj -c Release --no-build

cd ../vord-internal
dotnet build vord-internal.slnx -c Release --no-incremental
dotnet run --project test/billing/billing.csproj -c Release --no-build
dotnet run --project test/billing.integration/billing.integration.csproj -c Release --no-build
```

Then close the beads:

```bash
cd vord
bd close vord-rz6 vord-4gj vord-y3w vord-djv vord-tpg
bd close vord-7pe
```

Nothing here is pushed. Review and merge are Jonathan's call.

## Out of scope

- `Down()` migration paths. Production never rolls back; the first deploy freezes the chain.
- Testing `AuditLogPartitionService` itself.
- Replacing or reducing any SQLite coverage.
