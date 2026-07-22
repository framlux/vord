# Complexity Remediation Plans

Plans produced from the 2026-07-03 over-engineering audit of the C# server codebase.
The audit's conclusion: the architecture is sound; the removable weight (~4,000–5,000
production LOC + ~2,500–3,500 test LOC) is boilerplate, dead code from refactor
accretion, and ceremony. Each plan below is independently executable and leaves the
build green and all tests passing.

## Execution order

Run the plans in this order. Later plans assume earlier ones are done (e.g. plan 3
replaces blocks that plan 2 already shrank; plan 6 deletes interfaces that plans 2–3
touch call sites of).

| # | Plan | Scope | Est. effort | LOC removed |
|---|------|-------|-------------|-------------|
| 1 | [2026-07-03-1-dead-code-deletion.md](2026-07-03-1-dead-code-deletion.md) | Verified-dead types, methods, config, test cruft | ~1 day | ~1,300 prod + ~700 test |
| 2 | [2026-07-03-2-endpoint-error-helper.md](2026-07-03-2-endpoint-error-helper.md) | `SendApiErrorAsync` extension; replace 185 manual error writes | ~1 day | ~600 |
| 3 | [2026-07-03-3-tenant-401-centralization.md](2026-07-03-3-tenant-401-centralization.md) | `RequiresTenant` tag + pre-processor; remove ~65 per-endpoint 401 blocks | 1–2 days | ~450 |
| 4 | [2026-07-03-4-repository-consolidation.md](2026-07-03-4-repository-consolidation.md) | Merge near-duplicate repository methods; collapse subscription mutators | 1–2 days | ~500 |
| 5 | [2026-07-03-5-test-suite-builders.md](2026-07-03-5-test-suite-builders.md) | Builder pattern in worst unit-test files; trim duplicated functional validator tests | 1–2 days | ~2,000–3,000 test |
| 6 | [2026-07-03-6-interface-removal.md](2026-07-03-6-interface-removal.md) | Delete 16 never-mocked single-implementation interfaces | ~3 h | ~600 |
| 7 | [2026-07-03-7-small-consolidations.md](2026-07-03-7-small-consolidations.md) | Billing DTO/preamble dedup, parser catch blocks, health-threshold dedup, rate limiter, RetryHelper, invitation results, history endpoints, limit-resolution dedup | ~2 days | ~740 |

## Global constraints (apply to every plan)

- **Never run `git commit`.** Verify each task with build + tests, leave changes in the
  working tree, and continue. Jonathan reviews and commits.
- **Zero build warnings.** `dotnet build machine-info.slnx` must complete with 0 warnings
  after every task.
- **Code style** (enforced by `.editorconfig`, error-level): no `var`; Allman braces;
  file-scoped namespaces; `_camelCase` private fields; XML doc comments on public members;
  alphabetical `using`s; blank line before `return` (except after a comment); no `!boolean`
  negation — compare explicitly; no null-forgiving `!` operator — use nullable types with
  explicit checks; parenthesize compound conditions; one type per file (endpoint
  Request/Response co-location allowed); blank line at end of file.
- **License header** — every new file starts with:
  ```
  // Copyright (c) 2026 Framlux LLC
  // Licensed under the Functional Source License, Version 1.1, ALv2 Future License
  // See LICENSE for details.
  ```
- **Tests are exit criteria.** New shared code (helpers, pre-processors, consolidated
  repository methods) requires unit tests and, where it affects the HTTP surface,
  functional tests. Deleted production code takes its orphaned tests with it.
- **Test commands** (TUnit — run as executables, never `dotnet test`):
  ```bash
  dotnet run --project test/unit/server/unit.server.csproj
  dotnet run --project test/unit/services.core/unit.services.core.csproj
  dotnet run --project test/unit/database/unit.database.csproj
  dotnet run --project test/functional/web/functional.web.csproj
  dotnet run --project test/functional/grpc/functional.grpc.csproj
  dotnet run --project test/functional/hangfire/functional.hangfire.csproj
  # subset: append -- --treenode-filter "*SomeTestName*"
  ```
- **No schema changes anywhere in these plans** — no new migrations should be created.
- **In-flight branch caution:** `post-remediation-fixes` has uncommitted changes to
  `DatabaseRepository.Tenants.cs`, `MemberHandler.cs`, and their tests. Plan 4's Task 6
  and Plan 5's `MemberHandlerTests` task touch those files — do not start them until the
  branch's current changes are committed.

## Overall exit criteria

All seven plans are complete when:

1. `dotnet build machine-info.slnx` — 0 errors, 0 warnings.
2. All six TUnit projects above pass, plus `pnpm -C src/web test` and `pnpm -C src/web build`.
3. `grep -r "HttpContext.Response.WriteAsJsonAsync" src/server --include='*.cs'` returns
   matches only in `EndpointErrorExtensions.cs` and the two pre-processors.
4. No production symbol listed in plan 1's deletion inventory still exists
   (`grep` per plan-1 verification table returns 0).
5. Production LOC for `src/server` + `src/services.core` + `src/database` is reduced by
   at least 3,500 lines versus the pre-plan baseline (46,699 total C# LOC in src/ measured
   2026-07-03).

## Deliberately deferred — decisions Jonathan must make first

These audit findings are **not** planned because they need a product/ops decision:

- **One-time migration jobs** (`LegacyRedisKeyCleanup.cs`, `EncryptLegacyTenantOidcSecretsJob.cs`,
  ~330 LOC incl. worker wiring): delete only after confirming both have completed in every
  deployed environment.
- **Projection sharding machinery** (`StreamingShardCalculator`, per-shard locks/cursors,
  `StreamingOptions.ShardCount` defaults to 1): removable (~150–250 LOC) but it is a cheap,
  reversible seam. Keep or drop is a judgment call.
- **`SubscriptionService` split / `DowngradeSubscriptionEndpoint` handler extraction**
  (11–12 constructor deps): real design work, not mechanical cleanup.
- **Billing response double-wrap** (`{success, data:{success, message}}`): flattening it
  changes the wire format and requires coordinated `src/web` changes. Plan 7 dedups the DTO
  classes without changing the wire format.
- **`AuthorizationPolicies` constants**: plan 1 deletes the dead class. The alternative —
  adopting it across ~100 endpoints to eliminate magic policy strings — is a separate
  consistency initiative if wanted.
- **Inlining the async telemetry projection into ingest** (~740 LOC): performance tradeoff;
  only revisit with real telemetry-volume data.
- **Stripe price-ID fallback** (`BillingOptions.cs:25-50` + `StripeSyncJob.MapPriceIdToTier`,
  ~40 LOC): all six price-ID properties feed a tertiary fallback behind the billing DB's
  `TierMappings`; none is set in any appsettings. Dropping it (or keeping a two-ID
  belt-and-suspenders version) is a product decision.
- **Endpoint idiom unification** (query binding via `[QueryParam]` DTOs vs hand-parsed
  `Query<>`, divergent pagination clamping in 5 endpoints, ~60 LOC): opportunistic
  cleanup — fold into whichever plan next touches those files rather than a dedicated pass.
