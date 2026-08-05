# Postgres-backed migration tests (vord-7pe)

Date: 2026-08-05
Repos: `vord`, `vord-internal`

## Problem

Migrations are exercised against SQLite in CI. Every statement guarded by
`IfDatabase("PostgreSQL")` is skipped there by design, so the PostgreSQL-only half of the schema
is never executed by any test. Production is PostgreSQL.

`PartitionBillingAuditLogMigration` (vord-internal, 20260405001) shipped in April 2026 and never
did anything, on any database, while recording itself as applied. It carried two defects:

1. It guarded on `IfDatabase("Postgres")`. `AddPostgres()` resolves to `Postgres15_0Processor`,
   whose aliases are `PostgreSQL15_0` and `PostgreSQL` — not `Postgres` — so every statement was
   skipped and the migration was still marked applied.
2. Correcting the guard revealed it could never have succeeded anyway: renaming a table in
   PostgreSQL does not rename its indexes, so recreating them raised 42P07 "already exists".

It surfaced on 2026-08-04 only because `AuditLogPartitionService` threw 42P17 "BillingAuditLog is
not partitioned" at runtime on a rebuilt cluster.

The first production deploy freezes `InitialMigration` permanently. A schema defect that CI
structurally cannot see must not be discovered after that point.

## Current state

**vord** already runs `test/integration/Migrations/MigrationRunnerLiveTests.cs` against a
Testcontainers Postgres, gated by the `test-integration` CI job. It covers fresh-migrate,
idempotency, and shape assertions for tables, columns, defaults, nullability, and seed values.

Every one of those assertions is dialect-agnostic and already covered by the SQLite runs. Nothing
asserts the 34 `IfDatabase("PostgreSQL")` artifacts. The migrations do run against Postgres, but
nothing checks the Postgres-specific half produced what it should.

**vord-internal** has no Postgres migration coverage at all. One test project (`test/billing`),
SQLite-only via `FluentMigrator.Runner.SQLite`, no Testcontainers reference, and CI is a single
`build-test` job.

## Outcome

A test run that executes the full migration set against a real PostgreSQL instance, in CI,
failing the build when a migration errors or leaves the schema in an unintended shape.

The SQLite runs stay. They are fast and cover the dialect-agnostic path; this adds a second axis
rather than replacing one.

Assert shape, not just success. Both defects above would have passed a test that only checked
`MigrateUp` did not throw — the first because a skipped migration throws nothing at all.

---

## Part 1 — vord-internal integration test project

New `test/billing.integration/`: a TUnit exe like the other test projects, referencing
`api.csproj`, `Testcontainers.PostgreSql`, and `FluentMigrator.Runner.Postgres`. Registered in
`vord-internal.slnx`.

A `PostgresFixture` mirroring vord's: `postgres:18-alpine`, started in `[Before(Class)]` and
disposed in `[After(Class)]`, exposing the full connection string including the password (a
`NpgsqlDataSource.ConnectionString` strips it, which is not enough for tests that issue their own
`CREATE DATABASE`).

Each test runs against its own `migtest_<guid>` database created from the fixture's admin
connection, so cases do not observe each other's state.

The runner is built exactly as `src/billing-api/Program.cs:91-94` does —
`AddPostgres().ScanIn(typeof(InitialMigration).Assembly).For.Migrations()` — so the test exercises
the production processor resolution. That resolution is the thing that misbehaved; a test that
configured its own processor differently would not have caught it.

No skip-when-Docker-absent logic. Testcontainers throws on a missing container runtime and that
failure stands. A test that silently skips locally reintroduces the same blind spot.

`test/billing` stays SQLite-only and Docker-free.

### Cases

**Fresh migrate to head succeeds.** Plus canonical table existence sampled across the ten
migrations: `BillingAuditLog`, `ContactSubmissions`, `WaitlistEntries`, `Products`, `Prices`,
`TierMappings`, `PendingActions`, `StripeCustomers`, `StripeCanaryHealth`,
`CanaryWebhookReceipts`, and `WebhookEvents`.

**Partitioning post-condition.** `pg_class.relkind = 'p'` for `BillingAuditLog`; four month
partitions plus the default attached; and all three indexes
(`IX_BillingAuditLog_TenantExternalId_Timestamp`, `IX_BillingAuditLog_Timestamp`,
`IX_BillingAuditLog_Action_Timestamp`) present exactly once on the parent, since
duplicate-index-after-rename was defect #2. This is the case a smoke test passes while
still being wrong.

Partition names are month-derived at run time, so assertions read the database's own `now()`
rather than the test host clock, and check that the partition covering the current month exists.
No wall-clock dependency.

**Idempotency.** Migrate, migrate again; `VersionInfo` count unchanged and no throw.

**Conversion path.** `AddBillingAuditLogMigration` (20260327001) creates `BillingAuditLog` as an
ordinary table and `PartitionBillingAuditLogMigration` (20260405001) converts it, so the fresh
chain already exercises the rename-and-convert path. No separate scenario is needed.

## Part 2 — vord shape-parity assertions

Added to the existing `MigrationRunnerLiveTests`. These assert the Postgres-only artifacts that
the SQLite runs structurally cannot reach.

**Partitioned tables.** Four tables are partitioned on Postgres and plain on SQLite:
`MachineTelemetry` (two-level, `LIST("RetentionClass")` then `RANGE("ReceivedAt")` per class),
`AuditLog` (`RANGE("Timestamp")`), `AlertEvents` (`RANGE("TriggeredAt")`), and `RemoteCommands`
(`RANGE("CreatedAt")`). Assert `relkind = 'p'` for each, that `MachineTelemetry_Short`,
`_Medium`, and `_Long` are attached and are themselves partitioned, and that each parent has its
default partition. This is the same defect class as vord-internal's, at larger scale.

**Indexes, by definition rather than existence.** Several indexes exist under the same name on
both dialects with materially different definitions:

| Index | PostgreSQL | SQLite |
|---|---|---|
| `IX_Tenants_Name` | `UNIQUE (LOWER("Name"))` | `UNIQUE ("Name" COLLATE NOCASE)` |
| `IX_MachineTelemetry_SourceEventId` | `UNIQUE (SourceEventId, RetentionClass, ReceivedAt) WHERE SourceEventId IS NOT NULL` | `UNIQUE (SourceEventId) WHERE SourceEventId IS NOT NULL` |
| `IX_UserTenantRoles_Active` | `UNIQUE (UserId, AssignedTenantId) WHERE "IsActive" = true` | same shape, `= 1` |
| `IX_Machines_TenantId_Active` | `(TenantId) WHERE "IsDeleted" = false` | same shape, `= 0` |

An existence check passes on both. Assert against `pg_indexes.indexdef` so a wrong predicate or a
dropped column is caught. Also covered: `IX_TenantDeletions_ActiveTenant`,
`IX_RemoteCommands_CommandId`, `IX_IntegrationEndpoints_TenantId`,
`IX_IntegrationEndpoints_TenantId_Provider`, and the three trigram indexes
(`IX_MachineStateSummary_Hostname_Trgm`, `_Name_Trgm`, `_HardwareModel_Trgm`).

**Extension.** `pg_trgm` present in `pg_extension`. The three trigram indexes depend on it and it
has no SQLite counterpart.

**Check constraints.** `CK_AlertRules_DurationMinutes` exists with its expression, read via
`pg_get_constraintdef`.

**Deferrable foreign keys.** `FK_Users_CreatedBy` and `FK_Users_DeletedBy` on `UserAccounts` must
report `condeferrable AND condeferred`. That deferral is the entire reason those two are raw SQL
rather than fluent — the `System` row references itself, and without deferral a `--data-only`
restore requires disabling triggers. It is unverifiable on SQLite, and losing it would be silent.

## Part 3 — no-op dialect guard, both repos

A fast, database-free test in both repos. For each `[Migration]` type in the migrations assembly,
build a `MigrationContext` whose `QuerySchema` reports `DatabaseType = "PostgreSQL15_0"` with
aliases `["PostgreSQL15_0", "PostgreSQL"]`, call `GetUpExpressions`, and assert it yields at least
one expression.

The invariant is "no migration is silently a no-op on the production dialect" — exactly defect #1,
caught in milliseconds regardless of cause. It generalises past typo-hunting: any future reason a
migration ends up fully skipped fails the same assertion.

`IMigrationContext.QuerySchema`, `IMigrationContext.Expressions`, `IQuerySchema.DatabaseTypeAliases`,
and `IMigration.GetUpExpressions` are all present in FluentMigrator.Abstractions 7.2.0, verified
against the resolved package.

Both repos satisfy the invariant on the current tree: every migration emits at least one
unguarded or PostgreSQL-guarded statement. The only remaining `IfDatabase("Postgres")` occurrence
in either repo is inside a doc comment.

Placement: vord-internal's new `test/billing.integration`; vord's existing `test/unit/database`,
since it needs no container.

If the `GetUpExpressions` approach does not hold up against the real API surface during
implementation, fall back to a source-text scan of the migrations directory asserting every
`IfDatabase("X")` literal is a known processor alias, with comments stripped.

## Part 4 — CI

vord-internal gets a new job in `.github/workflows/ci.yaml` running the integration project, and
the same coverage in `prod.yaml` — the `test-billing` job is what actually gates publish, so a
check present only in `ci.yaml` would not block a release.

Both need the GitHub Packages authentication step the existing jobs carry, since the integration
project transitively references `Framlux.Vord.BillingGrpc`.

vord needs no CI change: `test-integration` already runs the project the new assertions land in,
and `test-unit-database` already runs the project the guard lands in.

## Out of scope

- `Down()` migration paths. Production never rolls back; the first deploy freezes the chain.
- Testing `AuditLogPartitionService` itself. This spec covers migration-produced schema shape.
- Replacing any SQLite coverage.
