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

# Web frontend (SvelteKit / Vitest)
pnpm -C src/web test      # also: pnpm -C src/web check  /  pnpm -C src/web build

# Target a subset of any TUnit project with: --treenode-filter "*SomeTestName*"

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

**`Deployment:SelfHosted` is the single mode switch.** It defaults to `true` so a fresh clone runs with no configuration; the hosted deployment sets it to `false`. It alone decides the billing client (real versus `NoOpBillingApiClient`), whether `BillingGatewayService` and `FleetAdminService` are mapped, whether `StripeSyncJob` is registered, the email transport, and whether entitlement limits apply. `Billing:Enabled` was deleted — a second switch is exactly the drift this replaced. In self-hosted, `SelfHostedSubscriptionService` decorates `ISubscriptionService` and answers every entitlement question permissively; it must implement **all twelve** interface members, because a delegated member silently reimposes a Free-tier limit that the interface does not reflect. Retention is the member most easily missed: it does not flow through `EffectiveLimits`, and it is capped at `RetentionClassPolicy.LongWindowDays`, not unlimited, because there is no unlimited retention class.

**Data flow:** Agent → gRPC → Server (control plane); Agent → gRPC → Server → PostgreSQL

**Auth:** API Key scheme for agents, OIDC/OAuth for web users (GitHub, Google, Microsoft social login; per-tenant custom OIDC for Team tier). Role-based: Admin, TenantAdmin, MachineAdmin, Viewer.

**Tenant deactivation is enforced within one request, not one TTL.** New logins, tenant-switch, and telemetry ingest block immediately on the live `Tenants.IsActive` check, but an already-open browser session carries its tenant role claim in the auth cookie. So `TenantDeletionHandler` — on both `RequestDeletionAsync` and `RestoreAsync`, **after commit** — evicts every tenant member's cached role claims via `IRoleCacheInvalidator`; `CookiePrincipalValidator` then rebuilds claims from `GetTenantsForUserAsync`, which filters on `Tenants.IsActive`. The eviction is best-effort (a Redis failure is logged, never fails the deletion) with the ≤5-minute claim-refresh TTL as the backstop. Any future path that flips a tenant's active flag must do the same, or it silently reverts to TTL-bounded enforcement.

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

**Billing:** Stripe integration for subscription management (checkout, webhooks, customer portal). Tiers: Free, Pro, Team.

**Infrastructure:** PostgreSQL, Redis, Docker containers published to GHCR (`ghcr.io/framlux/fleet/*`). Logging via Serilog.

**Server configuration settings (replica-safe cache):** admin-facing settings (`ServerConfigurationSettings`) are read through two layers: a shared Redis read-through in `ServerConfigurationService` (`config:{key}`, 5-min TTL) and Postgres as the source of truth. There is deliberately **no per-process in-memory layer** — a third layer was removed once it was found to have no production readers, and reintroducing one would put a replica-local copy behind the shared cache that nothing can invalidate promptly. Redis misses fall through to `IServerSettingsReader.GetSettingFromDatabaseAsync` (in the `database` project), which always reads the database, so a just-invalidated key is never re-seeded stale. Every write path — REST `AdminHandler` and gRPC `FleetAdminService` — must, **after commit**, call `ServerSettingsInvalidation.InvalidateAsync`, which deletes the Redis key; it is best-effort, with the Redis TTL as the correctness backstop. Both write paths validate through the single `ServerSettingValidation` helper. The `database` project takes no Redis dependency.

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
outside this repository consumes them as a package. `Framlux.Vord.BillingGrpc` 1.17.0 is already
published, so the next control-plane release is `billinggrpc-v1.18.0` or above.

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

- When modifying code, always run the full build and test suite before reporting completion. Verify: Go (`go build ./...` && `go test ./...`), .NET (`dotnet build` && `dotnet test`), SvelteKit (`npm run build`). Never report 'done' with a plan — confirm green builds.
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
