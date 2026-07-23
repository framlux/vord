# Production Readiness Batch 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the owner-ruled findings from the 2026-07-22 product steelman review (`docs/reviews/2026-07-22-product-steelman-review.md`): truthful customer-facing copy, an achievable privacy promise, a written migration-freeze rule, a Postgres-persisted data-protection key ring, per-retention-class telemetry partitioning, and a working one-touch agent install.

**Architecture:** Tasks 1–4 are copy/policy/documentation fixes. Task 5 replaces the Redis `IXmlRepository` with a Postgres-backed one (new table, added in place to `InitialMigration` per the pre-GA rule). Task 6 re-partitions `MachineTelemetry` as `LIST(RetentionClass)` × `RANGE(ReceivedAt)` so partition drops honor each tier's retention. Task 7 makes the documented `curl | bash` install real.

**Tech Stack:** .NET 10 / LinqToDB / FluentMigrator / TUnit; SvelteKit (vord web + vord-internal marketing).

## Global Constraints

- **Owner rulings (2026-07-22):** in-app billing copy must match actual limits; homepage stops selling custom thresholds on Pro (Team feature); privacy policy amended to a request-based deletion process (no purge job in this batch); `InitialMigration` in-place edits are ALLOWED until first production ship and FORBIDDEN after (this batch documents the rule and uses the freedom); key ring moves to a database table; retention fix = retention-class partitioning (approved over the bridge-delete option); install must work as documented.
- Repos: `/Users/jonathanmiller/Repositories/framlux/vord` (branch `main`) and `/Users/jonathanmiller/Repositories/framlux/vord-internal` (branch `main`). Commits allowed, one per task: `git -c commit.gpgsign=false commit`, explicit file lists, **never stage `vord/nuget.config`**, no AI attribution, no plan/task numbers in code/comments/messages.
- `dotnet build machine-info.slnx` → 0 errors, 0 warnings after every vord task. TUnit via compiled executables only (never `dotnet test`, never `dotnet run --no-build`); binaries at `test/<tier>/<name>/bin/Debug/net10.0/osx-arm64/<name>`.
- Web checks: `pnpm -C src/web check|test|build` (vord); `pnpm check|build` in `vord-internal/src/marketing`.
- Suite baselines: unit.services.core 1487, unit.server 1003, unit.database 478, functional.web 736, functional.grpc 94, functional.hangfire 2, Vitest 424.
- Style (error-level `.editorconfig`): no `var`; Allman; file-scoped namespaces; `_camelCase` fields; XML docs on public members; alphabetical usings; blank line before `return` (except after a comment); explicit boolean comparisons; no null-forgiving `!`; one type per file; license header on new files:
  ```
  // Copyright (c) 2026 Framlux LLC
  // Licensed under the Functional Source License, Version 1.1, ALv2 Future License
  // See LICENSE for details.
  ```
- New shared code requires unit tests and, where it affects the HTTP/behavioral surface, functional tests. Marketing pricing/positioning edits must also update `static/llms.txt` and `static/sitemap.xml` if page content/URLs change (repo rule).
- Actual plan facts (single source of truth for copy): Free = 3 machines / 1-day retention / no alert rules; Pro = 1,000 machines / 60-day retention / default (built-in) alert rules, $5/host/mo; Team = 10,000 machines ("unlimited" marketing framing NOT allowed on the billing page — use real numbers) / 365-day retention / custom alert rules + audit log + custom OIDC, $10/host/mo. Verify against `TierDefaults` in `src/server/appsettings.json` and seeded `TierFeatureLimits` in `InitialMigration.cs` before writing copy; if they disagree with this list, STOP and report.

---

### Task 1: Migration governance rule

**Files:**
- Modify: `vord/src/database/Migrations/InitialMigration.cs` (file-header comment only)
- Modify: `vord/src/database/Migrations/InitialMigration2.cs` (file-header comment only)
- Modify: `vord/CLAUDE.md` (Database section)
- Modify: `vord/README.md` (add an Upgrading note)

**Interfaces:** none — documentation only. No code or schema change; no test change.

- [ ] **Step 1:** Directly below the license header of BOTH migration files, add:

```csharp
// PRE-PRODUCTION MIGRATION POLICY: until the first production release ships, schema
// changes are made by editing this migration in place and recreating databases.
// AFTER the first production release, this file is FROZEN — never edit it again;
// every schema change becomes a new, incremental FluentMigrator migration file with
// a new version number. FluentMigrator records applied versions and will silently
// skip an already-applied version, so an in-place edit after ship never reaches a
// live database.
```

- [ ] **Step 2:** In `vord/CLAUDE.md`, replace the sentence "Migrations are consolidated to two files … add schema in place, do not create new migration files." with:

```markdown
Migrations are consolidated to two files — `InitialMigration.cs` (all schema) and
`InitialMigration2.cs` (deferred self-referential `Users` FKs). **Pre-production only:**
add schema in place to these files (databases are recreated). **The moment the first
production release ships, both files are frozen forever** — from then on every schema
change is a NEW incremental migration file with a new `[MigrationVersion]`; never edit
a shipped migration (FluentMigrator silently skips already-applied versions, so the
edit would never reach a live database).
```

- [ ] **Step 3:** In `vord/README.md`, after the deployment/config sections, add an `## Upgrading` section stating: pin `IMAGE_TAG` to explicit versions (never `latest`) for production, run `migration_runner` before starting the new server/worker images, and that pre-1.0 releases may require a database reset (schema is consolidated until GA).

- [ ] **Step 4:** Build to prove the comment edits compile: `dotnet build machine-info.slnx --no-incremental` → 0/0. Commit (message: "Document the pre-production migration freeze policy" + a body sentence on the silent-skip rationale).

---

### Task 2: Postgres-persisted data-protection key ring

The key ring currently lives only in Redis (`AddCoreDataProtection`, `src/services.core/Extensions/ServiceCollectionExtensions.cs:211-236`) — a Redis flush permanently destroys Team-tenant OIDC secrets encrypted with it. Move it to a Postgres table shared by api-server and services-worker. Redis keeps no role in key persistence.

**Files:**
- Modify: `vord/src/database/Migrations/InitialMigration.cs` (add table in place — allowed pre-GA)
- Create: `vord/src/database/Models/DataProtectionKey.cs`
- Create: `vord/src/database/TableNames.cs` entry (verify: constants live where existing table names are declared — follow that file)
- Create: `vord/src/database/Repositories/IDataProtectionKeyRepository.cs` + implementation partial `DatabaseRepository.DataProtectionKeys.cs` (follow the existing partial-repository pattern)
- Create: `vord/src/services.core/Security/PostgresXmlRepository.cs` (implements `Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository`)
- Modify: `vord/src/services.core/Extensions/ServiceCollectionExtensions.cs` (`AddCoreDataProtection` swaps `RedisXmlRepository` → `PostgresXmlRepository`)
- Modify: `vord/src/server/Program.cs` (drop the now-unused `Microsoft.AspNetCore.DataProtection.StackExchangeRedis` using if orphaned)
- Test: `vord/test/unit/database/Repositories/DataProtectionKeyRepositoryTests.cs` (new)
- Test: `vord/test/unit/services.core/Security/PostgresXmlRepositoryTests.cs` (new)
- Check: `test/shared/FunctionalTestFactory.cs` — see Step 6.

**Interfaces:**
- Produces: table `DataProtectionKeys` (`Id` int identity PK, `FriendlyName` varchar(256) nullable, `Xml` text not null, `CreatedAt` timestamptz not null); `IDataProtectionKeyRepository` with `Task<IReadOnlyList<DataProtectionKey>> GetAllAsync(CancellationToken)` and `Task InsertAsync(DataProtectionKey, CancellationToken)`; `PostgresXmlRepository(IServiceScopeFactory scopeFactory)` implementing `GetAllElements()` / `StoreElement(XElement, string)`.
- Consumed by: nothing else in this plan.

- [ ] **Step 1 (RED):** Write `DataProtectionKeyRepositoryTests` — insert two keys, `GetAllAsync` returns both with Xml round-tripped exactly; empty table returns empty list. Use the existing unit.database test-factory pattern (in-memory SQLite via `TestDatabaseFactory` — read a neighboring repository test first and mirror it). Also write `PostgresXmlRepositoryTests`: `StoreElement` then `GetAllElements` round-trips an `XElement` through a substituted repository; `GetAllElements` on empty repo returns empty. Build → these fail to compile (types missing) = RED.

- [ ] **Step 2:** Add the table. In `InitialMigration.cs`, using the file's existing dual-database idiom (`IfDatabase("SQLite")` FluentMigrator syntax + `IfDatabase("PostgreSQL")` — plain table, NOT partitioned; follow how a simple existing table like the audit log is declared — if the file declares simple tables with provider-neutral `Create.Table(...)`, do the same here):

  Columns: `Id` int identity PK; `FriendlyName` string(256) nullable; `Xml` text not null; `CreatedAt` DateTimeOffset not null.

- [ ] **Step 3:** Model + repository (mirror an existing small partial, e.g. the audit-log one). `GetAllAsync` = full table read (the ring is tiny — dozens of rows over years). `InsertAsync` = plain insert.

- [ ] **Step 4:** `PostgresXmlRepository`. IMPORTANT: `IXmlRepository` is consumed as a singleton by key management while `DatabaseContext`/repositories are scoped — the class must take `IServiceScopeFactory`, create a scope per call, and resolve `IDataProtectionKeyRepository` inside it. Methods are synchronous by contract — block with `.GetAwaiter().GetResult()` and add a comment explaining the sync-over-async is forced by the `IXmlRepository` contract and runs only at startup/key-rotation (rare, never per-request).

- [ ] **Step 5:** Swap the registration in `AddCoreDataProtection`: delete the `IConnectionMultiplexer`/`RedisXmlRepository` configure-options block; register `options.XmlRepository = new PostgresXmlRepository(sp.GetRequiredService<IServiceScopeFactory>())`. Update the method's XML doc (replica sharing now via the shared database). Both `Program.cs` call sites (`server:322`, `services.worker:52`) are unchanged. Remove `Microsoft.AspNetCore.DataProtection.StackExchangeRedis` package reference from `services.core.csproj` if nothing else uses it (grep first).

- [ ] **Step 6:** Functional-test factory: check how `FunctionalTestFactory` (test/shared) handles data protection today (it imports `Microsoft.AspNetCore.DataProtection`). If it already replaces the repository with an ephemeral one for tests, keep that. If tests previously rode the Redis mock, point the factory at `EphemeralDataProtectionProvider` or let the SQLite table serve. The requirement: all six suites green with no test talking to real Redis for keys.

- [ ] **Step 7 (GREEN):** Build 0/0; run unit.database, unit.services.core, then all six suites. Expected: baselines + the new tests. Note in the report: existing Redis-held keys are NOT migrated (pre-GA: acceptable; deployments get a fresh ring; any existing sessions/CSRF tokens invalidate once — say so in the commit body).

- [ ] **Step 8:** Commit ("Persist the data-protection key ring in Postgres instead of Redis" + body: Redis flush previously destroyed the ring and with it every tenant OIDC secret encrypted by it; the shared database is the durable home; sessions issued before this deploy are invalidated once).

---

### Task 3: Retention-class telemetry partitioning

Physical retention currently = `MAX(TierFeatureLimits.RetentionDays)` (365) for every row (`DatabaseRepository.Partitions.cs:15-18`). Re-partition `MachineTelemetry` as `LIST("RetentionClass")` → per-class `RANGE("ReceivedAt")` dailies so each class drops on its own schedule. Approved semantics: **retention class is stamped at write time from the tenant's tier; a mid-window tier change does not reclassify existing rows** (downgrades stay query-filtered by the existing history validator; upgrades do not resurrect dropped data).

**Files:**
- Modify: `vord/src/database/Migrations/InitialMigration.cs` (in-place edit of the `MachineTelemetry` DDL, ~lines 186-198)
- Modify: `vord/src/database/Models/MachineTelemetry.cs` (add `RetentionClass`)
- Modify: `vord/src/database/Repositories/DatabaseRepository.MachineState.cs` (insert path stamps the class)
- Modify: `vord/src/database/Repositories/DatabaseRepository.Partitions.cs` + `IPartitionRepository` (per-class retention query replaces `GetMaxRetentionDaysAsync`)
- Modify: `vord/src/services.core/Services/Infrastructure/PartitionManagementJob.cs` (create/drop per class)
- Modify: `vord/src/services.core/Services/Telemetry/TelemetryService.cs` (thread the tier→class through the insert; the ingest path already resolves the subscription for `IsIngestEligibleAsync` — reuse it, do NOT add a second lookup)
- Create: `vord/src/database/RetentionClass.cs` (enum: `Short = 0, Medium = 1, Long = 2` with XML docs mapping to Free/Pro/Team retention windows)
- Tests: `vord/test/unit/services.core/Services/Infrastructure/PartitionManagementJobTests.cs` (extend), `vord/test/unit/database/...` for the repository changes, plus the integration suite (real Postgres — the only place composite-partition DDL truly executes).

**Interfaces:**
- Produces: `RetentionClass` enum in the database project; `MachineTelemetry.RetentionClass` (smallint, part of partition routing); `IPartitionRepository.GetLongClassRetentionDaysAsync(CancellationToken)` (see drop rule below).
- **Class assignment is by EFFECTIVE retention, not by tier name:** compute the tenant's effective retention days (tier default from `TierFeatureLimits`, overridden by any `TenantSubscriptionOverride` retention — the same effective-limits resolution `SubscriptionService.GetEffectiveLimitsForTenantAsync` already performs; reuse it, do not re-derive) and stamp the smallest class whose window covers it: ≤1 day → Short, ≤60 → Medium, otherwise → Long. With the standard tiers this degenerates to Free→Short, Pro→Medium, Team→Long, but an override tenant (e.g. Free with 30-day override) is automatically placed in the class that can physically hold its data.
- **Drop windows are fixed constants** — Short = 1 day, Medium = 60, Long = 365 — declared next to the enum, with one exception: the Long class's window is `GREATEST(365, MAX(override retention across all tenants))` via `GetLongClassRetentionDaysAsync`, so a rare >365-day override extends only Long. No per-class override query exists for Short/Medium; one tenant's override can never stretch another class's physical window.

- [ ] **Step 1 (design read):** Read `PartitionManagementJob.cs` fully, plus the DDL block and the insert path, before editing. Confirm partition names today (`MachineTelemetry_YYYYMMDD` shape at :223) and the SQLite branch (plain table — SQLite gets only the new column, no partitioning).

- [ ] **Step 2 (RED):** Extend `PartitionManagementJobTests`: (a) creates one daily partition per class per day with class-qualified names (`MachineTelemetry_Short_20260722` etc.) and correct `FOR VALUES` bounds; (b) drops a Short partition older than Short's window while the same-day Long partition survives; (c) Long-only override extension: with an override granting a tenant 400-day retention, Long's drop cutoff honors 400 while Short's and Medium's stay at their constants. Also unit-test the class-assignment map: 1→Short, 30→Medium (override case), 60→Medium, 61→Long, 365→Long, 400→Long. Unit-test the pure SQL-building/cutoff logic (extract `internal static` helpers per repo convention). Watch them fail.

- [ ] **Step 3:** DDL edit in `InitialMigration.cs` (PostgreSQL branch):

```sql
CREATE TABLE "MachineTelemetry" (
    "Id" BIGINT GENERATED BY DEFAULT AS IDENTITY NOT NULL,
    "MachineId" BIGINT NOT NULL REFERENCES "Machines" ("Id"),
    "TenantId" INTEGER NOT NULL REFERENCES "Tenants" ("Id"),
    "RetentionClass" SMALLINT NOT NULL,
    "TelemetryType" SMALLINT NOT NULL,
    "Payload" TEXT NOT NULL,
    "ReceivedAt" TIMESTAMPTZ NOT NULL,
    "ServerReceivedAt" TIMESTAMPTZ NOT NULL,
    "SourceEventId" VARCHAR(64),
    PRIMARY KEY ("Id", "RetentionClass", "ReceivedAt")
) PARTITION BY LIST ("RetentionClass")
```

Then create the three class parents in the same migration:

```sql
CREATE TABLE "MachineTelemetry_Short" PARTITION OF "MachineTelemetry"
    FOR VALUES IN (0) PARTITION BY RANGE ("ReceivedAt");
CREATE TABLE "MachineTelemetry_Medium" PARTITION OF "MachineTelemetry"
    FOR VALUES IN (1) PARTITION BY RANGE ("ReceivedAt");
CREATE TABLE "MachineTelemetry_Long" PARTITION OF "MachineTelemetry"
    FOR VALUES IN (2) PARTITION BY RANGE ("ReceivedAt");
```

Existing indexes on the parent propagate to all leaves — keep them as declared. SQLite branch: add `RetentionClass` SMALLINT NOT NULL to the plain table. Check `ServerSettingSeedTests`/any schema-pinning tests for impact.

- [ ] **Step 4:** `PartitionManagementJob`: daily creation loops classes × days (leaf `CREATE TABLE IF NOT EXISTS "MachineTelemetry_{Class}_{yyyyMMdd}" PARTITION OF "MachineTelemetry_{Class}" FOR VALUES FROM … TO …`); drop path uses the fixed Short/Medium constants and `GetLongClassRetentionDaysAsync` for Long, dropping each class's leaves past its own cutoff. Keep the existing identifier-validation and bounded-lookback behavior. Delete `GetMaxRetentionDaysAsync` and its callers/tests (replaced, not kept alongside).

- [ ] **Step 5:** Ingest stamping: the envelope-processing path already resolves the tenant's subscription (eligibility gate) and effective limits are available via `GetEffectiveLimitsForTenantAsync` (cached). Map effective retention days→class with a small `internal static` (unit-tested per the Step 2 matrix; unknown/zero → Short, fail-safe cheapest). Stamp every inserted row. If calling `GetEffectiveLimitsForTenantAsync` on the ingest hot path adds a per-envelope DB round-trip that the existing subscription cache does not already absorb, STOP and report — the cache design may need a retention field rather than a second lookup.

- [ ] **Step 6 (GREEN):** Build 0/0; unit suites green; **run the integration suite** (`dotnet run --project test/integration/integration.csproj` with Podman `DOCKER_HOST` exports per CLAUDE.md) — composite-partition DDL + insert routing + partition create/drop must be proven against real Postgres, not just SQLite. If an existing integration test seeds `MachineTelemetry`, it will catch routing errors (a row whose class has no leaf partition fails to insert — that is the loud failure we want in tests).

- [ ] **Step 7:** Commit ("Partition telemetry by retention class so drops honor each tier's window" + body with the write-time-stamping semantics and the override-extension rule).

---

### Task 4: One-touch install, for real

Make `curl -fsSL https://get.vordfleet.dev | sudo bash -s -- --token YOUR_TOKEN` work as documented, unify the two drifted script variants on the keyring flow, and make the KB truthful.

**Files:**
- Modify: `vord/deployment/agent/install.sh` (add flag parsing; keep env-var support)
- Modify: `vord/src/web/src/lib/utils/install-script.ts` (dashboard script: keyring flow, drop `apt-key`)
- Modify: `vord/src/agent/nfpm.yaml` (`license: Proprietary` → the real agent license (MIT per `vord/LICENSE.md`); fix `homepage` to the public repo URL)
- Create: `vord-internal/src/marketing/static/install.sh` (published copy — see Step 4 for how it stays in sync)
- Create: `vord-internal/src/marketing/src/hooks.server.ts` (or extend if exists): host-based intercept serving the script
- Modify: KB docs in `vord-internal/src/marketing/src/content/support/`: `getting-started/install-agent.md`, `getting-started/add-first-hosts.md`, `fleet-management/agent-troubleshooting.md`
- Modify: `vord/.github/workflows/prod.yaml` — add a step copying `deployment/agent/install.sh` into the marketing image build context or artifact (see Step 4 decision)
- Test: Vitest for `install-script.ts` (vord web); `bash -n` + a flag-parsing bats-style check if the repo has shell tests (if none exist, add a minimal `deployment/agent/install_test.sh` runner invoked manually — do not invent a new CI harness in this task)

**Interfaces:**
- Produces: `install.sh` accepting `--token <t>`, `--server <addr>`, `--update`, `--help` (flags override the existing `VORD_REGISTRATION_TOKEN`/`VORD_SERVER_ADDRESS` env vars; `--update` = package-manager upgrade path); served at `https://get.vordfleet.dev` (marketing app intercepts `Host: get.vordfleet.dev` and returns the static script as `text/plain`). DNS pointing `get.vordfleet.dev` at the marketing app is an OWNER action — flag it in the report, do not attempt it.

- [ ] **Step 1:** Flag parsing in `install.sh` (standard `while [[ $# -gt 0 ]] case … esac`), mapping to the existing env-var variables so the body stays single-path. `--update`: detect package manager, run `apt-get install --only-upgrade vord-agent` / `dnf upgrade vord-agent`, exit. `--help`: usage text with both flag and env-var forms. Verify with `bash -n` and by running `--help` locally.

- [ ] **Step 2:** Rewrite the dashboard generator (`install-script.ts`) to emit the SAME keyring flow as `deployment/agent/install.sh:64-79` (`signed-by` keyring, no `apt-key`), with the token inlined. Add/extend a Vitest asserting the emitted script contains `signed-by` and does NOT contain `apt-key`, and that the token lands in the right place.

- [ ] **Step 3:** KB truth pass — in the three docs replace, everywhere: service `vordfleet-agent` → `vord-agent`; config `/etc/vordfleet/agent.conf` → `/etc/framlux/vord-agent.toml`; endpoint `api.vordfleet.dev` → `grpc.app.vordfleet.dev`; uninstall rm-loose-files → `sudo apt remove vord-agent` / `sudo dnf remove vord-agent`; keep the `curl | bash` one-liner (it becomes true); `--update` doc now matches the real flag. Also fix the nav naming drift ("Fleet tab" → the real Dashboard/Machines/Register nav) while inside these files.

- [ ] **Step 4:** Serving: copy the canonical script to `marketing/static/install.sh` and add a CI guard instead of trusting manual sync — in `vord/.github/workflows/prod.yaml` (or a small script invoked by it) add a diff check failing the build when `vord/deployment/agent/install.sh` and the marketing copy diverge; in `hooks.server.ts`, when `event.url.hostname === 'get.vordfleet.dev'`, return the static file with `content-type: text/plain` for any path (so the bare-domain curl works). `pnpm check`/`build` in marketing must stay clean.

- [ ] **Step 5:** Commits — one in each repo ("Make the documented one-touch agent install real" for vord: script flags + dashboard keyring unification + nfpm metadata; "Serve the agent install script and correct the support docs" for vord-internal). Report the DNS action for the owner.

---

### Task 5: In-app billing page tells the truth

**Files:**
- Modify: `vord/src/web/src/routes/(app)/settings/billing/+page.svelte`
- Check: any Vitest pinning the old copy (`grep -rn "Unlimited machines" src/web/src` first)

**Interfaces:** none new. Copy only; no logic changes.

- [ ] **Step 1:** Fix the three verified lies (against the Global Constraints fact list, after re-verifying against `TierDefaults`/`TierFeatureLimits`):
  - `tierTaglines` (~line 55-58): Pro → `'Up to 1,000 machines, 60-day retention, built-in alert rules.'`; Team → `'Everything in Pro plus custom alert rules, audit log, SSO, and 1-year retention.'`
  - Downgrade-to-Pro confirm (~line 655): "Data retention will be reduced to 30 days" → "60 days".
  - Fallback prices (~lines 52-53): `?? 300` → `?? 500` (Pro $5), `?? 500` → `?? 1000` (Team $10). These render only when no catalog has synced; they must match marketed prices.
- [ ] **Step 2:** Sweep the rest of the page and the Plan Comparison table for the same numbers (machine limits row says Pro/Team "Unlimited" — change Pro to "1,000", Team to "10,000"; retention row already says 30 days? verify — the comparison table at ~line 994-998 says Pro "30 days": correct it to 60 if the seeded value says 60; if the table matches seeds already, leave it).
- [ ] **Step 3:** `pnpm -C src/web check` 0/0, `test` (fix any copy-pinning assertions to the new truth — flag each in the report), `build`. Commit ("Correct the billing page's plan limits, retention, and fallback prices").

---

### Task 6: Marketing and docs stop overselling

**Files:**
- Modify: `vord-internal/src/marketing/src/routes/+page.svelte` (~line 104 feature card)
- Modify: `vord-internal/src/marketing/src/content/support/fleet-management/alerts-setup.md` (Free-tier claims at ~lines 13, 41)
- Check: `vord-internal/src/marketing/static/llms.txt` for the same claims (repo rule: positioning changes update llms.txt)

**Interfaces:** none. `ComparisonTable.svelte` is already correct (custom rules = Team only) — do not touch it.

- [ ] **Step 1:** Feature card: `'… Custom thresholds on Pro. …'` → `'Built-in rules for the obvious stuff: disk failing, machine offline, temp rising. Custom thresholds on Team. Emails and webhooks, not a query language.'`
- [ ] **Step 2:** `alerts-setup.md`: align with code truth — Free tier has NO alert rules (evaluation skips Free; default rules are provisioned on upgrade). Rewrite the two passages claiming Free gets read-only built-in rules; state: alerting starts on Pro (built-in rules, email + webhook delivery); custom rules and thresholds are Team.
- [ ] **Step 3:** Grep `llms.txt` for "custom" / "alert" claims and align. `pnpm check` + `pnpm build` in marketing. Commit ("Align alerting claims with the shipped tier gates").

---

### Task 7: Privacy policy matches reality

**Files:**
- Modify: `vord/src/web/src/routes/privacy/+page.svelte` (~lines 110, 128)

**Interfaces:** none. Owner ruling: request-based process now; no purge job in this batch.

- [ ] **Step 1:** Replace the two promises:
  - ~line 110 ("If you cancel your account, we will delete your data"): → "If you cancel your account, your data becomes inaccessible immediately and telemetry expires automatically, within at most one year of collection. To request earlier full deletion of remaining account records, contact us at the address below and we will complete it within 30 days." (The "at most one year" wording is deliberate: retention classes drop on write-time schedules, so a downgraded tenant's hidden data can physically outlive their current plan's window.)
  - ~line 128 ("You may request … deletion"): keep the right, ground it: "You may request a copy of your data at any time using the built-in export (Settings → Data Export), and may request account deletion by contacting support; deletion requests are completed within 30 days."
  - Verify the page already displays a working contact address; if not, use the support contact used elsewhere in the app/marketing.
- [ ] **Step 2:** `pnpm -C src/web check` + `build`. Commit ("Make the privacy policy's deletion promise operationally true").

---

## Exit Criteria

1. Both builds 0 errors / 0 warnings; all six vord TUnit suites green; vord integration suite green (Task 3 requires it); vord Vitest green; marketing `pnpm check`/`build` green.
2. `git grep -n "apt-key" vord/src vord/deployment` → no hits. `git grep -n "vordfleet-agent\|/etc/vordfleet/" vord-internal/src/marketing/src/content` → no hits.
3. `MachineTelemetry` is LIST×RANGE partitioned; `GetMaxRetentionDaysAsync` no longer exists; partition job tests cover per-class create/drop + override extension.
4. `RedisXmlRepository` no longer referenced anywhere in vord; `DataProtectionKeys` table exists in both DDL branches.
5. Billing page, homepage, alerts KB, and privacy page contain none of the disproven claims (spot-check strings: "Unlimited machines" absent from Pro contexts, "Custom thresholds on Pro" absent, "we will delete your data" absent).
6. One commit per task, none touching `vord/nuget.config`; DNS action for `get.vordfleet.dev` reported to the owner as a handoff.
