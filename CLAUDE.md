# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Summary
This product is a software-as-a-service application that provides insight into Linux-based servers for customers. The customers are broken down into Tenants that have a single active subscription that limits what usage they can do in the platform. Users are assigned to one or more Tenants; machines are assigned to exactly one Tenant. Machine registration is initiated by installing the agent with a tenant-scoped registration token; the server issues a unique-per-machine API key upon successful registration. Installing the agent with that API key constitutes approval of the machine. Telemetry is only accepted from machines with a valid API key tied to an active subscription.

There are multiple usage tiers, each with their own limits for machine counts and service functionality. All users will sign-in via OIDC and there are no passwords or passkeys stored in the service. Tenants may upgrade or downgrade between subscription tiers.

System-wide administrators are marked with a special flag in their user account and can access system-wide settings.

The system is built to be run in a horizontally scalable way inside kubernetes and behind an SSL-terminating proxy. Services should attempt to be state-less, using stateful services (Postgres or Redis) to handle state as much as possible.

## Build & Run

Solution file: `machine-info.slnx` (.NET 10.0)

```bash
# Build entire solution
dotnet build machine-info.slnx

# Run individual services
dotnet run --project src/server/server.csproj
pnpm dev

# Publish (self-contained, specify RID)
dotnet publish src/server/server.csproj -c Release -r linux-x64 --self-contained
```

## Testing

Uses **TUnit** (not xUnit/NUnit). Tests run as an executable NOT using the `dotnet test` command. The test projects are split by tier — there is **no** `test/unit/unit.csproj` or `test/functional/functional.csproj`; run the specific project(s) you need:

```bash
# Unit tests (split by assembly under test)
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/database/unit.database.csproj

# Functional tests (full HTTP pipeline with in-memory SQLite, split by surface)
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
dotnet run --project test/functional/hangfire/functional.hangfire.csproj

# Integration tests (requires Docker or Podman for Testcontainers — spins up a real Postgres)
dotnet run --project test/integration/integration.csproj

# Go agent's cluster reporter against a real k3s in Podman (build tag k3s_integration, never part of go test ./...)
make -C src/agent test-k3s

# Web frontend (SvelteKit / Vitest)
pnpm -C src/web test      # also: pnpm -C src/web check  /  pnpm -C src/web build

# Target a subset of any TUnit project with the full four-segment path:
#   --treenode-filter "/*/*/SomeTestClass/*"
# A bare "*SomeTestName*" matches nothing and reports "Zero tests ran" rather than failing, so a
# filtered run can look green while running no tests at all. Check the total before believing it.

# For Podman on macOS, point Testcontainers at the podman socket first:
#   export DOCKER_HOST="unix://$(podman machine inspect podman-machine-default --format '{{.ConnectionInfo.PodmanSocket.Path}}')"
#   export TESTCONTAINERS_RYUK_DISABLED=true
#   export TESTCONTAINERS_HOST_OVERRIDE=localhost
```

- When adding or changing service dependencies, always update ALL test files that construct or mock the modified service. - Check for missing mock parameters, new interface dependencies, and model initializations before running tests.
- All new code must have unit tests and functional tests written for it
- Before completing any new coding work, please run all tests and verify code coverage is about 75% for all new code
- All new code must have unit and functional tests written, where possible
- Tests must adhere to FIRST principles and *must* test for intent and *never* simply exercise code or to increase test numbers
- Tests must test both happy-paths as well as error cases, parameter ranges (both valid ranges and invalid ranges), and null inputs.
- All new features, bug fixes, and code review fixes must include regression tests that verify the functionality works and prevent regressions. Tests are part of the exit criteria — no feature or fix is complete without unit and functional tests that would catch a regression if the code were reverted or broken.
- Extract non-trivial logic from endpoint handlers into `internal static` methods so they can be unit tested directly without framework coupling (e.g., `ValidateMetricConstraints`, `EvaluateCondition`). Endpoint handlers themselves are tested via functional tests.
- Validator tests must cover: valid requests, each field's invalid states, boundary values (at/above/below limits), and cross-field constraints (e.g., duration minimum depends on metric type).
- When a feature adds new enum values, seed data, or configuration: write tests that verify the exact values (durations, severities, flags) — not just counts. Configuration correctness tests catch silent regressions that count-based assertions miss.
- Measure code coverage using coverlet: `~/.dotnet/tools/coverlet test/unit/bin/Debug/net10.0/osx-arm64/unit.dll --target test/unit/bin/Debug/net10.0/osx-arm64/unit --include "[Assembly]Namespace.Class" --format cobertura --output coverage.xml`. Target >80% line and branch coverage for all new code. Note that functional tests run in a separate process and are not captured by coverlet — factor this into coverage analysis for endpoint handlers.

## Architecture

- Fleet/telemetry platform for managing machines and collecting system telemetry via an installable root-level agent.

- Prefer minimal, focused solutions over heavy infrastructure.
- When designing test frameworks or architectural changes, start with the simplest approach (e.g., extract business logic into testable classes) before proposing complex test servers or abstractions.

**Services:**
- `server` — REST API (FastEndpoints) + gRPC control plane. Ports: 12233 (HTTP), 12234 (gRPC)
- `web` — Sveltekit UI with Skeleton components, OIDC authentication
- `agent` — Deployed on managed machines (root privilege). Local SQLite database for queuing telemetry, communicates with server via gRPC and publishes telemetry via gRPC
- `migrationRunner` — Runs database migrations on startup
- `database` — Shared: LinqToDB models, FluentMigrator migrations, DatabaseContext
- `grpc` — Shared: the **agent** wire contracts only (`src/grpc/protos/` — registration, configuration, telemetry) and their generated code. Deliberately **not** published: every consumer is inside this repo, and the Go agent generates its own bindings from the raw `.proto` (`src/agent/Makefile` passes `-I ../grpc/protos`).
- `billing-grpc` — Shared: the **internal control-plane** contract (`src/billing-grpc/protos/BillingService.proto` — `BillingGateway`, `BillingManagement`, `FleetAdmin`) and its generated code. Published as the `Framlux.Vord.BillingGrpc` NuGet package for the closed billing repo to consume.

**The control-plane contract is a separate project on purpose.** One shared contract project meant one assembly, so anything referencing the agent contracts could also see the billing types — a convention, not a constraint. The split makes it a constraint at the project level, but `services.core` and `server` are each still a single assembly, so a `ProjectReference` alone would re-expose the contract to everything in them. `BillingContractBoundaryTests` (in `test/unit/server/Architecture/`) closes that gap by decoding each assembly's IL and failing when any type outside the permitted set touches `Framlux.Vord.BillingGrpc`. The rule is **not** "billing types stay in billing-named code" — the proto also declares `FleetAdmin`, which legitimately appears in fleet-admin code. It is: *the internal control-plane contract is referenced only by the server-side gRPC services that implement it and the `services.core` billing layer that calls it.* Permitted are the namespaces `Framlux.FleetManagement.Services.Core.Billing` and `Framlux.FleetManagement.Server.Endpoints.Web.Billing`, plus exactly three types — `BillingGatewayService`, `FleetAdminService` and `ServiceCollectionExtensions` (the composition root must name the generated client). `Endpoints.Grpc` is *not* allowed wholesale because the agent-facing `RegistrationService`, `ConfigurationService` and `TelemetryService` share that namespace. Widening the permitted set is a deliberate, reviewable change; a second test fails if an entry on the list no longer uses the contract, so the list cannot rot into a blanket exemption.

**This repository must build with no credentials.** It is open core, and a self-hoster clones it and runs `dotnet restore && dotnet build` with nothing configured. Concretely: `nuget.config` lists nuget.org and nothing else, no project may take a `PackageReference` on a package that only exists on an authenticated feed, and no workflow may read from `framlux/vord-internal`. The billing gRPC contract used to arrive as a `Framlux.Vord.BillingGrpc` package from GitHub Packages — which returns 401 to anonymous restore even for public packages — so the proto now lives here in `src/billing-grpc` and the dependency points the other way: vord publishes the contract, vord-internal consumes it. The generated C# namespace is `Framlux.Vord.BillingGrpc` (it comes from `option csharp_namespace` in the proto, not from the project's `RootNamespace`), so moving the file between projects changes no C# source in either repo — if a split of these contracts ever seems to require editing `using` statements, something else is wrong. Anything that reintroduces an authenticated feed, or a cross-repo read from the closed repo, breaks self-hosting.

**`Deployment:SelfHosted` is the single mode switch.** It defaults to `true` so a fresh clone runs with no configuration; the hosted deployment sets it to `false`. It alone decides the billing client (real versus `NoOpBillingApiClient`), whether `BillingGatewayService` and `FleetAdminService` are mapped, whether `StripeSyncJob` is registered, the email transport, and whether entitlement limits apply. `Billing:Enabled` was deleted — a second switch is exactly the drift this replaced. In self-hosted, `SelfHostedSubscriptionService` decorates `ISubscriptionService` and answers every entitlement question permissively; it must implement **all thirteen** interface members, because a delegated member silently reimposes a Free-tier limit that the interface does not reflect. Retention is the member most easily missed: it does not flow through `EffectiveLimits`, and it is capped at `RetentionClassPolicy.LongWindowDays`, not unlimited, because there is no unlimited retention class.

**Data flow:** Agent → gRPC → Server (control plane); Agent → gRPC → Server → PostgreSQL

**Auth:** API Key scheme for agents, OIDC/OAuth for web users (GitHub, Google, Microsoft social login; per-tenant custom OIDC for Team tier). Role-based: Admin, TenantAdmin, MachineAdmin, Viewer.

**Tenant deactivation is enforced within one request, not one TTL.** New logins, tenant-switch, and telemetry ingest block immediately on the live `Tenants.IsActive` check, but an already-open browser session carries its tenant role claim in the auth cookie. So `TenantDeletionHandler` — on both `RequestDeletionAsync` and `RestoreAsync`, **after commit** — evicts every tenant member's cached role claims via `IRoleCacheInvalidator`; `CookiePrincipalValidator` then rebuilds claims from `GetTenantsForUserAsync`, which filters on `Tenants.IsActive`. The eviction is best-effort (a Redis failure is logged, never fails the deletion) with the ≤5-minute claim-refresh TTL as the backstop. Any future path that flips a tenant's active flag must do the same, or it silently reverts to TTL-bounded enforcement.

**Enterprise is a tier owned by its agreement, never by Stripe.** `SubscriptionTier.Enterprise` (4) has every Team feature and limits set per enterprise agreement, invoiced outside Stripe. Two rules must never collapse into one: "has Team features" is `SubscriptionPolicy.RequiresTeam(...) == false` — the predicate is block-polarity and refuses everything outside `{Team, Enterprise}`, so `== false` means the tenant has Team features, not that it is on the Team tier — and "billed through Stripe" is `StripeBilledTiers` (`{Pro, Team}`); anything that reconciles, pushes quantities or charges uses the second. Every existing subscription write (`UpdateSubscriptionStateAsync`, `UpdateSubscriptionPeriodEndAsync`, `SetCancelAtPeriodEndAsync`) filters `Tier <> Enterprise` in SQL, and `UpdateSubscriptionStateAsync` additionally throws if asked to set tier Enterprise, so no Stripe action, reconciliation pass or customer action can change an Enterprise row. A caller that reads the tier (possibly from the 30-second cache) and then writes must treat a write that changed no row as "an agreement was applied meanwhile": the customer billing endpoints that write the row (Team-to-Pro downgrade, Free cancel, reactivate) answer 409 with `BillingEndpointGuards.EnterpriseAgreementMessage` and leave their transaction uncommitted, and the webhook handlers skip their cleanup. The only way in is `ApplyEnterpriseSubscriptionAsync` via `IEnterpriseAgreementHandler` (`FleetAdmin.ApplyEnterpriseAgreement`), which writes tier, term and every override in one transaction and refuses a revision lower than the one applied (an equal revision is a no-op, and a duplicate that loses the guarded update to its twin is also reported `AlreadyApplied`, never `StaleRevision`, because billing-api treats the first as success). `BillingGatewayService` acknowledges and ignores every `BillingAction` for an Enterprise tenant; `UpdateTenantSubscription`, `SetTenantOverride` and `RemoveTenantOverride` refuse an Enterprise tenant, and `UpdateTenantSubscription` also refuses Enterprise as a target. The two override RPCs refuse atomically with the write through the SQL-guarded `UpsertOverrideUnlessEnterpriseAsync` / `RemoveOverrideUnlessEnterpriseAsync`, after locking the subscription row first (`GetSubscriptionForUpdateAsync`: `FOR UPDATE` on Postgres, a no-op on SQLite, never cached) so an apply in flight finishes before the guarded write starts. An agreement may set the alert-rule and webhook limits to 0 ("none allowed") deliberately, while -1 (the tier default) is never written for Enterprise.

**Irreversible tier-change side effects run inside the transaction that changes the tier.** The Free and Pro downgrade cleanups (`IDowngradeCleanupService`: OIDC off, rules and integrations disabled; machines are never deleted — hosts past the new limit are paused, which is derived and never stored, and registration refuses new ones) run before the commit, so the tier write's row lock makes a concurrent agreement apply wait for them instead of landing between a committed tier and a cleanup that would then hit an Enterprise tenant. Only what a rollback cannot undo stays after the commit: retention reclassification and cache invalidation. `EnterpriseAgreementHandler.ApplyAsync` follows the same rule: it locks and reads the prior row (uncached) first, then writes tier, overrides, the provisioner restore and, last, the audit row in one transaction, so a failed restore rolls the apply back and billing-api's retry applies the revision afresh instead of finding it already applied; the subscription cache is invalidated after the commit and also when the transaction rolled back, because the restore reads through the cache on the transaction's connection. The audit insert stays last on purpose: the provisioner swallows a concurrent seed's unique violation, which aborts a PostgreSQL transaction, and the next statement is what turns that into a thrown error instead of a commit that silently rolls back. Ending an agreement is still not implemented: the Free downgrade no longer deletes machines, but retention collapse and the OIDC lockout check remain unsolved (see the Enterprise spec's "Later: ending an agreement"). Once any tenant is Enterprise, rolling the fleet back to a build without the value is unsafe — fix forward.

## Key Conventions
- All code files must start with the license header; each service or application must have a license file in the root folder of the service or application
- Start with the Microsoft C# Coding Standards https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions
- For Go code, follow the Go Coding Standards https://go.dev/doc/effective_go
- For Svelte code, follow the Svlete guidelines https://svelte.dev/docs/svelte/best-practices
- All code should compile without any errors or warnings

**Enforced by `.editorconfig` (errors/warnings):**
- **No `var`** — always use explicit types (error-level)
- File-scoped namespaces
- Private fields: `_camelCase`
- Allman brace style (brace on new line)
- No `this.` qualifier
- XML doc comments required on public members (CS1591 warning)
- Nullable enabled globally

**Other code standards**
- Always keep logical operators (| && ||) on the same line
- Conditional statements should use parens to make it clear what operations is what; for example: if (a || (b == false)), if ((a == true) && (b == false)), if (string.Equals("foo", "bar") && a), if ("foo".Equals("bar") && (a == false))
- Never use !boolean, always be explicit. For example: if (false == false)
- Always add blank line before return statements EXCEPT if the line prior is a comment
- Always add a blank line at the end of files
- Only one class or record or enum or struct per file (except for auto-generated files). Endpoint Request/Response types may be co-located in the same file as their Endpoint class, following the FastEndpoints convention. Private nested types (used to encapsulate implementation details — e.g., disposal handles, internal state machines) are permitted in the enclosing type's file.
- Commends should always be written in natural language and should never include prompt information, plan numbers (ex: H1), and should always be descriptive on the intent of what the code is doing
- Never had "Yoda statements", which are things like if (false == some_expression), always put the variable before the constant
- All files should be formatted with spaces not tabs
- All .NET `using` statements must be in alphabetical order
- Any timestamps should be serialized as ISO8601

**Namespaces:** `Framlux.FleetManagement.{Server|Agent|Web}`

**Database:** LinqToDB (not EF Core). Models use `[Table]`, `[Column]`, `[PrimaryKey]` attributes. Access via domain-specific repository interfaces (e.g., `IMachineRepository`, `IAuditLogRepository`) — never inject `DatabaseContext` directly in server-side code. `DatabaseContext` is only used within repository implementations in the `database` project and for DI registration in `Program.cs`. Do not use composite/aggregate repository interfaces. If a constructor has 6+ repository dependencies, consider whether the class has too many responsibilities. Migrations use FluentMigrator. Migrations are consolidated to two files — `InitialMigration.cs` (all schema) and `InitialMigration2.cs` (deferred self-referential `Users` FKs). **Pre-production only:** add schema in place to these files (databases are recreated). **The moment the first production release ships, both files are frozen forever** — from then on every schema change is a NEW incremental migration file with a new `[MigrationVersion]`; never edit a shipped migration (FluentMigrator silently skips already-applied versions, so the edit would never reach a live database). Internal stream-cursor / checkpoint state (e.g. the projection high-water mark) belongs in its own dedicated table keyed by shard/partition id — **never** in `ServerConfigurationSettings`, which is for admin-facing configuration only.

**API Endpoints:** FastEndpoints pattern — inherit `Endpoint<TReq, TRes>`, configure route/auth/version in `Configure()`, implement `HandleAsync()`. Versioned routes: `/v{n}/api/{resource}`.

**Validation:** Use `Validator<T>` (FastEndpoints' wrapper around FluentValidation) for all request-level validation. Validators auto-register and run before the handler — no manual DI wiring needed. Validators are singletons; use `Resolve<T>()` inside rules to access scoped services if needed. Place all constraint logic that can be derived from the request DTO in the validator (field formats, ranges, per-metric minimums, enum parsing). Business logic validation that requires DB state (e.g., checking a record exists, verifying tenant ownership) belongs in the handler using `AddError()` / `ThrowIfAnyErrors()`. When the handler needs DB-loaded context for validation (e.g., a metric type stored on an existing rule), include that field in the request DTO so the validator can enforce constraints, and have the handler verify the request value matches the DB value.

**Billing:** Stripe integration for subscription management (checkout, webhooks, customer portal). Tiers: Free, Pro, Team, Enterprise. Enterprise is billed by invoice outside Stripe and is owned by its agreement (see the Enterprise paragraph under Architecture).

**Infrastructure:** PostgreSQL, Redis, Docker containers published to GHCR (`ghcr.io/framlux/fleet/*`). Logging via Serilog.

**Server configuration settings (replica-safe cache):** admin-facing settings (`ServerConfigurationSettings`) are read through two layers: a shared Redis read-through in `ServerConfigurationService` (`config:{key}`, 5-min TTL) and Postgres as the source of truth. There is deliberately **no per-process in-memory layer** — a third layer was removed once it was found to have no production readers, and reintroducing one would put a replica-local copy behind the shared cache that nothing can invalidate promptly. Redis misses fall through to `IServerSettingsReader.GetSettingFromDatabaseAsync` (in the `database` project), which always reads the database, so a just-invalidated key is never re-seeded stale. Every write path — REST `AdminHandler` and gRPC `FleetAdminService` — must, **after commit**, call `ServerSettingsInvalidation.InvalidateAsync`, which deletes the Redis key; it is best-effort, with the Redis TTL as the correctness backstop. Both write paths validate through the single `ServerSettingValidation` helper. The `database` project takes no Redis dependency.

**The quota cutoff is cleared after commit, never inside the transaction that moves it.** Which hosts are paused is derived, never stored: `QuotaPauseService` caches each tenant's cutoff in Redis (`quota:cutoff:v1:{tenantId}`, 30 seconds), and a host past the cutoff is paused. Every write that can move it — a tier change, an override set or remove, an Enterprise apply, and a machine register, delete or purge — marks the scoped `QuotaCutoffInvalidator` at the seam that detects the change: `CachingSubscriptionRepository` for tier writes and the Enterprise apply, the override RPCs in `FleetAdminService`, `MachineService`, `MachineHandler` and `TenantPurgeJob`. The clear runs once that write has committed, deleting the subscription entry first and the cutoff second, because ingest re-seeds the subscription entry on every envelope and a cutoff rebuilt from the old tier would outlive the change; clearing inside the transaction fails for the same reason. The webhook handlers, the downgrade and reactivate endpoints and the override RPCs dispatch right after their own commit (the sync job applies every paid tenant's correction in one scope, so waiting for disposal would leave entries stale for the whole pass). The machine paths dispatch where their own ordering needs it: registration marks right after the insert and dispatches after the billing sync, delete dispatches after the API-key eviction so nothing the clear could throw leaves the deleted machine's key authenticating, and purge dispatches after the fleet commit, before the billing teardown that can fail and retry. Disposing the scope dispatches whatever is left, which is all the Enterprise apply relies on and what makes a throw between a mark and its dispatch still clear. The deletes are fire-and-forget and best-effort, with the 30-second lifetime as the backstop. A limit change also writes a `HostsPaused` audit row through `HostsPausedAuditWriter`, inside the same transaction as the tier or override write, counting from the limit just written (`GetMachineLimitForTierAsync`, which reads the database, never the cache). `FleetAdminService.UpdateTenantSubscription` has no transaction, so its audit and clear run after the alert-rule restore, which a retry could no longer perform once the tier is written.

**Paused hosts are derived, acknowledged-and-discarded, never stored.** `IQuotaPauseService` is the only place the rule is computed: a tenant's oldest N registered machines by `Id` stay active, where N is `EffectiveLimits.MachineLimit`. A limit of 0 or less pauses all of them. Self-hosted resolves to `int.MaxValue` and never pauses. On every agent-facing surface, an inactive subscription wins over a pause.

- **Ingest.** `ProcessEnvelopeAsync` checks the pause after the subscription gate and the envelope sanity checks, and before the dedup mark. A paused host gets `TelemetryAck{Success=true, Reason=HOST_PAUSED}`, and nothing is stored, deduped, enqueued or alerted; the envelope counts as `IngestOutcome.Paused`. The unary inactive refusal keeps `Success=false` and adds `SUBSCRIPTION_INACTIVE`. The stream still refuses an inactive tenant at open with PermissionDenied and gives no reason; agents learn it from the unary fallback or from ping.
- **Ping.** `AgentPing` records the heartbeat first. It then reports `state`: `SUBSCRIPTION_INACTIVE`, else `HOST_PAUSED`, else `UNSPECIFIED`. Neither state ever sets `success=false`. A failed heartbeat write still returns `success=false` with no state. A failed subscription lookup or pause lookup reports `UNSPECIFIED` and is counted under the `ping` surface, because the heartbeat has already landed and ingest enforces both rules on every envelope anyway; only a canceled call propagates.
- **Commands.** `GetPendingCommands` returns nothing for a paused host. `SubmitCommandAsync` answers 409 after the ownership and key-authorisation checks.
- **Failure.** Ingest, ping, commands and every display surface treat `QuotaCheckUnavailableException` as active. They count it on `vord.quota.pause_check_failures{surface}` and log the cause. Display surfaces go through `QuotaCutoffDisplay` and fetch the cutoff once per request.
- **Alerts.** `AlertEvaluationJob` visits tenants with an enabled rule or an open event. An unavailable decision skips the tenant for that pass, because frozen summaries would page. Before the tier skip, the job silently resolves paused hosts' open events and deletes their condition rows, then excludes paused hosts from evaluation. `UpsertObservationAsync` restarts `FirstTriggeredAt` when the stored observation is older than `AlertConstants.ConditionObservationGap`, so a resumed host cannot page on its first pass. The gap is three minutes: the job runs every minute, and one missed or delayed pass plus drift must not reset a window that is still running. Pausing does not rely on the gap, because a paused host's rows are deleted outright.
- **Display.** Paused overrides health and online status everywhere. DTOs carry `isPaused`; `IsOnline` stays heartbeat-true, so UI code checks `isPaused` first. Paused is its own bucket in fleet aggregation and dashboard counts, never counted as online, offline or any health in any tenant-facing count; the cross-tenant `vord.fleet.machines` gauge still counts them under their last swept health (or Offline if never reported), and `vord.fleet.paused_machines` counts them separately. A health filter never matches a paused host. `paused` is a separate filter on the list, search and `/machines/ids`. Status sorting puts paused hosts last in both directions, ordering by paused first and then by health in the requested direction. Fleet search's numeric filters still use a paused host's last reported figures. The admin contract carries `FleetTenant.paused_machine_count`, from the shared limit fold on `ListTenants` and from the cutoff on `GetTenantDetail`, and `FleetMachine.is_paused`, from each tenant's cutoff. `vord.fleet.paused_machines` is computed in four set-based queries folded in memory through the same limit fold, and reports 0 in self-hosted. A rule that gates on "machines exist" must subtract it: otherwise a fleet where every host is paused reads as an ingest stall, because paused envelopes count as `paused`, not `accepted`.
- **Billing.** `GetBillableMachineCountAsync` bills `max(floor, min(registered, limit(tier)))`.
- **Agent.** Pause is an in-memory lease in `internal/pause`.
  - An ack or ping carrying either reason starts or renews the lease. A successful ping with no reason ends it, and a failed ping changes nothing. The lease lapses after 3 live ping intervals without renewal.
  - Entering a pause clears the queue and any not-yet-queued SSH rows, closes the streams, gates every collector and the command poll, and skips the shutdown flush.
  - Leaving a pause clears the queue again, advances the journal cursor or the auth.log offset to now, and drops the CPU baseline before collection restarts. The second clear matters: a collector tick that passed the pause gate just before the pause began can still enqueue after the first clear, and nothing legitimate is queued while paused, so whatever is there on resume is a straggler that must not be sent.
  - Old agents mark an acked batch delivered, so the paused period is a deliberate, unrecoverable gap.
- **Tests.** Give every `ISubscriptionService` substitute an explicit machine limit. NSubstitute's default of 0 pauses everything under a real `QuotaPauseService`.

**Online and healthy are orthogonal, and liveness has one source.** Online asks whether the server has heard from a machine lately; health asks whether what it reported is good. The health sweep answers both: it short-circuits to `Offline` when neither the telemetry receipt (`LastSeenAt`) nor the heartbeat receipt (`LastHeartbeatAt`) is within `OnlineThresholdSeconds`, and otherwise writes the maximum of the metric verdict and a staleness verdict, so stale telemetry raises a Warning but never masks a Critical. Every read path derives `IsOnline` from `MachineLiveness`, never from a second clock — the Redis ping path was removed precisely so a second clock cannot come back. `AgentPing` writes `LastHeartbeatAt` at server receipt time through a monotonic guard; the agent clock is never trusted. `OnlineThresholdSeconds` must stay at least twice `AgentHeartbeatSeconds`, enforced by `ServerSettingValidation.ValidateRelationships` on both the REST and gRPC write paths, because the agent jitters its heartbeat ±15% and equal values made a healthy machine flap offline on half its cycles.

**The Kubernetes inventory is its own projection, and every write to it takes a per-tenant lock.** Agents report a host's Kubernetes identity from local files as telemetry type 14, and the one candidate that holds a cluster's reporter Lease reports the cluster itself as type 15. `KubernetesInventoryProjectionService`, a worker-only background service, reads those rows past its own cursor and builds `KubernetesClusters` and `KubernetesNodes` from them. It is deliberately not part of the machine-state projection, which ignores types 14 and 15 through its unknown-type branch, so a report that cannot be applied never holds back CPU and memory state. The cursor lives in its own table (`KubernetesInventoryProjectionCursor`, one row) and is written in the same transaction as each applied row; a skipped row advances it with a standalone write, so nothing is held in memory and there is no shutdown flush. One replica projects at a time under `LockNames.KubernetesInventoryProjection`. Every Kubernetes write — the projection, garbage collection, machine soft delete, cluster rename and tenant purge — first takes `pg_advisory_xact_lock(KubernetesTenantLockNamespace, tenantId)` (namespace constant `0x4B38_5349`, Postgres only), so they never interleave on one tenant; a new Kubernetes write must take it too. `ApplyLocalNodeReportAsync` resolves a report in this order:
- **Established membership.** A machine that already has a node, or a reporter candidate row, keeps its cluster; when the two disagree the node's cluster wins and the candidate row follows it. A fingerprint that disagrees only sets the node's mismatch flag, and clears again when it agrees.
- **Moved to another cluster.** The exception to membership: when the report's fingerprint differs from the machine's cluster and exactly one *other* cluster in the tenant holds it, the machine has moved. The node it had is deleted when only local reports ever described it, and is unlinked from the machine (`MachineId` null) when the cluster reporter has seen it, since the reporter still lists that node; a node is created for the machine in the cluster it moved to, its candidate row moves with it, and the cluster it left keeps its key and its other nodes. During a genuine rotation no other cluster holds the new fingerprint, so this never fires then.
- **Fingerprint.** A machine with no node joins the one cluster that holds its fingerprint, or the one of several sharing it that already has a node of that name; otherwise it is ambiguous and nothing is written. A report without a fingerprint cannot create a node.
- **Rotation join.** When no cluster holds the fingerprint, the machine joins the one cluster in which a strict majority of the nodes that reported in the last hour report it, and otherwise founds a new cluster. A single drifted node therefore cannot pull a newcomer in; the nodes that rotate later move to the new cluster, and garbage collection deletes the old one once it is empty.
- **Re-key.** When every node of a cluster that reported in the last hour reports one fingerprint that differs from the cluster's, and no other cluster in the tenant holds it, the cluster takes that fingerprint and every node's flag is recomputed.

Once the cluster is resolved, three more rules apply in the same transaction:
- **Name reconciliation.** A report whose node name came from the hostname fallback (`NodeNameSource == "hostname"`) applies to the cluster's reporter-sourced row instead of adding a hostname-named one, and the reporter's name is authoritative. The row must have a `LastReporterSeenAt`, and its `NodeMachineId` must equal the machine's `SystemId` (ordinal, case-insensitive). The match is refused, and the report keeps its own row, when the `SystemId` is empty, when no row or several rows match, or when another non-deleted machine in the tenant carries the same `SystemId`: cloned machines share one, and merging would hand the row back and forth on every report.
- **Candidate upkeep.** A machine that reports `ReporterEnrolled` is upserted as a `KubernetesReporterCandidates` row of its cluster (`LastReportAt` is the report's `ServerReceivedAt`, `ReporterError` is the report's). A machine that does not is deleted from the table. A machine is a candidate of one cluster only, so an existing row in another cluster is moved. A report older than the machine's candidate row is superseded and changes nothing.
- **Reporter rows are never deleted by a local report.** When a machine moves to a different node row, or to another cluster, its previous row is deleted if only local reports ever described it, and merely unlinked from the machine (`MachineId` null) if the cluster reporter has seen it, because the reporter still lists that node.

A cluster's distro follows the latest report that names a known one. A host with no kubelet is never a node and never founds a cluster, but an enrolled one still takes part in reporting, as a k0s controller does: when it is already a candidate of a cluster, or exactly one cluster in the tenant holds its fingerprint, its candidate row is upserted there and the outcome is `CandidateOnly`; otherwise it writes nothing, and a report that says it is not enrolled deletes its candidate row. The machine page derives "ambiguous" and "unknown" when it reads, from the latest report and the clusters that hold its fingerprint, and never stores them.

`ApplyClusterSnapshotAsync` applies a type-15 snapshot in its own transaction under the same lock and writes the cursor whatever the outcome (`Accepted`, `Fenced`, `MachineDeleted`, `ErrorRecorded`, or `Unresolved` when nothing could be placed). `KubernetesClusterPayloadNormaliser` runs first: a UID that is not a UUID or a fingerprint that is not 64 lowercase hex becomes null, a reporter error that is none of the four codes becomes `api_unreachable`, an error snapshot lists no nodes, strings are cut to their columns, and nodes whose name is not a Kubernetes node name are dropped. A snapshot whose Lease creation time cannot be read or lies more than ten minutes after the server received the snapshot, or whose transition count is negative, cannot be ordered, so it is skipped and counted as `invalid_payload`; a bogus future epoch would otherwise outrank, and so fence, every later snapshot of the cluster. **Order matters: resolve, separate and merge first, rebind to the surviving cluster, then fence, then write.** Fencing a pre-merge cluster could compare the snapshot with the wrong epoch or transition count, so a snapshot that is then fenced has still merged the clusters it proved to be one.
- **Resolution.** The sender's candidate row, else its node row, else the cluster holding the snapshot's UID, else the one UID-less cluster holding its fingerprint (adopted, keeping its id and name), else a new cluster with an unknown distro. An error snapshot, or one without a fingerprint, never founds a cluster and returns `Unresolved`.
- **A UID is never overwritten.** When the resolved cluster already holds a different UID, the snapshot belongs to another cluster sharing the CA: it moves to the cluster holding its own UID (or a new one) and takes the sender's candidate row and the nodes it lists, and the first cluster is left untouched. This is how two clusters grouped by one CA are told apart.
- **Merges.** Another cluster holding the UID is merged in. So is every UID-less cluster that is fingerprint-related (it holds the snapshot's fingerprint, or one of its nodes reported it within the last hour) and has a node the snapshot lists by name; a name alone never merges, because default names such as `ubuntu` collide across clusters. `merge` keeps the lower id, which takes the other's name only if it has none, and keeps the newer Lease state of the two (newer epoch, then larger transition count, then newer last-accepted markers) together with the reporter that sent it. Nodes and candidates move to the survivor, and a node name both hold combines into one row: fields only the machine reports come from the row with the newer local report, fields only the reporter reports come from the row with the newer sighting, the kubelet version comes from the row updated last unless that row has none (then the other row's is kept), and the machine link comes from whichever row has one (the other row is deleted first, so the unique index over the link holds). The other cluster is deleted before the survivor takes its UID, so the partial unique index over (tenant, UID) never sees two holders.
- **Fencing is per Lease epoch.** Uninstall deletes the Lease, so a re-enabled cluster restarts `leaseTransitions` at 0. The survivor ignores a snapshot from an older Lease creation time; within one epoch it ignores a lower transition count, and an equal count unless (`ServerReceivedAt`, `Id`) is strictly newer than the last accepted snapshot's. A newer epoch resets the comparison. A fenced snapshot writes nothing but the cursor.
- **Accepting.** The cluster records the Lease state, the snapshot's UID and fingerprint (recomputing every node's mismatch flag), and the sender as the active reporter, and the sender becomes a candidate whose `LastReportAt` only ever moves forward, so a local report received after the snapshot is not taken for an older one. An error snapshot records its error on the cluster and the candidate and changes no node and none of the last reported details; a normal one clears them and sets the cluster version, `RoleOutdated` and `Truncated`, and moves the cluster's `LastSeenAt` forward to the snapshot's receipt time (never back, and an error snapshot leaves it).
- **Nodes.** Every listed node is upserted with the reporter's fields and `LastReporterSeenAt`; its kubelet version is only taken when the node has had no local report within `StaleThreshold`. `UnschedulableSince` is dated by the first snapshot that shows the node unschedulable, kept while it stays so and cleared when it is schedulable. An unlinked node is linked to a machine by `/etc/machine-id` only when exactly one non-deleted machine of the tenant has that non-empty `SystemId` (ordinal, ignoring case), that machine is linked to no other node, and the snapshot lists no other node with the same machine id. A snapshot that is not truncated deletes the nodes it no longer lists, unless their machine has had a local report within `StaleThreshold`; that includes a snapshot that lists no nodes at all, which is deliberate and pinned by a test.

Garbage collection runs hourly, per tenant under the lock, and `KubernetesReportingPolicy.StaleThreshold` (90 minutes, defined once in the `database` project) is the only definition of a stale snapshot:
- **Nodes** are deleted only when they have had neither a local report nor a cluster reporter sighting for seven days, so a Vord outage or a paused holder never deletes agentless nodes. Candidates are deleted after seven days without a local report.
- **Reporting disabled** applies to a cluster with reporter state, no candidates and no snapshot newer than the threshold. A cluster still receiving snapshots is never reverted, however far its candidates' local reports lag. The revert deletes the cluster's nodes that no local report ever described, clears the reporter columns of the others, and clears the cluster's reporter columns and Lease epoch (`LeaseUid`, `LeaseCreatedAt`, `LeaseTransitions` back to 0), because enabling reporting again creates a new Lease.
- **Empty clusters**, with no nodes and no candidates, are deleted last.

Machine soft delete, under the same lock, unlinks the machine's node, deletes its candidate row and nulls `ActiveReporterMachineId` where it names the machine. Tenant purge deletes candidates, then nodes, then clusters, all before the machines they reference. Two clusters signed by one shared CA group as one until a cluster snapshot tells them apart.

**Cluster reporter commands are accepted only for eligible machines.** `k8s_reporter_enrol`, `k8s_reporter_unenrol` and `k8s_reporter_uninstall` (`KubernetesCommandTypes`) ride the existing signed command path and are on `RemoteCommandService`'s allowlist, which ignores case. Every command is therefore handed to the scoped `IKubernetesCommandEligibility` once the machine is loaded and the key is authorized (before the pause check), and the check lets any other type through untouched. A non-lowercase spelling of a Kubernetes type is refused with 400 "Invalid command type" because the agent matches exactly, so changing the case can never skip the check. All three types take no parameters (null, blank or `{}` only). Eligibility reads the machine's latest type-14 payload that the server accepted in the last seven days through `GetLatestKubernetesNodePayloadsAsync` (one single-type query, `DISTINCT ON` on Postgres; never the all-types `GetLatestTelemetryPerTypeAsync`). Recency and "latest" are decided by `ServerReceivedAt`, and the row id breaks a tie. `ReceivedAt` comes from the agent's clock and is used only to bound which partitions are read. The payload is then normalised with `KubernetesNodePayloadNormaliser`, so a report the projection would refuse counts as no report. Enrol and uninstall need `control_plane && admin_credential_present`; uninstall is deliberately no stricter than enrol so it can clean up an orphaned install whose server-side state was already collected. Unenrol needs a `KubernetesReporterCandidates` row (`GetCandidateForMachineAsync`) or a latest report with `reporter_enrolled`. `SubmitCommandAsync` also stores a blank `Params` as null for every command type, because a blank string is not valid for the JSON column, and the eligibility check comes before the pause and capability checks so an ineligible host gets its own 400 first.

Cluster reporting is open to every subscription tier, and the rules that make that safe are these:
- **The Team check is by command type, in the handler.** `CommandSendEndpoint` no longer carries the Team tag. After request validation, and only when `KubernetesCommandTypes.IsReporterCommand(req.CommandType)` is false, it resolves the tenant's subscription with `ISubscriptionService.GetSubscriptionForTenantAsync` and `SubscriptionPolicy.RequiresTeam`, exactly as `TeamSubscriptionPreProcessor` does (so a self-hosted deployment passes by the same route), and answers 403 "Remote commands require a Team subscription" (`CommandSendEndpoint.RequiresTeamMessage`). `IsReporterCommand` is an ordinal exact match on the three constants, so a non-lowercase spelling is not a reporter command and faces the Team check. The check moved out of the pre-processor because it needs the body, which means an invalid body now gets its 400 validation error whatever the tier. Authorising a signing key for a machine (`MachineAuthorizedKeyAddEndpoint`) has no Team gate either: an authorisation grants nothing on its own, and the per-type check on `/commands` still applies.
- **The signed payload must name the request's type and params.** Right after the signature check, `SubmitCommandAsync` parses the canonical payload (`CanonicalPayloadBinding`, by the agent's rules in `crypto/verify.go`) and answers 400 "Canonical payload does not match the request" unless its `command_type` equals `CommandType` ordinally and its `params` map equals the request's `Params` map, with null or blank equal to `{}`, absent `params` equal to `{}`, a repeated key or a case-variant of either field's name an error, and every `params` value a string. The Team check judges the request's type and the agent runs the signed one, so without this a tenant below Team could name a reporter command and sign something else. It also moves a params mismatch from the host, where the agent rejected the command, to send time. The id, tenant, machine, user, nonce and timestamps stay the agent's to bind.
- **Params are validated.** `CommandSendValidator` requires a non-blank `Params` to be a JSON object whose every value is a string, with no repeated key, or the request fails with "Params must be a JSON object of string values"; `CommandParameters` is the one definition of that shape. `GetPendingCommands` tolerates a stored row that is not that shape (written some other way): a value that is not a string goes to the agent as its raw JSON text, which the agent then rejects on binding, a `Params` that is not an object goes out empty, a warning names the command id, and the machine's other pending commands are still delivered.
- **Cleanup commands skip the capability check, but only on a host that enrolled.** `KubernetesCommandTypes.IsCleanupCommand` (unenrol and uninstall, ordinal and exact) exempts a command from "Remote commands are not enabled on this machine", so a host whose `allow_remote_commands` was switched off after it enrolled can still be cleaned up. Enrol still needs the capability. The service reads the capability bits once, before the eligibility check, and hands the eligibility check the same answer (`remoteCommandsEnabled`): uninstall on a host without the capability then needs the same evidence of enrolment unenrol always needs, a candidate row or a latest report with `reporter_enrolled`, and otherwise answers 400 "This host has remote commands disabled and never enrolled; enable remote commands to uninstall from it". The order of the other checks is unchanged: eligibility, then pause, then capability.

What still needs a manual cleanup is a cluster whose reporter can no longer be removed by command: a tenant whose subscription can no longer issue commands (canceled or read-only), a paused host (the agent does not poll while paused), tenant deletion, and a host whose state was lost. There, `kubectl delete` the objects labelled `app.kubernetes.io/managed-by=vord-agent` in `vord-system`, then the cluster-scoped `ClusterRole` and `ClusterRoleBinding` named `vord-reporter`, and delete `/var/lib/vord-agent/k8s-reporter.json` on each enrolled host so its agent stops using the token.

**Cluster reporting is derived when it is read and never stored.** `KubernetesReporting.Derive` tests in order: no candidate rows is `off`; an error on the cluster or on any candidate is `error`; `RoleOutdated` is `role_outdated`; no snapshot, or one strictly older than `KubernetesReportingPolicy.StaleThreshold`, is `stale`; anything else is `active`. `staleSince` is `LastSnapshotReceivedAt` whenever the cluster has candidates and the snapshot is stale, whatever else is wrong, because an error does not make node values fresher. `reporterError` is the cluster's own error, else the error of the first failing candidate by ordinal machine name (machine id breaks a tie), and is null exactly when the state is not `error`. Node counts are `True` (ready), `False` (not ready) and everything else, including nodes no snapshot has listed (unknown). A node's `kubeletSkew` is true when both versions read and the kubelet's minor version is more than three behind the cluster's or ahead of it. Cluster names treat a node as control-plane when its machine reported one or the reporter lists the `control-plane` or `master` role, so agentless control-plane nodes name the cluster too. The list reads clusters, nodes, candidates and machine names once each however many clusters there are. The cluster detail adds, through `KubernetesReporterMachineReader`, the candidates and the eligible machines. Eligible machines are found with one `GetLatestKubernetesNodePayloadsAsync` over the tenant's active machines (the command check's rule and seven-day window, `KubernetesCommandEligibility.CanEnrol`), and a machine is listed when it belongs to the cluster by node or candidate row or reports the cluster's CA fingerprint, which finds a k0s controller or a `--disable-agent` k3s server before its first enrol. Clusters of one tenant can share a CA, so a machine matched by fingerprint alone is dropped once one batched lookup (`GetMachineIdsLinkedToOtherClustersAsync`) shows it is already a node or candidate of a different cluster; a node or candidate of this cluster is always listed. Each carries `online` (the swept health, as everywhere), `isPaused` (the quota cutoff, fetched once; a paused host still heartbeats and gets a 409 from the command endpoint), `remoteCommandsEnabled` (`AgentCapabilities.RemoteCommands`, the single definition of capability bit 0; null when the Redis capability read failed, which is logged and counted on `vord.kubernetes.capability_read_failures` while the rest of the page is still served, and `KubernetesInventoryMetrics` is therefore registered in both the API server and the worker) and the ids of its authorized signing keys, each read for all machines at once. A machine that is a candidate but no node is `grouped` on its own page in the candidate's cluster, and the machine page reads its telemetry without type 15, so a holder's snapshot is never loaded for it.

**The agent verifies the exact bytes that were signed, and executes only what they say.** `Processor.Process` (`src/agent/internal/commands/processor.go`) never rebuilds the canonical payload from the fields the server delivers, because the web client and the server render the same timestamps differently (`toISOString()` against the .NET round-trip format) and the web omits `params` where the server stores null, so a rebuilt payload would differ byte for byte from the signed one and reject every command. After the ownership checks and the key lookup, in this order:
- **Signature first.** The Ed25519 signature is checked over the `CanonicalPayload` bytes exactly as delivered, before anything in them is read.
- **Strict parse.** `crypto.ParseCanonicalPayload` accepts a JSON object with exactly the nine known keys, each once. Unknown, duplicate and case-variant keys (which `encoding/json` would match anyway), trailing data, invalid UTF-8 and a missing key other than `params` are errors; an absent `params` is an empty map.
- **Semantic binding.** `BindsTo` requires every signed field to describe the delivered command: command id, type, machine, tenant, user and nonce exactly, both timestamps as the same instant, and `params` as maps with absent equal to empty. A command whose unsigned fields disagree with the signed ones is rejected as a canonical payload mismatch.
- **Execution.** The timestamp window (five minutes of age, and no more than that into the future) and the nonce are checked on the signed values, and the handler runs the signed `command_type` with the signed `params`, never the delivered copies, so nothing outside the signed bytes can influence what runs.

The web always signs `"params":{}` and sends `Params` null; the three reporter commands refuse any parameters.

**The cluster page signs reporter commands in the browser, one machine at a time.** `src/web/src/lib/commands/signedCommand.ts` is the only place the web signs a command, for the machine page and the cluster page alike. It finds the local key, matches the server key by public key, builds the canonical payload with `"params":{}` always present (the agent binds what it executes to the signed bytes and reads absent params as empty), signs it, and sends the request with `Params` null, never an empty string, because the server stores it in a jsonb column. `clusters/[clusterId]` plans every action with the pure functions in `utils/clusterReporting.ts`. For each machine, the first of the user's registered keys that is in the machine's `authorizedSigningKeyIds` signs. A machine is not sent a command when none of the user's keys is authorised (it is listed under "Your signing key is not authorised on these machines"), when it is paused (the server would answer 409) or when `remoteCommandsEnabled` is false; null means the capabilities could not be read, which is unknown rather than disabled, so it is still sent. Enable enrols every eligible machine that is not enrolled yet. Disable sends one `k8s_reporter_uninstall` through an online eligible machine (the holder first, then another candidate) and an unenrol to every other online candidate, and sends nothing when no machine can uninstall, because unenrolling alone would drop the hosts' tokens and leave the cluster objects behind. `disableBlock` makes the page disable Disable in exactly that case and list, per eligible machine, the first thing that keeps it from taking the uninstall (key not authorised, paused, remote commands disabled, then offline; the key is judged only once the keys have loaded and the user has some). Enable and the per-row Enrol and Unenrol still send to an offline host, with a note that it must receive the command within five minutes, because the agent refuses one signed longer ago than that (the command is signed valid for ten, but the agent's window is the tighter); Disable skips offline hosts. Update permissions enrols through an online candidate in preference to an offline holder. Re-enrol and Update permissions sign `k8s_reporter_enrol`. The results shown are submission results only (accepted, or the API's message); what the agent then did stays in the machine's command history. The API client reduces every 403 to a bare "Forbidden", so the Team-tier message never reaches the page and `describeSendError` words that case itself.

**The reporter's assumptions about the Kubernetes API are pinned by a real-cluster test.** `src/agent/internal/kubereporter/integration_k3s_test.go` (build tag `k3s_integration`, run with `make -C src/agent test-k3s`; it needs Podman and skips when `podman` is not on the PATH) starts a k3s container and walks two in-process candidates through enrol, election on a fake clock, snapshot, failover without a release, release on a paused gate, a revoked token, uninstall and re-enrol into a new Lease epoch. The test is split across files that all carry the tag: `integration_k3s_test.go` holds `TestK3sIntegration` and the scenario phases, `integration_k3s_probes_test.go` the API-contract probes, `integration_k3s_harness_test.go` the `k3sCluster` harness, `integration_k3s_candidate_test.go` the candidate driver, `snapshotQueue` and `fakeClock`, and `integration_k3s_util_test.go` the utilities. It also records what the real API answers, which the in-memory fake in the unit tests (`fakeapi_test.go`) must keep imitating:
- A Lease PUT from a stale `resourceVersion` is 409 with reason `Conflict`. So is a PUT of an object that was deleted after it was read, because the body carries its uid and `resourceVersion`: that is a Conflict and not a 404, while a GET of the missing Lease is 404 (the Role names the Lease, so RBAC lets the reporter ask). Any other Lease name, or a list, is 403.
- A DELETE whose `preconditions.uid` is not the object's is 409 `Conflict` (checked on a Namespace, a Secret, a Lease and a ClusterRole), and a matching one is 200 (uninstall's own deletes use it on every kind it removes). A DELETE of a namespace that is already terminating is 200 with the namespace body, so a repeated uninstall finishes.
- A POST into a terminating namespace is 403 `Forbidden` with a `NamespaceTerminating` cause, which `IsNamespaceTerminating` recognises and `IsAuthRejected` does not count as a rejected credential.
- A token whose Secret was deleted is 401 `Unauthorized`, but the API server caches successful authentications, so it keeps working for several seconds, and once the roles are deleted too those calls are 403. Both are `credentials_rejected`.

**Internal gRPC is mutual TLS on its own port — never on the agent port.** `BillingGatewayService` and `FleetAdminService` are reached only over the `InternalGrpc` endpoint (default 12236), a Kestrel listener bound in `Startup/InternalGrpcEndpoint.cs` with `ClientCertificateMode.RequireCertificate` and a chain check against the internal CA alone (`X509ChainTrustMode.CustomRootTrust` — the machine trust store is deliberately not consulted). The agent endpoint (12234) must keep its current plain configuration forever: agents authenticate with an API key, have no client certificate, and requiring one there would refuse the entire fleet. Transport validity is not the authorisation decision — `CertificateSubjectAuthorizer` additionally matches the caller's certificate against `InternalGrpc:AllowedClientSubjects` (full subject DN, common name, or any DNS SAN; exact and case-insensitive). Without that list every certificate the internal CA ever issued would be accepted, which is no stronger than the shared secret this replaced, so `InternalGrpcOptionsValidator` refuses to start an enabled-but-subjectless endpoint. Both services are mapped only when `Deployment:SelfHosted` is false, and `DeploymentOptionsValidator` refuses to start a self-hosted deployment that has `InternalGrpc:Enabled` set, since the endpoint would then serve nothing. When `InternalGrpc:Enabled` is off they still fail closed because no caller on a plain-text port can present a certificate. Outbound calls to billing-api work the same way in reverse: `Billing:ClientCertificatePath` / `ClientCertificateKeyPath` / `ServerCaPath` feed `MutualTlsHandlerFactory`, so this process's identity is a mounted file, never a header or an embedded secret. There is no shared internal key anywhere in the system; do not reintroduce one.

**Antiforgery (CSRF):** ASP.NET Core antiforgery is registered globally (`AntiforgeryStartup.ConfigureOptions`) and **every state-changing FastEndpoints endpoint** (verb other than GET/HEAD/OPTIONS) is automatically opted in via the FE `Endpoints.Configurator`. The middleware enforces on form-encoded and multipart content types only; JSON requests are unaffected. The runtime skip predicate (`AntiforgeryStartup.ShouldSkipAntiforgery`) bypasses the check when the request did not carry the auth cookie (API-key callers etc.). To opt a specific endpoint out — for example a webhook that authenticates via HMAC signature — attach `[SkipAntiforgery]` to the endpoint class AND add its full type name to `AntiforgeryOptOutAllowlist.Entries`. A regression test (`AntiforgeryEnrollmentRegressionTests`) fails if those two diverge, so every opt-out is a deliberate, reviewable change.

**The email transport follows the deployment mode:** Resend when `Deployment:SelfHosted` is false, SMTP when it is true. In the hosted deployment email is not optional — `EmailOptionsValidator` fails startup when `Email:Resend:ApiKey` or `Email:FromEmail` is missing, since that deployment always sends invitations and alerts and Resend rejects sends from an unverified address. In a self-hosted deployment email genuinely is optional: an empty `Email:Smtp:Host` selects `NoOpEmailService` and every send reports `Skipped`, while a configured host requires `Email:FromEmail` and a port in range. An absent API key no longer implies self-hosted — the flag decides that, and nothing else may infer the mode from a credential. Every `IEmailService` method returns `EmailDeliveryOutcome` with three states — `Sent`, `Skipped`, `Failed` — instead of throwing or returning bool. `Skipped` means no provider is configured and is terminal success: callers must never retry it or record it as a delivery failure. Any new `IEmailService` consumer must switch on all three states explicitly rather than treating non-`Sent` as an error.

## Releases

Trunk-based: `main` is the only long-lived branch. There is no `prod` branch — releases are cut by
pushing a **tag**, and `.github/workflows/prod.yaml` derives the version from the tag name. Nothing
reads `<Version>` out of a csproj at release time any more, so a published artifact can never
disagree with the ref that built it.

| Tag | Builds and publishes | Does NOT touch |
| --- | --- | --- |
| `agent-v<semver>` (e.g. `agent-v2.9.0`) | Go agent for linux amd64/arm64/386/armv7 — tarball artifacts plus deb/rpm packaged by nfpm and pushed to Gemfury. | Any container |
| `server-v<semver>` (e.g. `server-v2.9.0`) | `api-server`, `migration_runner`, `services-worker` (via `dotnet publish /t:PublishContainer -p:Version=…`) and the `web` container, each tagged `<semver>` and `latest`. Runs the full .NET unit/functional/integration suites first. | The agent |
| `billinggrpc-v<semver>` (e.g. `billinggrpc-v1.18.0`) | `Framlux.Vord.BillingGrpc` — the internal control-plane contract in `src/billing-grpc/protos` — packed with `-p:Version` from the tag and pushed to nuget.org (skipped with a warning while `NUGET_ORG_API_KEY` is unset) and to GitHub Packages. | Any container, the agent, or the agent contracts in `src/grpc` |

There is no tag prefix for the agent contracts in `src/grpc`, and there must not be one: nothing
outside this repository consumes them as a package. Published `Framlux.Vord.BillingGrpc` versions are
immutable, so read the highest existing tag before choosing the next one — `git tag -l 'billinggrpc-v*' | sort -V | tail -1`
— rather than trusting a number written here, which rots the moment someone releases without editing this line.

**Agent and server versions are independent.** The agent used to inherit `src/server/server.csproj`'s
`<Version>`; it no longer does. Bump either on its own cadence. The two version lines share a
starting point only because the agent's first independent tag must not regress below the last
version it shipped under the old lockstep scheme (package managers refuse a "downgrade").

Cutting a release:

```bash
git switch main && git pull            # release from main only; the tag must point at a green commit
git tag agent-v2.9.0                   # or server-v2.9.0
git push origin agent-v2.9.0
```

- Use plain `MAJOR.MINOR.PATCH`. The workflow rejects a tag that lacks a known prefix or a semver.
  Pre-release suffixes parse, but avoid them for `agent-v*` — deb/rpm version ordering for
  pre-releases is not what you would expect.
- The csproj `<Version>` elements are now only a local-dev default; the container tag comes from the
  git tag. Keep them roughly in step for sanity, but they are not the release source of truth.
- The pre-existing bare `v*` tags (`v2.8.5` and earlier) belong to the old scheme and trigger
  nothing. Leave them alone; do not reuse the bare prefix.
- Deployment is still a separate, deliberate step: ArgoCD syncs the `framlux/stack` repo's `main`
  branch, where each image tag is pinned explicitly. Publishing an image does not deploy it —
  bump the tag in `stack/clusters/prod/apps/vord-platform/base/**` to roll it out.
- Locally, `make -C src/agent build` derives its version from `git describe --tags --match 'agent-v*'`,
  so it only ever sees agent tags, never `server-v*` or the old `v*`.

## Planning Rules
- Before writing a plan or implementation, always check what work has ALREADY been completed in the codebase. Diff against recent commits and existing file state. Never include already-done items in plans.
- Any time you are given high level design, architecture, or planning direction that is wide-ranging in impact, document it in your Claude.md file for memory
- Any architectural, code-quality, product, or process changes must be documented in Claude.md

## Workflow Rules

- When modifying code, always run the full build and test suite before reporting completion. Verify: Go (`go build ./...` && `go test ./...`), .NET (`dotnet build machine-info.slnx` plus the test executables listed under Testing — **never** `dotnet test`, which does not run these TUnit projects), SvelteKit (`npm run build`). Never report 'done' with a plan — confirm green builds.
- When encountering transient API errors (500s), automatically retry the operation without waiting for user prompting. Do not stop and ask — just resume.
- Before starting implementation, check what work has already been completed in the codebase. Do not include already-done items in plans. If resuming a multi-session effort, diff against current state first.
- Before fixing this bug, write a failing test that reproduces the exact behavior I'm seeing. Then fix the code to make the test pass. Then run the full test suite.


## Frontend
- For SvelteKit: Always use SvelteKit's `fetch` (from `load` functions or `event.fetch`), never Node's native `fetch`. SvelteKit fetch handles cookies, relative URLs, and SSR proxy automatically.
- Use Svelte 5 reactivity patterns ($derived, $state) not Svelte 4 patterns.
- For Skeleton UI v3, verify import paths and dark mode uses `:where(.dark, .dark *)` selector.
- When working with Svelte 5, use `$derived` for reactive state and avoid Svelte 4 patterns. When working with Skeleton UI dark mode, use `&:where(.dark, .dark *)` not `&.dark`.

## .NET / C#
- For symbol lookup — verifying callers, finding definitions/implementations, rename/removal impact — use the **csharp-lsp** (the `LSP` tool: `findReferences`, `goToDefinition`, `incomingCalls`, `goToImplementation`, `workspaceSymbol`), not `grep`. LSP resolves the actual symbol, so it avoids text-match footguns like a singular method name matching its plural (`GetMachineIdsForRuleAsync` vs `GetMachineIdsForRulesAsync`) or a prefix collision (`GetUserByExternalIdAsync` vs `GetUserByExternalIdForProviderAsync`). Reserve `grep` for text/comment/config sweeps where there is no symbol to resolve.
- For .NET/FastEndpoints: Use `Send.NotFoundAsync` not `SendNotFoundAsync`.
- Verify NuGet package versions match what's in the .csproj.
- For LinqToDB, check async extension imports and IUpdatable API signatures. DatabaseContext properties may require `this.` qualifier — do not remove it.
- When editing .NET code, always verify: correct FastEndpoints method names (Send.NotFoundAsync not SendNotFoundAsync), LinqToDB async extension imports, and NuGet package version compatibility before declaring build success.

## Go
- For Go libraries, prefer BurntSushi/toml for TOML parsing. When modifying database code, always run tests against the actual test DB setup (e.g., :memory: SQLite) to catch OS-level issues.

<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:970c3bf2 -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules (this repo's policy — authoritative)

- Use `bd` as the durable source of truth for this repo's work items — features, bugs, decisions, todos, and progress. Transient per-run task lists during a single plan execution (e.g. the subagent-driven-development controller's TodoWrite / `.superpowers` ledger) are fine, but anything that should survive the session or travel across machines goes in `bd`.
- **Repo/project-specific** knowledge and learnings → `bd` (via `bd remember` / issues). **Global, cross-project** user preferences (e.g. commit-attribution rules, review-model choice, test conventions) stay in the user-level `~/.claude` MEMORY.md — do NOT move those into this repo's beads.
- Run `bd prime` for detailed command reference and session close protocol.

**Architecture in one line:** issues live in a local Dolt DB; sync uses `refs/dolt/data` on your git remote; `.beads/issues.jsonl` is a passive export. See https://github.com/gastownhall/beads/blob/main/docs/SYNC_CONCEPTS.md for details and anti-patterns.

## Agent Context Profiles

The managed Beads block is task-tracking guidance, not permission to override repository, user, or orchestrator instructions.

- **Conservative (default)**: Use `bd` for task tracking. Do not run git commits, git pushes, or Dolt remote sync unless explicitly asked. At handoff, report changed files, validation, and suggested next commands.
- **Minimal**: Keep tool instruction files as pointers to `bd prime`; use the same conservative git policy unless active instructions say otherwise.
- **Team-maintainer**: Only when the repository explicitly opts in, agents may close beads, run quality gates, commit, and push as part of session close. A current "do not commit" or "do not push" instruction still wins.

## Session Completion

This protocol applies when ending a Beads implementation workflow. It is subordinate to explicit user, repository, and orchestrator instructions.

1. **File issues for remaining work** - Create beads for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **Handle git/sync by active profile**:
   ```bash
   # Conservative/minimal/default: report status and proposed commands; wait for approval.
   git status

   # Team-maintainer opt-in only, unless current instructions forbid it:
   git pull --rebase
   bd dolt push
   git push
   git status
   ```
5. **Hand off** - Summarize changes, validation, issue status, and any blocked sync/commit/push step

**Critical rules:**
- Explicit user or orchestrator instructions override this Beads block.
- Do not commit or push without clear authority from the active profile or the current user request.
- If a required sync or push is blocked, stop and report the exact command and error.
<!-- END BEADS INTEGRATION -->
