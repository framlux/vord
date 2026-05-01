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

Uses **TUnit** (not xUnit/NUnit). Tests run as an executable NOT using the `dotnet test` command:

```bash
# Unit tests
dotnet run --project test/unit/unit.csproj

# Functional tests (full HTTP pipeline with in-memory SQLite)
dotnet run --project test/functional/functional.csproj
```

- When adding or changing service dependencies, always update ALL test files that construct or mock the modified service. - Check for missing mock parameters, new interface dependencies, and model initializations before running tests.
- All new code must have unit tests and functional tests written for it
- Before completing any new coding work, please run all tests and verify code coverage is about 75% for all new code
- All new code must have unit and functional tests written, where possible
- Tests must adhere to FIRST principles and *must* test for intent and *never* simply exercise code or to increase test numbers
- Tests must test both happy-paths as well as error cases, parameter ranges (both valid ranges and invalid ranges), and null inputs.

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
- `grpc` — Shared: Protobuf definitions (`src/grpc/protos/`) and generated code

**Data flow:** Agent → gRPC → Server (control plane); Agent → gRPC → Server → PostgreSQL

**Auth:** API Key scheme for agents, OIDC/OAuth for web users (GitHub, Google, Microsoft social login; per-tenant custom OIDC for Team tier). Role-based: Admin, TenantAdmin, MachineAdmin, Viewer.

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
- Only one class or record or enum or struct per file (except for auto-generated files). Endpoint Request/Response types may be co-located in the same file as their Endpoint class, following the FastEndpoints convention.
- Commends should always be written in natural language and should never include prompt information, plan numbers (ex: H1), and should always be descriptive on the intent of what the code is doing
- Never had "Yoda statements", which are things like if (false == some_expression), always put the variable before the constant
- All files should be formatted with spaces not tabs
- All .NET `using` statements must be in alphabetical order
- Any timestamps should be serialized as ISO8601

**Namespaces:** `Framlux.FleetManagement.{Server|Agent|Web}`

**Database:** LinqToDB (not EF Core). Models use `[Table]`, `[Column]`, `[PrimaryKey]` attributes. Access via domain-specific repository interfaces (e.g., `IMachineRepository`, `IAuditLogRepository`) — never inject `DatabaseContext` directly in server-side code. `DatabaseContext` is only used within repository implementations in the `database` project and for DI registration in `Program.cs`. Do not use composite/aggregate repository interfaces. If a constructor has 6+ repository dependencies, consider whether the class has too many responsibilities. Migrations use FluentMigrator.

**API Endpoints:** FastEndpoints pattern — inherit `Endpoint<TReq, TRes>`, configure route/auth/version in `Configure()`, implement `HandleAsync()`. Versioned routes: `/v{n}/api/{resource}`.

**Billing:** Stripe integration for subscription management (checkout, webhooks, customer portal). Tiers: Free, Pro, Team.

**Infrastructure:** PostgreSQL, Redis, Docker containers published to GHCR (`ghcr.io/framlux/fleet/*`). Logging via Serilog.


## Planning Rules
- Before writing a plan or implementation, always check what work has ALREADY been completed in the codebase. Diff against recent commits and existing file state. Never include already-done items in plans.
- Any time you are given high level design, architecture, or planning direction that is wide-ranging in impact, document it in your Claude.md file for memory
- Any architectural, code-quality, product, or process changes must be documented in Claude.md

## Workflow Rules

- When modifying code, always run the full build and test suite before reporting completion. Verify: Go (`go build ./...` && `go test ./...`), .NET (`dotnet build` && `dotnet test`), SvelteKit (`npm run build`). Never report 'done' with a plan — confirm green builds.
- When encountering transient API errors (500s), automatically retry the operation without waiting for user prompting. Do not stop and ask — just resume.
- Before starting implementation, check what work has already been completed in the codebase. Do not include already-done items in plans. If resuming a multi-session effort, diff against current state first.


## Frontend
- For SvelteKit: Always use SvelteKit's `fetch` (from `load` functions or `event.fetch`), never Node's native `fetch`. SvelteKit fetch handles cookies, relative URLs, and SSR proxy automatically.
- Use Svelte 5 reactivity patterns ($derived, $state) not Svelte 4 patterns.
- For Skeleton UI v3, verify import paths and dark mode uses `:where(.dark, .dark *)` selector.
- When working with Svelte 5, use `$derived` for reactive state and avoid Svelte 4 patterns. When working with Skeleton UI dark mode, use `&:where(.dark, .dark *)` not `&.dark`.

## .NET / C#
- For .NET/FastEndpoints: Use `Send.NotFoundAsync` not `SendNotFoundAsync`.
- Verify NuGet package versions match what's in the .csproj.
- For LinqToDB, check async extension imports and IUpdatable API signatures. DatabaseContext properties may require `this.` qualifier — do not remove it.
- When editing .NET code, always verify: correct FastEndpoints method names (Send.NotFoundAsync not SendNotFoundAsync), LinqToDB async extension imports, and NuGet package version compatibility before declaring build success.

## Go
- For Go libraries, prefer BurntSushi/toml for TOML parsing. When modifying database code, always run tests against the actual test DB setup (e.g., :memory: SQLite) to catch OS-level issues.