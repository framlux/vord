# Stripe Billing API Commit-Readiness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the month-old uncommitted Stripe billing-api working set in `/Users/jonathanmiller/Repositories/framlux/vord-internal` (14 modified + 6 new files, currently builds 0-warning, 474 pass / 13 skip) as seven clean per-theme commits, after making the remaining fixes required by the 2026-07-20 product rulings: audit rows on every webhook processing attempt, honest at-least-once comments, checkout empty-`CustomerId` race/crash repair, meter-identifier consistency at the third call site, rate-limit documentation + 429 test, `BillingInterval` kept current with an `interval_changed` audit event, and convention cleanups.

**Architecture:** All work is in `src/billing-api` (FastEndpoints REST + gRPC + LinqToDB/PostgreSQL, SQLite in tests) and `test/billing` (TUnit). Commit order follows the assessment §3 dependency order: T4 UniqueConstraintDetection → T7 CORS → T5 SessionValidator negative cache → T6 rate limiting → T2 meter identifiers → T1 webhook transactions (which also carries ruling 4's `BillingInterval`/`interval_changed` work, because that work lives inside `HandleSubscriptionUpdatedAsync`'s existing transaction — it belongs with T1, not T3) → T3 checkout claim-first rework. `Program.cs` and `Endpoints/CheckoutEndpoint.cs` contain hunks from multiple themes; those are staged per-theme with `git apply --cached` patches given verbatim below.

**Tech Stack:** .NET 10, FastEndpoints, LinqToDB, FluentMigrator, Stripe.net, TUnit + NSubstitute, `Microsoft.Extensions.TimeProvider.Testing` (FakeTimeProvider).

## Global Constraints

- Repo: `/Users/jonathanmiller/Repositories/framlux/vord-internal`, branch `main`. All paths below are relative to that root unless absolute.
- Build must stay 0 errors / 0 warnings: verify with `dotnet build vord-internal.slnx -c Release`.
- Tests run via TUnit with `dotnet run --project test/billing/billing.csproj -c Release` — never `dotnet test`. If results look stale, rebuild with `dotnet build test/billing/billing.csproj -c Release --no-incremental` and run the compiled executable `test/billing/bin/Release/net10.0/osx-arm64/billing` directly, with `--treenode-filter "/*/*/ClassName/*"` to scope to one class.
- Conventions (CLAUDE.md): license header on every file; no `var`; Allman braces; explicit `== false` (never `!x`); no Yoda conditions; parens around compound conditions; blank line before `return` (unless preceded by a comment); alphabetical-within-group `using`s matching sibling files (System first, as existing files do); XML doc comments on public members; one type per file; blank line at end of file; comments in natural language with no plan/task numbers.
- Commits: `git -c commit.gpgsign=false commit` — natural-language messages, NO AI attribution, NO Co-Authored-By footer. Stage exactly the files (or staged hunks) listed per task; run the full test suite green + 0-warning build before every commit.
- Ruling 1 (webhook semantics): retries stay harmlessly re-entrant — transactional structure and no-re-notify behavior STAY — but audit rows are written on EVERY processing attempt including reprocessed/duplicate events; move `LogAsync` back OUT of the `FleetNotifiedAt` gate while keeping everything transactional; fix the five comments that overstate "cannot re-notify" (real contract is at-least-once: crash after fleet-notify before commit re-notifies on retry).
- Ruling 2 (meter dedup): Stripe meter aggregation is "Last" — the hourly identifier bucket is correct as-is; keep it, document the Last-aggregation rationale in a code comment, wire `Identifier` into `SubscriptionCreateEndpoint`, and add an identifier assertion to `MigrationMeteredEndpointTests` (FakeTimeProvider injected via the factory).
- Ruling 3 (rate limiting): accepted as burst-shaping — document on the policy that the raw-cookie partition key is rotation-bypassable by design, forgeries die at session validation after one fleet round-trip bounded by the 10s negative cache, and flood defense is ingress's job; add a 429 integration test.
- Ruling 4 (BillingInterval): kept CURRENT — the subscription.updated handler derives the interval from the active price and updates `StripeCustomers.BillingInterval` inside the existing transaction; add an `interval_changed` audit event for same-tier interval swaps (which today write no dedicated audit row).
- Ruling 5 (checkout race): fix BOTH failure modes before landing T3 — (a) race loser must never send `Customer = ""` to Stripe: short poll for the winner's back-fill, then 503 + Retry-After if still empty; (b) a persisted claim row with empty `CustomerId` must self-repair on the next checkout (create customer, back-fill, proceed). Both paths get tests.
- Ruling 6 (CORS deploy gate): the new validator crash-loops any environment with empty `Cors:Origins`; kube manifests are NOT in this repo — the final task's completion summary must tell Jonathan to verify prod config before deploying.
- TDD where a fix has a testable failure mode: failing test first for the checkout race paths, the audit-every-attempt revert, the `interval_changed` audit, and the 429 test (the 429 code already exists, so that test is expected to pass on first run — run it before staging to confirm).

---

### Task 1: T4 — Structured unique-constraint detection

**Files:**
- Modify: `test/billing/Database/UniqueConstraintDetectionTests.cs` (namespace, line 8; XML summaries on the 5 tests, lines 34–75)
- Commit (already-written WIP, no further edits): `src/billing-api/Database/UniqueConstraintDetection.cs` (new), `src/billing-api/Endpoints/WebhookEndpoint.cs`
- Test: `test/billing/Database/UniqueConstraintDetectionTests.cs`

**Interfaces:**
- Produces: `internal static class UniqueConstraintDetection` with `public static bool IsUniqueConstraintViolation(Exception ex)` (namespace `Framlux.Billing.Api.Database`)
- Consumes: `Npgsql.PostgresException.SqlState`
- Note: `CheckoutEndpoint.cs` also calls this helper, but its whole rework lands in Task 7; only `WebhookEndpoint.cs` is committed here.

- [ ] **Step 1:** Fix the test namespace so it matches the folder convention used by the sibling suites (`Tests.Auth`, `Tests.Configuration`). In `test/billing/Database/UniqueConstraintDetectionTests.cs` replace:
  ```csharp
  namespace Framlux.Billing.Api.Tests.DatabaseTests;
  ```
  with:
  ```csharp
  namespace Framlux.Billing.Api.Tests.Database;
  ```

- [ ] **Step 2:** Add XML doc summaries to the five test methods (the sibling WIP suite `WebhookProcessorServiceTests` documents its new tests; these are the only undocumented ones in this file). Insert directly above each `[Test]` attribute:

  Above `PostgresUniqueViolation_SqlState23505_IsDetected`:
  ```csharp
      /// <summary>
      /// A PostgresException with SqlState 23505 is detected as a unique-constraint violation.
      /// </summary>
  ```
  Above `PostgresUniqueViolation_WrappedInInnerException_IsDetected`:
  ```csharp
      /// <summary>
      /// A unique violation wrapped by an outer exception is still detected by walking the
      /// inner-exception chain.
      /// </summary>
  ```
  Above `PostgresNonUniqueViolation_DifferentSqlState_IsNotDetected`:
  ```csharp
      /// <summary>
      /// A not-null violation (SqlState 23502) is not misclassified as a unique-constraint violation.
      /// </summary>
  ```
  Above `MessageMentioning23505ButNotAUniqueViolation_IsNotDetected`:
  ```csharp
      /// <summary>
      /// A message that merely contains the text "23505" without the SqlState does not trigger detection.
      /// </summary>
  ```
  Above `SqliteUniqueMessage_IsDetected`:
  ```csharp
      /// <summary>
      /// The SQLite unique-violation message used by the in-memory test database is detected.
      /// </summary>
  ```

- [ ] **Step 3:** Verify build and full suite:
  ```bash
  dotnet build vord-internal.slnx -c Release
  dotnet run --project test/billing/billing.csproj -c Release
  ```
  Expected: build succeeds with 0 warnings; tests report 474 passed / 13 skipped / 0 failed.

- [ ] **Step 4:** Commit exactly this theme's files:
  ```bash
  cd /Users/jonathanmiller/Repositories/framlux/vord-internal
  git add src/billing-api/Database/UniqueConstraintDetection.cs \
          src/billing-api/Endpoints/WebhookEndpoint.cs \
          test/billing/Database/UniqueConstraintDetectionTests.cs
  git -c commit.gpgsign=false commit \
    -m "Detect unique-constraint violations via structured Postgres SqlState" \
    -m "Replace the webhook endpoint's message-substring check with a shared UniqueConstraintDetection helper that inspects PostgresException.SqlState 23505, walks the inner-exception chain, and falls back to the SQLite message used by the in-memory test database. Substring matching could both miss driver-wrapped violations and false-positive on unrelated text containing 23505. The checkout endpoint adopts the same helper in its upcoming claim-first rework."
  ```
  Verify with `git show --stat HEAD` that exactly the three files above are in the commit.

---

### Task 2: T7 — CORS startup fail-fast validation

**Files:**
- Modify: `test/billing/Configuration/CorsConfigurationValidatorTests.cs` (XML summaries on the 5 tests, lines 14–96)
- Commit (already-written WIP): `src/billing-api/Configuration/CorsConfigurationValidator.cs` (new), `test/billing/Infrastructure/BillingFunctionalTestFactory.cs` (the `Cors__Origins__0` env var, line 98), and ONLY the CORS hunk of `src/billing-api/Program.cs` (lines 42–45 of the working tree) via `git apply --cached`
- Test: `test/billing/Configuration/CorsConfigurationValidatorTests.cs`

**Interfaces:**
- Produces: `public static class CorsConfigurationValidator` with `public static void Validate(IReadOnlyList<string> origins, bool allowCredentials)` (throws `InvalidOperationException`)
- Consumes: `builder.Configuration.GetSection("Cors:Origins").Get<string[]>()` in `Program.cs`

- [ ] **Step 1:** Add XML doc summaries to the five test methods in `test/billing/Configuration/CorsConfigurationValidatorTests.cs`, directly above each `[Test]`:

  Above `Validate_EmptyOriginsWithCredentials_Throws`:
  ```csharp
      /// <summary>
      /// An empty origin list with credentials enabled fails startup validation.
      /// </summary>
  ```
  Above `Validate_WildcardOriginWithCredentials_Throws`:
  ```csharp
      /// <summary>
      /// A bare wildcard origin with credentials enabled fails startup validation.
      /// </summary>
  ```
  Above `Validate_WildcardSubdomainWithCredentials_Throws`:
  ```csharp
      /// <summary>
      /// A wildcard-subdomain origin with credentials enabled fails startup validation.
      /// </summary>
  ```
  Above `Validate_ExplicitOriginsWithCredentials_DoesNotThrow`:
  ```csharp
      /// <summary>
      /// Explicit wildcard-free origins with credentials enabled pass validation.
      /// </summary>
  ```
  Above `Validate_EmptyOriginsWithoutCredentials_DoesNotThrow`:
  ```csharp
      /// <summary>
      /// Validation is skipped entirely when the policy does not allow credentials.
      /// </summary>
  ```

- [ ] **Step 2:** Verify build and full suite (same commands as Task 1 Step 3). Expected: 0 warnings, 474 / 13 / 0.

- [ ] **Step 3:** Stage the theme. `Program.cs` also carries T6 and T2 hunks that must NOT enter this commit, so stage only its CORS hunk from stdin (the index currently holds the HEAD version, so this patch applies against HEAD):
  ```bash
  cd /Users/jonathanmiller/Repositories/framlux/vord-internal
  git add src/billing-api/Configuration/CorsConfigurationValidator.cs \
          test/billing/Configuration/CorsConfigurationValidatorTests.cs \
          test/billing/Infrastructure/BillingFunctionalTestFactory.cs
  git apply --cached <<'EOF'
  diff --git a/src/billing-api/Program.cs b/src/billing-api/Program.cs
  --- a/src/billing-api/Program.cs
  +++ b/src/billing-api/Program.cs
  @@ -38,8 +38,10 @@ builder.Host.UseSerilog((context, configuration) =>
           .MinimumLevel.Override("LinqToDB", Serilog.Events.LogEventLevel.Warning)
           .WriteTo.Console(new RenderedCompactJsonFormatter()));
   
  -// CORS configuration for billing UI origin
  +// CORS configuration for billing UI origin. The policy allows credentials (cookies), so fail
  +// fast if the origin list is empty or contains a wildcard, both of which are invalid in that mode.
   string[] corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
  +CorsConfigurationValidator.Validate(corsOrigins, allowCredentials: true);
   builder.Services.AddCors(options => options.AddDefaultPolicy(policyBuilder => policyBuilder
       .WithOrigins(corsOrigins)
       .WithMethods("GET", "POST", "OPTIONS")
  EOF
  ```
  IMPORTANT (heredoc mechanics): the patch body above is shown indented for readability inside this plan; when writing the heredoc, the lines between `EOF` markers must start at column 0 exactly as a real `git diff` emits them (context lines start with one space, additions with `+`, removals with `-`). If `git apply` reports a corrupt patch, write the same content to a temp file with the Write tool (which preserves leading whitespace exactly) and run `git apply --cached <file>`.

- [ ] **Step 4:** Verify the staging is exact:
  ```bash
  git diff --cached --stat
  git diff --cached -- src/billing-api/Program.cs
  ```
  Expected: 4 files staged; the `Program.cs` staged diff shows ONLY the two-line comment replacement and the `CorsConfigurationValidator.Validate(...)` line — no rate-limiter or TimeProvider lines. `git diff -- src/billing-api/Program.cs` (unstaged) still shows the rate-limiter and TimeProvider hunks.

- [ ] **Step 5:** Commit:
  ```bash
  git -c commit.gpgsign=false commit \
    -m "Fail fast at startup when CORS origins are missing or wildcarded" \
    -m "The billing CORS policy allows credentials, so an empty origin list silently breaks the billing UI and a wildcard origin is both insecure and rejected by browsers. Validate the configured origins at startup and refuse to boot on either misconfiguration. The functional test factory now supplies a test origin so the API boots under test." \
    -m "Deploy note: any environment currently running with an empty or missing Cors:Origins will crash-loop on rollout of this commit; production configuration must be verified before deploying."
  ```
  Verify `git show --stat HEAD` lists exactly the 4 files.

---

### Task 3: T5 — Session-validation negative caching

**Files:**
- Modify: `test/billing/Auth/SessionValidatorTests.cs` (XML summaries on the 4 tests, lines 69–137)
- Commit (already-written WIP): `src/billing-api/Auth/SessionValidator.cs`
- Test: `test/billing/Auth/SessionValidatorTests.cs`

**Interfaces:**
- Consumes/Produces: `public async Task<ValidatedSession?> ValidateAsync(HttpRequest request, CancellationToken ct)` — unchanged contract (callers only observe `null`); the negative cache (`"invalid"` sentinel, 10s TTL) is fully internal.

- [ ] **Step 1:** Add XML doc summaries to the four test methods in `test/billing/Auth/SessionValidatorTests.cs`, directly above each `[Test]`:

  Above `ValidateAsync_RepeatedFailures_OnlyCallsFleetOnce_NegativeCache`:
  ```csharp
      /// <summary>
      /// Repeated failed validations for the same cookie pair collapse to a single fleet
      /// round-trip via the negative cache.
      /// </summary>
  ```
  Above `ValidateAsync_DifferentCookies_NotSharedNegativeCache`:
  ```csharp
      /// <summary>
      /// Negative cache entries are per cookie pair; distinct cookies each cost one round-trip.
      /// </summary>
  ```
  Above `ValidateAsync_SuccessfulValidation_CachesPositiveResult`:
  ```csharp
      /// <summary>
      /// A successful validation is cached so an immediate repeat does not re-hit the fleet.
      /// </summary>
  ```
  Above `ValidateAsync_MissingCookies_DoesNotCallFleet`:
  ```csharp
      /// <summary>
      /// Requests without both auth cookies short-circuit without any fleet call.
      /// </summary>
  ```

- [ ] **Step 2:** Verify build and full suite. Expected: 0 warnings, 474 / 13 / 0.

- [ ] **Step 3:** Commit:
  ```bash
  cd /Users/jonathanmiller/Repositories/framlux/vord-internal
  git add src/billing-api/Auth/SessionValidator.cs test/billing/Auth/SessionValidatorTests.cs
  git -c commit.gpgsign=false commit \
    -m "Cache failed session validations for ten seconds" \
    -m "Failed validations are now cached as a sentinel value with a short TTL, so a burst of unauthenticated requests for the same cookie pair costs one fleet round-trip instead of one per request. The sentinel cannot collide with real entries because positive entries are JSON objects. Callers are unaffected: they observe null exactly as before, just faster."
  ```
  Verify `git show --stat HEAD` lists exactly the 2 files.

---

### Task 4: T6 — Rate limiting for checkout/portal

**Files:**
- Modify: `src/billing-api/Program.cs` (replace the policy comment, lines 195–197 of the working tree)
- Create: `test/billing/Endpoints/RateLimitingTests.cs`
- Commit: `src/billing-api/Endpoints/PortalEndpoint.cs` (whole file — its only WIP change is the `RequireRateLimiting` line), `test/billing/Endpoints/RateLimitingTests.cs`, plus ONLY the rate-limiter hunks of `src/billing-api/Program.cs` and ONLY the `Configure()` hunk of `src/billing-api/Endpoints/CheckoutEndpoint.cs`, both via `git apply --cached`
- Test: `test/billing/Endpoints/RateLimitingTests.cs`

**Interfaces:**
- Produces: fixed-window policy `"CheckoutPortal"` (10/min, queue 0, 429 rejection) registered via `options.AddPolicy("CheckoutPortal", httpContext => ...)`; consumed by `Options(x => x.RequireRateLimiting("CheckoutPortal"))` in `CheckoutEndpoint.Configure()` and `PortalEndpoint.Configure()`.
- Partition key: `vord_tenant` cookie → `vord_auth` cookie → remote IP → `"anonymous"`.

- [ ] **Step 1 (ruling 3 — document burst-shaping):** In `src/billing-api/Program.cs`, replace the WIP policy comment:
  ```csharp
      // Per-session (per-tenant) limiter for the anonymous-by-session checkout and portal
      // endpoints. Partitioning on the auth/tenant cookie (falling back to remote IP) bounds how
      // many Stripe customer/checkout/portal operations a single session can trigger in a burst.
  ```
  with:
  ```csharp
      // Per-session limiter for the anonymous-by-session checkout and portal endpoints. This is
      // burst-shaping, not flood defense: the partition key is a raw, unvalidated cookie, so a
      // client that rotates cookie values mints fresh partitions at will. Unauthenticated
      // forgeries die at session validation after at most one fleet round-trip, bounded by the
      // validator's ten-second negative cache, and volumetric flood protection is the ingress
      // layer's job. The limiter bounds how many Stripe checkout and portal operations one
      // well-behaved session can trigger in a burst.
  ```

- [ ] **Step 2 (429 test):** Create `test/billing/Endpoints/RateLimitingTests.cs` with exactly:
  ```csharp
  // Copyright (c) 2026 Framlux LLC
  // Licensed under the Functional Source License, Version 1.1, ALv2 Future License
  // See LICENSE for details.

  using System.Net;
  using System.Net.Http.Json;
  using Framlux.Billing.Api.Endpoints;
  using Framlux.Billing.Api.Models;
  using Framlux.Billing.Api.Tests.Infrastructure;

  namespace Framlux.Billing.Api.Tests.Endpoints;

  /// <summary>
  /// HTTP integration tests for the CheckoutPortal rate-limit policy applied to the checkout
  /// and portal endpoints.
  /// </summary>
  public sealed class RateLimitingTests : IDisposable
  {
      private readonly BillingFunctionalTestFactory _factory;

      /// <summary>
      /// Creates the test fixture with an isolated billing API instance.
      /// </summary>
      public RateLimitingTests()
      {
          _factory = new BillingFunctionalTestFactory();
      }

      /// <inheritdoc/>
      public void Dispose()
      {
          _factory.Dispose();
      }

      /// <summary>
      /// The eleventh request in the same fixed window from the same partition is rejected with
      /// 429, and the checkout endpoint shares the same policy budget as the portal endpoint.
      /// Rate limiting runs before session validation, so unauthenticated requests consume
      /// permits and are otherwise answered with 401.
      /// </summary>
      [Test]
      public async Task PortalAndCheckout_EleventhRequestInWindow_Returns429()
      {
          using HttpClient client = _factory.CreateClient();

          for (int i = 0; i < 10; i++)
          {
              HttpResponseMessage allowed = await client.PostAsync("/api/v1/portal", null);
              await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
          }

          HttpResponseMessage rejected = await client.PostAsync("/api/v1/portal", null);
          await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);

          // The checkout endpoint uses the same policy and the same anonymous partition, so it
          // is throttled by the shared budget.
          HttpResponseMessage checkoutRejected = await client.PostAsJsonAsync(
              "/api/v1/checkout", new CheckoutRequest { Tier = BillingTier.Pro });
          await Assert.That(checkoutRejected.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
      }
  }
  ```

- [ ] **Step 3:** Run the new test (it exercises already-written WIP code, so it must pass immediately) and then the full suite + build:
  ```bash
  dotnet build test/billing/billing.csproj -c Release --no-incremental
  ./test/billing/bin/Release/net10.0/osx-arm64/billing --treenode-filter "/*/*/RateLimitingTests/*"
  dotnet build vord-internal.slnx -c Release
  dotnet run --project test/billing/billing.csproj -c Release
  ```
  Expected: RateLimitingTests 1 passed; solution builds 0 warnings; full suite 475 passed / 13 skipped / 0 failed.

- [ ] **Step 4:** Stage the theme. `CheckoutEndpoint.cs` also carries the T3 claim-first rework which must NOT enter this commit, and `Program.cs` still carries the T2 TimeProvider hunk; stage only this theme's hunks (the index holds the post-Task-2 state, i.e. `Program.cs` with the CORS hunk and `CheckoutEndpoint.cs` at HEAD; `git apply` will report harmless line offsets):
  ```bash
  cd /Users/jonathanmiller/Repositories/framlux/vord-internal
  git add src/billing-api/Endpoints/PortalEndpoint.cs test/billing/Endpoints/RateLimitingTests.cs
  git apply --cached <<'EOF'
  diff --git a/src/billing-api/Program.cs b/src/billing-api/Program.cs
  --- a/src/billing-api/Program.cs
  +++ b/src/billing-api/Program.cs
  @@ -21,6 +21,7 @@ using Microsoft.IdentityModel.Tokens;
   using Npgsql;
   using Serilog;
   using Serilog.Formatting.Compact;
  +using System.Threading.RateLimiting;
   
   WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
   builder.WebHost.ConfigureKestrel(options =>
  @@ -190,5 +191,27 @@ builder.Services.AddRateLimiter(options =>
           policy.PermitLimit = 5;
           policy.QueueLimit = 0;
       });
  +
  +    // Per-session limiter for the anonymous-by-session checkout and portal endpoints. This is
  +    // burst-shaping, not flood defense: the partition key is a raw, unvalidated cookie, so a
  +    // client that rotates cookie values mints fresh partitions at will. Unauthenticated
  +    // forgeries die at session validation after at most one fleet round-trip, bounded by the
  +    // validator's ten-second negative cache, and volumetric flood protection is the ingress
  +    // layer's job. The limiter bounds how many Stripe checkout and portal operations one
  +    // well-behaved session can trigger in a burst.
  +    options.AddPolicy("CheckoutPortal", httpContext =>
  +    {
  +        string partitionKey = httpContext.Request.Cookies["vord_tenant"]
  +            ?? httpContext.Request.Cookies["vord_auth"]
  +            ?? httpContext.Connection.RemoteIpAddress?.ToString()
  +            ?? "anonymous";
  +
  +        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
  +        {
  +            Window = TimeSpan.FromMinutes(1),
  +            PermitLimit = 10,
  +            QueueLimit = 0
  +        });
  +    });
   });
   builder.Services.AddFastEndpoints();
  EOF
  git apply --cached <<'EOF'
  diff --git a/src/billing-api/Endpoints/CheckoutEndpoint.cs b/src/billing-api/Endpoints/CheckoutEndpoint.cs
  --- a/src/billing-api/Endpoints/CheckoutEndpoint.cs
  +++ b/src/billing-api/Endpoints/CheckoutEndpoint.cs
  @@ -77,6 +77,7 @@ public sealed class CheckoutEndpoint : Endpoint<CheckoutRequest, CheckoutRespons
       {
           Post("/checkout");
           AllowAnonymous();
  +        Options(x => x.RequireRateLimiting("CheckoutPortal"));
           Version(1);
       }
   
  EOF
  ```
  Same heredoc mechanics note as Task 2 Step 3: patch lines start at column 0 between the `EOF` markers.

- [ ] **Step 5:** Verify staging:
  ```bash
  git diff --cached --stat
  git diff --cached -- src/billing-api/Endpoints/CheckoutEndpoint.cs
  git diff --cached -- src/billing-api/Program.cs
  ```
  Expected: 4 files staged. The staged `CheckoutEndpoint.cs` diff is exactly the one `Options(...)` line. The staged `Program.cs` diff is exactly the `using System.Threading.RateLimiting;` line plus the policy block with the new comment. Unstaged `git diff -- src/billing-api/Program.cs` shows only the `builder.Services.AddSingleton(TimeProvider.System);` line remaining.

- [ ] **Step 6:** Commit:
  ```bash
  git -c commit.gpgsign=false commit \
    -m "Rate limit the checkout and portal endpoints per session" \
    -m "Add a fixed-window CheckoutPortal policy (10 requests per minute, no queue, 429 on rejection) partitioned by the tenant cookie, falling back to the auth cookie, remote IP, then a shared anonymous bucket. This is deliberate burst-shaping rather than flood defense: the partition key is an unvalidated cookie and rotating it mints fresh partitions, but forged sessions die at validation after at most one fleet round-trip thanks to the negative cache, and volumetric protection belongs to ingress. Includes an integration test for the 429 path."
  ```
  Verify `git show --stat HEAD` lists exactly the 4 files.

---

### Task 5: T2 — Meter-event dedup identifiers + metered-price guard

**Files:**
- Modify: `src/billing-api/Services/MeterEventIdentifier.cs` (add Last-aggregation remarks, lines 14–22), `src/billing-api/Endpoints/Admin/SubscriptionCreateEndpoint.cs` (fields lines 92–97, ctor lines 102–116, meter options lines 244–252), `test/billing/Infrastructure/BillingFunctionalTestFactory.cs` (add `TimeProviderOverride`), `test/billing/Endpoints/Admin/MigrationMeteredEndpointTests.cs` (identifier assertion, lines 79–114), `test/billing/Endpoints/Admin/SubscriptionCreateEndpointTests.cs` (identifier assertion, lines 197–238), `test/billing/Services/BillingManagementServiceTests.cs` (XML summaries, lines 319–393)
- Commit (including already-written WIP): the six files above plus `src/billing-api/Services/BillingManagementService.cs`, `src/billing-api/Endpoints/Admin/MigrationMeteredEndpoint.cs`, `src/billing-api/Program.cs` (whole file — after Task 4 the only remaining unstaged hunk is the `TimeProvider.System` registration), `test/billing/billing.csproj` (the `Microsoft.Extensions.TimeProvider.Testing` line belongs to this theme), `test/billing/Services/StripeGatewayRetryTests.cs`
- Test: `test/billing/Endpoints/Admin/MigrationMeteredEndpointTests.cs`, `test/billing/Endpoints/Admin/SubscriptionCreateEndpointTests.cs`, `test/billing/Services/BillingManagementServiceTests.cs`

**Interfaces:**
- Produces: `public static string MeterEventIdentifier.Build(string stripeCustomerId, DateTimeOffset utcNow)` → `"{customerId}:{yyyyMMddHH}"`; `public TimeProvider? TimeProviderOverride { get; set; }` on `BillingFunctionalTestFactory`
- Consumes: `TimeProvider.GetUtcNow()` (DI singleton `TimeProvider.System`, registered in `Program.cs`); `Stripe.Billing.MeterEventCreateOptions.Identifier`
- Changes: `SubscriptionCreateEndpoint` constructor gains a `TimeProvider timeProvider` parameter (DI-resolved; no test constructs it directly).

- [ ] **Step 1 (ruling 2 — document Last aggregation):** In `src/billing-api/Services/MeterEventIdentifier.cs`, insert `<remarks>` between the `</summary>` and the first `<param>` of `Build` (after line 19):
  ```csharp
      /// <remarks>
      /// The Stripe meter is configured with "Last" aggregation, so the most recent event in the
      /// aggregation window wins rather than events summing. That makes the coarse hourly bucket
      /// safe in both directions: a same-hour retry is dropped as a duplicate identifier instead
      /// of double-counting, and a genuine same-hour change that gets suppressed self-heals when
      /// the next hourly heartbeat reports the new count under a fresh bucket.
      /// </remarks>
  ```

- [ ] **Step 2 (test infrastructure):** In `test/billing/Infrastructure/BillingFunctionalTestFactory.cs`, add a property after the `AuthHandler` property (after line 67):
  ```csharp
      /// <summary>
      /// Optional replacement for the system <see cref="TimeProvider"/> registered by the API.
      /// Set before the first client is created to make time-derived values, such as meter-event
      /// identifiers, deterministic in tests.
      /// </summary>
      public TimeProvider? TimeProviderOverride { get; set; }
  ```
  and register it inside `ConfigureTestServices`, directly after the `SessionValidator` replacement block (after the `});` that closes `services.AddSingleton(sp => ... new SessionValidator(...))`, currently line 203):
  ```csharp
              // Optionally pin time so endpoints that derive meter-event identifiers from the
              // clock produce deterministic values.
              if (TimeProviderOverride is not null)
              {
                  services.RemoveAll<TimeProvider>();
                  services.AddSingleton<TimeProvider>(TimeProviderOverride);
              }
  ```

- [ ] **Step 3 (TDD — failing identifier assertion for the third call site):** In `test/billing/Endpoints/Admin/SubscriptionCreateEndpointTests.cs`:
  - Add `using Microsoft.Extensions.Time.Testing;` to the usings, between the existing `using LinqToDB.Async;` and `using NSubstitute;` lines (the file's order is System first, then alphabetical).
  - In `MeteredSubscription_NoQuantityOnItem_InitialUsageReported`, add as the first line of the test body (before the `CreateCustomerAsync` mock setup):
  ```csharp
          _factory.TimeProviderOverride = new FakeTimeProvider(new DateTimeOffset(2026, 6, 15, 10, 5, 0, TimeSpan.Zero));
  ```
  - Extend the meter-event assertion at lines 232–237 from:
  ```csharp
          // Verify initial meter event was created with the machine count
          await _factory.StripeGateway.Received(1).CreateMeterEventAsync(
              Arg.Is<Stripe.Billing.MeterEventCreateOptions>(o =>
                  o.EventName == "machine_count" &&
                  o.Payload["stripe_customer_id"] == "cus_metered" &&
                  o.Payload["value"] == "8"),
              Arg.Any<CancellationToken>());
  ```
  to:
  ```csharp
          // Verify initial meter event was created with the machine count and the deterministic
          // hourly dedup identifier
          await _factory.StripeGateway.Received(1).CreateMeterEventAsync(
              Arg.Is<Stripe.Billing.MeterEventCreateOptions>(o =>
                  o.EventName == "machine_count" &&
                  o.Identifier == "cus_metered:2026061510" &&
                  o.Payload["stripe_customer_id"] == "cus_metered" &&
                  o.Payload["value"] == "8"),
              Arg.Any<CancellationToken>());
  ```

- [ ] **Step 4 (identifier assertion for the migration endpoint):** In `test/billing/Endpoints/Admin/MigrationMeteredEndpointTests.cs`:
  - Add `using Microsoft.Extensions.Time.Testing;` to the usings (after `LinqToDB.Async`, before `NSubstitute`).
  - In `Migration_ConvertLicensedToMetered_Success`, add as the first line of the test body (before the `using BillingDatabaseContext db = ...` line):
  ```csharp
          _factory.TimeProviderOverride = new FakeTimeProvider(new DateTimeOffset(2026, 6, 15, 10, 5, 0, TimeSpan.Zero));
  ```
  - Extend the meter-event assertion at lines 109–114 from:
  ```csharp
          // Verify initial usage record was created with 7 machines
          await _factory.StripeGateway.Received(1).CreateMeterEventAsync(
              Arg.Is<Stripe.Billing.MeterEventCreateOptions>(o =>
                  o.Payload["value"] == "7" &&
                  o.Payload["stripe_customer_id"] == "cus_migrate"),
              Arg.Any<CancellationToken>());
  ```
  to:
  ```csharp
          // Verify initial usage record was created with 7 machines and the deterministic
          // hourly dedup identifier
          await _factory.StripeGateway.Received(1).CreateMeterEventAsync(
              Arg.Is<Stripe.Billing.MeterEventCreateOptions>(o =>
                  o.Identifier == "cus_migrate:2026061510" &&
                  o.Payload["value"] == "7" &&
                  o.Payload["stripe_customer_id"] == "cus_migrate"),
              Arg.Any<CancellationToken>());
  ```

- [ ] **Step 5 (see the red):** Run the two touched classes:
  ```bash
  dotnet build test/billing/billing.csproj -c Release --no-incremental
  ./test/billing/bin/Release/net10.0/osx-arm64/billing --treenode-filter "/*/*/SubscriptionCreateEndpointTests/*"
  ./test/billing/bin/Release/net10.0/osx-arm64/billing --treenode-filter "/*/*/MigrationMeteredEndpointTests/*"
  ```
  Expected: `MeteredSubscription_NoQuantityOnItem_InitialUsageReported` FAILS (the endpoint sends no `Identifier` yet); `Migration_ConvertLicensedToMetered_Success` PASSES (the migration endpoint already sets it — this is the characterization half of the gap).

- [ ] **Step 6 (wire the third call site):** In `src/billing-api/Endpoints/Admin/SubscriptionCreateEndpoint.cs`:
  - Add a field after `private readonly BillingOptions _billingOptions;` (line 96):
  ```csharp
      private readonly TimeProvider _timeProvider;
  ```
  - Change the constructor (lines 102–116) from:
  ```csharp
      public SubscriptionCreateEndpoint(
          IStripeGateway stripe,
          IFleetAdminClient fleetAdmin,
          IBillingAuditService auditService,
          BillingDatabaseContext db,
          IOptions<BillingOptions> billingOptions,
          ILogger<SubscriptionCreateEndpoint> logger)
      {
          _stripe = stripe;
          _fleetAdmin = fleetAdmin;
          _auditService = auditService;
          _db = db;
          _billingOptions = billingOptions.Value;
          _logger = logger;
      }
  ```
  to:
  ```csharp
      public SubscriptionCreateEndpoint(
          IStripeGateway stripe,
          IFleetAdminClient fleetAdmin,
          IBillingAuditService auditService,
          BillingDatabaseContext db,
          IOptions<BillingOptions> billingOptions,
          TimeProvider timeProvider,
          ILogger<SubscriptionCreateEndpoint> logger)
      {
          _stripe = stripe;
          _fleetAdmin = fleetAdmin;
          _auditService = auditService;
          _db = db;
          _billingOptions = billingOptions.Value;
          _timeProvider = timeProvider;
          _logger = logger;
      }
  ```
  - Change the meter options (lines 244–252) from:
  ```csharp
                  Stripe.Billing.MeterEventCreateOptions meterOptions = new()
                  {
                      EventName = _billingOptions.MeterEventName,
                      Payload = new Dictionary<string, string>
                      {
                          { "stripe_customer_id", customerId },
                          { "value", req.Quantity.ToString() }
                      }
                  };
  ```
  to:
  ```csharp
                  // Derive a deterministic identifier keyed to a coarse hourly UTC bucket so a
                  // retried create within the same window is de-duplicated by Stripe rather than
                  // double-counting the initial machine usage.
                  Stripe.Billing.MeterEventCreateOptions meterOptions = new()
                  {
                      EventName = _billingOptions.MeterEventName,
                      Identifier = MeterEventIdentifier.Build(customerId, _timeProvider.GetUtcNow()),
                      Payload = new Dictionary<string, string>
                      {
                          { "stripe_customer_id", customerId },
                          { "value", req.Quantity.ToString() }
                      }
                  };
  ```

- [ ] **Step 7 (XML summaries on the new BillingManagementServiceTests tests):** In `test/billing/Services/BillingManagementServiceTests.cs`, insert directly above the `[Test]` attributes at lines 319, 356, and 392:

  Above `ReportMachineUsage_SameHourBucket_ProducesStableIdentifier_RetrySafe`:
  ```csharp
      /// <summary>
      /// Two usage reports within the same UTC hour share one meter-event identifier so Stripe
      /// drops the at-least-once retry as a duplicate. With Last aggregation on the meter, a
      /// suppressed same-hour change self-heals at the next hourly report.
      /// </summary>
  ```
  Above `ReportMachineUsage_DifferentHourBucket_ProducesDistinctIdentifier`:
  ```csharp
      /// <summary>
      /// Reports that cross an hour boundary produce distinct identifiers so genuinely new usage
      /// is counted rather than suppressed as a duplicate.
      /// </summary>
  ```
  Above `UpdateQuantity_MeteredPrice_RejectsWithoutCallingStripe`:
  ```csharp
      /// <summary>
      /// A quantity update against a metered-price subscription is refused before any Stripe
      /// write, since fixed quantities would mis-bill alongside metered usage reporting.
      /// </summary>
  ```

- [ ] **Step 8:** Verify: rebuild, rerun the two endpoint test classes (both green now), then full suite + solution build:
  ```bash
  dotnet build test/billing/billing.csproj -c Release --no-incremental
  ./test/billing/bin/Release/net10.0/osx-arm64/billing --treenode-filter "/*/*/SubscriptionCreateEndpointTests/*"
  ./test/billing/bin/Release/net10.0/osx-arm64/billing --treenode-filter "/*/*/MigrationMeteredEndpointTests/*"
  dotnet build vord-internal.slnx -c Release
  dotnet run --project test/billing/billing.csproj -c Release
  ```
  Expected: 0 warnings; full suite 475 passed / 13 skipped / 0 failed.

- [ ] **Step 9:** Commit (staging `Program.cs` as a whole file is now safe — its only remaining unstaged hunk is this theme's `TimeProvider.System` registration):
  ```bash
  cd /Users/jonathanmiller/Repositories/framlux/vord-internal
  git add src/billing-api/Services/MeterEventIdentifier.cs \
          src/billing-api/Services/BillingManagementService.cs \
          src/billing-api/Endpoints/Admin/MigrationMeteredEndpoint.cs \
          src/billing-api/Endpoints/Admin/SubscriptionCreateEndpoint.cs \
          src/billing-api/Program.cs \
          test/billing/billing.csproj \
          test/billing/Infrastructure/BillingFunctionalTestFactory.cs \
          test/billing/Services/BillingManagementServiceTests.cs \
          test/billing/Services/StripeGatewayRetryTests.cs \
          test/billing/Endpoints/Admin/MigrationMeteredEndpointTests.cs \
          test/billing/Endpoints/Admin/SubscriptionCreateEndpointTests.cs
  git diff --cached -- src/billing-api/Program.cs
  git -c commit.gpgsign=false commit \
    -m "De-duplicate Stripe meter events with hourly identifiers" \
    -m "Every meter-event call site now sends a deterministic identifier of the form customerId:yyyyMMddHH, so at-least-once gRPC and admin retries within the same hour are dropped by Stripe as duplicates instead of double-counting machine usage. The meter uses Last aggregation, so a suppressed same-hour change self-heals at the next hourly heartbeat. UpdateSubscriptionQuantity now refuses metered-price subscriptions before touching Stripe, since a fixed quantity with prorations would mis-bill alongside usage reporting. TimeProvider.System is registered for DI and the test factory can pin it to a FakeTimeProvider for deterministic identifier assertions."
  ```
  Expected `git diff --cached` output before committing: only the `builder.Services.AddSingleton(TimeProvider.System);` line for `Program.cs`. Verify `git show --stat HEAD` lists exactly the 11 files.

---

### Task 6: T1 — Webhook transactional idempotency, audit-every-attempt, honest comments, BillingInterval currency

Ruling 4 is implemented here (not in T3) because it modifies `HandleSubscriptionUpdatedAsync` inside the transaction this theme introduces; T3 only touches `CheckoutEndpoint`.

**Files:**
- Modify: `src/billing-api/Services/WebhookProcessorService.cs` (checkout handler lines 284–405; subscription.updated handler lines 425–596; deleted handler lines 598–660; payment-failed handler lines 662–709; payment-succeeded handler lines 711–758), `test/billing/Services/WebhookProcessorServiceTests.cs` (two existing tests at lines 1486–1540 and 1542–1590; four new tests appended)
- Commit: those two files only
- Test: `test/billing/Services/WebhookProcessorServiceTests.cs`

**Interfaces:**
- Consumes: `Task IBillingAuditService.LogAsync(string tenantExternalId, string action, string performedBy, object? details = null, object? previousState = null, object? newState = null, CancellationToken ct = default)`; `db.BeginTransactionAsync(ct)` → `DataConnectionTransaction`; `db.Prices` (`CatalogPrice`, has `Interval`/`IsActive`/`StripePriceId`); `BillingIntervalExtensions.TryFromStripeInterval(string, out BillingInterval)`; LinqToDB `AsUpdatable()` / `IUpdatable<T>` for the conditionally-composed update.
- Produces: new audit action `"interval_changed"` (performedBy `"webhook"`, details `{ PreviousPriceId, CurrentPriceId }`, previousState `{ PriceId, Interval }`, newState `{ PriceId, Interval }`); `StripeCustomers.BillingInterval` updated on price change within the existing conditional `CurrentPriceId` advance.
- Ruling 1 applied per handler: checkout.completed / subscription.deleted / invoice.payment_failed / invoice.payment_succeeded get `LogAsync` moved outside the `FleetNotifiedAt` gate (still inside the transaction, before commit). For subscription.updated, the tier/interval audits are governed by price-change detection, not the gate; its already-notified fast path is retained because the price advance commits atomically with `FleetNotifiedAt`, so re-running detection on a reprocessed event cannot produce an audit row — there is no gated audit to move, and removing the fast path would violate the retained no-re-notify semantics (the WIP test `ProcessNextEventAsync_SubscriptionUpdatedAlreadyNotified_SkipsTierChangeFleetCall` pins this).

- [ ] **Step 1 (TDD — flip the two reprocess-audit tests to expect audit on every attempt):** In `test/billing/Services/WebhookProcessorServiceTests.cs`:

  (a) Replace the doc comment, name, and audit assertion of the first test (lines 1486–1540). Replace:
  ```csharp
      /// <summary>
      /// Reprocessing a checkout event whose FleetNotifiedAt is already set must not re-notify the
      /// fleet nor write a second audit row. This is the crash-and-retry path where the event was
      /// notified but never marked Completed.
      /// </summary>
      [Test]
      public async Task ProcessNextEventAsync_CheckoutAlreadyNotified_DoesNotReNotifyOrDuplicateAudit()
  ```
  with:
  ```csharp
      /// <summary>
      /// Reprocessing a checkout event whose FleetNotifiedAt is already set must not re-notify
      /// the fleet, but it must still write an audit row: every processing attempt is recorded,
      /// including the crash-and-retry path where the event was notified but never Completed.
      /// </summary>
      [Test]
      public async Task ProcessNextEventAsync_CheckoutAlreadyNotified_DoesNotReNotifyButStillAudits()
  ```
  and replace its audit assertion:
  ```csharp
              // No audit row must be written on the reprocessed event.
              await auditService.DidNotReceive().LogAsync(
                  Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                  Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
  ```
  with:
  ```csharp
              // The audit row is written on every processing attempt, including this reprocessed
              // one, so the audit trail records each delivery.
              await auditService.Received(1).LogAsync(
                  "tenant-reprocess", "subscription_created", "webhook",
                  Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
  ```

  (b) Replace the doc comment, name, and audit assertion of the second test (lines 1542–1590). Replace:
  ```csharp
      /// <summary>
      /// Processing the same checkout event twice (first run notifies and marks notified, the
      /// retry sees FleetNotifiedAt set) results in exactly one fleet notification and one audit
      /// row, proving the FleetNotifiedAt gate is honoured across reprocessing.
      /// </summary>
      [Test]
      public async Task ProcessNextEventAsync_CheckoutProcessedTwice_NotifiesAndAuditsExactlyOnce()
  ```
  with:
  ```csharp
      /// <summary>
      /// Processing the same checkout event twice (first run notifies and marks notified, the
      /// retry sees FleetNotifiedAt set) results in exactly one fleet notification but one audit
      /// row per attempt: the gate protects the fleet call, not the audit trail.
      /// </summary>
      [Test]
      public async Task ProcessNextEventAsync_CheckoutProcessedTwice_NotifiesOnceAndAuditsEachAttempt()
  ```
  and replace its final audit assertion:
  ```csharp
              // Exactly one upgrade notification and one audit row despite two processing passes.
              await fleetClient.Received(1).SendBillingActionAsync(
                  BillingAction.UpgradeToPro,
                  "tenant-twice",
                  null,
                  Arg.Any<CancellationToken>());
              await auditService.Received(1).LogAsync(
                  "tenant-twice", "subscription_created", "webhook",
                  Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
  ```
  with:
  ```csharp
              // Exactly one upgrade notification, but one audit row per processing pass.
              await fleetClient.Received(1).SendBillingActionAsync(
                  BillingAction.UpgradeToPro,
                  "tenant-twice",
                  null,
                  Arg.Any<CancellationToken>());
              await auditService.Received(2).LogAsync(
                  "tenant-twice", "subscription_created", "webhook",
                  Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
  ```

- [ ] **Step 2 (TDD — new reprocess-audit tests for the other gated handlers):** Append the following three tests at the end of `WebhookProcessorServiceTests` (before the closing class brace, after `ProcessNextEventAsync_DuplicateSubscriptionUpdatedTierChange_DoesNotDoubleNotify`):
  ```csharp
      /// <summary>
      /// Reprocessing a subscription.deleted event whose FleetNotifiedAt is already set must not
      /// re-notify the fleet but must still write an audit row for the attempt.
      /// </summary>
      [Test]
      public async Task ProcessNextEventAsync_SubscriptionDeletedAlreadyNotified_DoesNotReNotifyButStillAudits()
      {
          IFleetApiClient fleetClient = Substitute.For<IFleetApiClient>();
          IBillingAuditService auditService = Substitute.For<IBillingAuditService>();
          (WebhookProcessorService service, IFleetApiClient _, BillingTestDatabaseFactory dbFactory) =
              CreateServiceWithDependencies(fleetClient: fleetClient, auditService: auditService);

          using (dbFactory)
          {
              await SeedStripeCustomer(dbFactory,
                  tenantExternalId: "tenant-del-reprocess",
                  customerId: "cus_del_reprocess");

              string payload = BuildSubscriptionPayload(
                  eventId: "evt_del_reprocess",
                  eventType: EventTypes.CustomerSubscriptionDeleted,
                  customerId: "cus_del_reprocess");
              WebhookEvent webhookEvent = new()
              {
                  EventId = "evt_del_reprocess",
                  EventType = EventTypes.CustomerSubscriptionDeleted,
                  ReceivedAt = DateTimeOffset.UtcNow,
                  RawPayload = payload,
                  EventCreatedAt = DateTimeOffset.UtcNow,
                  Status = WebhookEventStatus.Pending,
                  RetryCount = 0,
                  FleetNotifiedAt = DateTimeOffset.UtcNow
              };
              await dbFactory.Context.InsertAsync(webhookEvent);

              await service.ProcessNextEventAsync(CancellationToken.None);

              await fleetClient.DidNotReceive().SendBillingActionAsync(
                  Arg.Any<BillingAction>(),
                  Arg.Any<string>(),
                  Arg.Any<DateTimeOffset?>(),
                  Arg.Any<CancellationToken>());
              await auditService.Received(1).LogAsync(
                  "tenant-del-reprocess", "subscription_deleted", "webhook",
                  Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
          }
      }

      /// <summary>
      /// Reprocessing an invoice.payment_failed event whose FleetNotifiedAt is already set must
      /// not re-notify the fleet but must still write an audit row for the attempt.
      /// </summary>
      [Test]
      public async Task ProcessNextEventAsync_PaymentFailedAlreadyNotified_DoesNotReNotifyButStillAudits()
      {
          IFleetApiClient fleetClient = Substitute.For<IFleetApiClient>();
          IBillingAuditService auditService = Substitute.For<IBillingAuditService>();
          (WebhookProcessorService service, IFleetApiClient _, BillingTestDatabaseFactory dbFactory) =
              CreateServiceWithDependencies(fleetClient: fleetClient, auditService: auditService);

          using (dbFactory)
          {
              await SeedStripeCustomer(dbFactory,
                  tenantExternalId: "tenant-fail-reprocess",
                  customerId: "cus_fail_reprocess");

              string payload = BuildInvoicePayload(
                  eventId: "evt_fail_reprocess",
                  eventType: EventTypes.InvoicePaymentFailed,
                  customerId: "cus_fail_reprocess");
              WebhookEvent webhookEvent = new()
              {
                  EventId = "evt_fail_reprocess",
                  EventType = EventTypes.InvoicePaymentFailed,
                  ReceivedAt = DateTimeOffset.UtcNow,
                  RawPayload = payload,
                  EventCreatedAt = DateTimeOffset.UtcNow,
                  Status = WebhookEventStatus.Pending,
                  RetryCount = 0,
                  FleetNotifiedAt = DateTimeOffset.UtcNow
              };
              await dbFactory.Context.InsertAsync(webhookEvent);

              await service.ProcessNextEventAsync(CancellationToken.None);

              await fleetClient.DidNotReceive().SendBillingActionAsync(
                  Arg.Any<BillingAction>(),
                  Arg.Any<string>(),
                  Arg.Any<DateTimeOffset?>(),
                  Arg.Any<CancellationToken>());
              await auditService.Received(1).LogAsync(
                  "tenant-fail-reprocess", "payment_failed", "webhook",
                  Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
          }
      }

      /// <summary>
      /// Reprocessing an invoice.payment_succeeded event whose FleetNotifiedAt is already set
      /// must not re-notify the fleet but must still write an audit row for the attempt.
      /// </summary>
      [Test]
      public async Task ProcessNextEventAsync_PaymentSucceededAlreadyNotified_DoesNotReNotifyButStillAudits()
      {
          IFleetApiClient fleetClient = Substitute.For<IFleetApiClient>();
          IBillingAuditService auditService = Substitute.For<IBillingAuditService>();
          (WebhookProcessorService service, IFleetApiClient _, BillingTestDatabaseFactory dbFactory) =
              CreateServiceWithDependencies(fleetClient: fleetClient, auditService: auditService);

          using (dbFactory)
          {
              await SeedStripeCustomer(dbFactory,
                  tenantExternalId: "tenant-ok-reprocess",
                  customerId: "cus_ok_reprocess");

              string payload = BuildInvoicePayload(
                  eventId: "evt_ok_reprocess",
                  eventType: EventTypes.InvoicePaymentSucceeded,
                  customerId: "cus_ok_reprocess");
              WebhookEvent webhookEvent = new()
              {
                  EventId = "evt_ok_reprocess",
                  EventType = EventTypes.InvoicePaymentSucceeded,
                  ReceivedAt = DateTimeOffset.UtcNow,
                  RawPayload = payload,
                  EventCreatedAt = DateTimeOffset.UtcNow,
                  Status = WebhookEventStatus.Pending,
                  RetryCount = 0,
                  FleetNotifiedAt = DateTimeOffset.UtcNow
              };
              await dbFactory.Context.InsertAsync(webhookEvent);

              await service.ProcessNextEventAsync(CancellationToken.None);

              await fleetClient.DidNotReceive().SendBillingActionAsync(
                  Arg.Any<BillingAction>(),
                  Arg.Any<string>(),
                  Arg.Any<DateTimeOffset?>(),
                  Arg.Any<CancellationToken>());
              await auditService.Received(1).LogAsync(
                  "tenant-ok-reprocess", "payment_succeeded", "webhook",
                  Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
          }
      }
  ```

- [ ] **Step 3 (TDD — interval swap test):** Append one more test after those three:
  ```csharp
      /// <summary>
      /// A price change within the same tier (an interval swap such as Pro Monthly to Pro
      /// Annual) dispatches no tier action but writes an interval_changed audit row and updates
      /// the stored BillingInterval and CurrentPriceId.
      /// </summary>
      [Test]
      public async Task ProcessNextEventAsync_SubscriptionUpdatedSameTierIntervalSwap_AuditsIntervalChangeAndUpdatesInterval()
      {
          IFleetApiClient fleetClient = Substitute.For<IFleetApiClient>();
          IBillingAuditService auditService = Substitute.For<IBillingAuditService>();
          (WebhookProcessorService service, IFleetApiClient _, BillingTestDatabaseFactory dbFactory) =
              CreateServiceWithDependencies(fleetClient: fleetClient, auditService: auditService);

          using (dbFactory)
          {
              // Seed a catalog where both prices map to the Pro tier with different intervals.
              int productId = await dbFactory.Context.InsertWithInt32IdentityAsync(new CatalogProduct
              {
                  StripeProductId = "prod_pro_swap",
                  Name = "VordFleet Pro",
                  IsActive = true,
                  CreatedAt = DateTimeOffset.UtcNow,
                  UpdatedAt = DateTimeOffset.UtcNow,
              });
              int monthlyPriceId = await dbFactory.Context.InsertWithInt32IdentityAsync(new CatalogPrice
              {
                  StripePriceId = "price_pro_monthly_swap",
                  ProductId = productId,
                  Interval = BillingInterval.Monthly,
                  UnitAmountCents = 900,
                  Currency = "usd",
                  IsActive = true,
                  CreatedAt = DateTimeOffset.UtcNow,
                  UpdatedAt = DateTimeOffset.UtcNow,
              });
              int annualPriceId = await dbFactory.Context.InsertWithInt32IdentityAsync(new CatalogPrice
              {
                  StripePriceId = "price_pro_annual_swap",
                  ProductId = productId,
                  Interval = BillingInterval.Annual,
                  UnitAmountCents = 9000,
                  Currency = "usd",
                  IsActive = true,
                  CreatedAt = DateTimeOffset.UtcNow,
                  UpdatedAt = DateTimeOffset.UtcNow,
              });
              await dbFactory.Context.InsertAsync(new TierMapping
              {
                  PriceId = monthlyPriceId,
                  Tier = DomainBillingTier.Pro,
                  CreatedAt = DateTimeOffset.UtcNow,
              });
              await dbFactory.Context.InsertAsync(new TierMapping
              {
                  PriceId = annualPriceId,
                  Tier = DomainBillingTier.Pro,
                  CreatedAt = DateTimeOffset.UtcNow,
              });

              await dbFactory.Context.InsertAsync(new StripeCustomer
              {
                  TenantExternalId = "tenant-interval-swap",
                  CustomerId = "cus_interval_swap",
                  SubscriptionId = "sub_interval_swap",
                  CurrentPriceId = "price_pro_monthly_swap",
                  BillingInterval = BillingInterval.Monthly,
                  CreatedAt = DateTimeOffset.UtcNow,
                  UpdatedAt = DateTimeOffset.UtcNow,
              });

              string payload = BuildSubscriptionPayload(
                  eventId: "evt_interval_swap",
                  eventType: EventTypes.CustomerSubscriptionUpdated,
                  subscriptionId: "sub_interval_swap",
                  customerId: "cus_interval_swap",
                  priceId: "price_pro_annual_swap");
              WebhookEvent webhookEvent = CreateWebhookEvent(
                  "evt_interval_swap",
                  EventTypes.CustomerSubscriptionUpdated,
                  payload);
              await dbFactory.Context.InsertAsync(webhookEvent);

              await service.ProcessNextEventAsync(CancellationToken.None);

              // No tier action is dispatched for a same-tier swap; only the period-end update goes out.
              await fleetClient.DidNotReceive().SendBillingActionAsync(
                  BillingAction.UpgradeToPro, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
              await fleetClient.DidNotReceive().SendBillingActionAsync(
                  BillingAction.UpgradeToTeam, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
              await fleetClient.DidNotReceive().SendBillingActionAsync(
                  BillingAction.DowngradeToPro, Arg.Any<string>(), null, Arg.Any<CancellationToken>());
              await fleetClient.Received(1).SendBillingActionAsync(
                  BillingAction.UpdatePeriodEnd,
                  "tenant-interval-swap",
                  Arg.Any<DateTimeOffset?>(),
                  Arg.Any<CancellationToken>());

              // The swap leaves an interval_changed audit row.
              await auditService.Received(1).LogAsync(
                  "tenant-interval-swap", "interval_changed", "webhook",
                  Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());

              // The stored interval and price advance together.
              StripeCustomer? updated = await dbFactory.Context.StripeCustomers
                  .Where(c => c.TenantExternalId == "tenant-interval-swap")
                  .FirstOrDefaultAsync();
              await Assert.That(updated!.CurrentPriceId).IsEqualTo("price_pro_annual_swap");
              await Assert.That(updated.BillingInterval).IsEqualTo(BillingInterval.Annual);
          }
      }
  ```
  This test also requires one new using: `BillingTier` is ambiguous in this file because both `Framlux.Billing.Api.Models` and `Framlux.Vord.BillingGrpc` are imported and the gRPC namespace also defines `BillingTier`, so add the same alias the production file uses, after the existing `using Stripe;` line:
  ```csharp
  using DomainBillingTier = Framlux.Billing.Api.Models.BillingTier;
  ```
  Everything else resolves with existing usings: `CatalogProduct`, `CatalogPrice`, `TierMapping`, and `BillingInterval` come from `Framlux.Billing.Api.Models` (the gRPC namespace defines no `BillingInterval`, so no alias is needed for it), and `InsertWithInt32IdentityAsync` comes from `LinqToDB`.

- [ ] **Step 4 (see the red):**
  ```bash
  dotnet build test/billing/billing.csproj -c Release --no-incremental
  ./test/billing/bin/Release/net10.0/osx-arm64/billing --treenode-filter "/*/*/WebhookProcessorServiceTests/*"
  ```
  Expected failures: `..._DoesNotReNotifyButStillAudits` (checkout, deleted, payment-failed, payment-succeeded variants), `..._NotifiesOnceAndAuditsEachAttempt`, and `..._SameTierIntervalSwap_...`. Everything else in the class passes.

- [ ] **Step 5 (checkout handler — audit outside the gate + honest comment):** In `src/billing-api/Services/WebhookProcessorService.cs`, replace the transaction comment (lines 311–314):
  ```csharp
          // Store or update the customer mapping, dispatch the fleet notification, mark the event
          // notified, and write the audit row inside a single transaction. This makes the handler
          // crash-safe: a retry after a partial write cannot re-notify the fleet or duplicate the
          // audit entry because FleetNotifiedAt is committed atomically with those side effects.
  ```
  with:
  ```csharp
          // Store or update the customer mapping, dispatch the fleet notification, mark the event
          // notified, and write the audit row inside a single transaction, so the FleetNotifiedAt
          // gate commits atomically with the work it guards. Delivery stays at-least-once: a crash
          // after the fleet call but before the commit rolls back the gate and the retry notifies
          // the fleet again.
  ```
  Then move the audit call out of the gate. Replace (lines 375–395):
  ```csharp
          if (webhookEvent.FleetNotifiedAt.HasValue == false)
          {
              if (string.IsNullOrEmpty(initialPriceId) == false)
              {
                  await db.StripeCustomers
                      .Where(c => c.TenantExternalId == tenantExternalId)
                      .Set(c => c.CurrentPriceId, initialPriceId)
                      .Set(c => c.UpdatedAt, DateTimeOffset.UtcNow)
                      .UpdateAsync(ct);
              }

              await fleetApiClient.SendBillingActionAsync(upgradeAction, tenantExternalId, null, ct);

              await db.WebhookEvents
                  .Where(e => e.EventId == webhookEvent.EventId)
                  .Set(e => e.FleetNotifiedAt, DateTimeOffset.UtcNow)
                  .UpdateAsync(ct);

              await auditService.LogAsync(tenantExternalId, "subscription_created", "webhook",
                  new { Tier = tier, checkoutSession.CustomerId, checkoutSession.SubscriptionId }, ct: ct);
          }
  ```
  with:
  ```csharp
          if (webhookEvent.FleetNotifiedAt.HasValue == false)
          {
              if (string.IsNullOrEmpty(initialPriceId) == false)
              {
                  await db.StripeCustomers
                      .Where(c => c.TenantExternalId == tenantExternalId)
                      .Set(c => c.CurrentPriceId, initialPriceId)
                      .Set(c => c.UpdatedAt, DateTimeOffset.UtcNow)
                      .UpdateAsync(ct);
              }

              await fleetApiClient.SendBillingActionAsync(upgradeAction, tenantExternalId, null, ct);

              await db.WebhookEvents
                  .Where(e => e.EventId == webhookEvent.EventId)
                  .Set(e => e.FleetNotifiedAt, DateTimeOffset.UtcNow)
                  .UpdateAsync(ct);
          }

          // The audit row is deliberately outside the FleetNotifiedAt gate: every processing
          // attempt, including reprocessed and duplicate events, is recorded.
          await auditService.LogAsync(tenantExternalId, "subscription_created", "webhook",
              new { Tier = tier, checkoutSession.CustomerId, checkoutSession.SubscriptionId }, ct: ct);
  ```
  (The `await transaction.CommitAsync(ct);` that follows is unchanged.)

- [ ] **Step 6 (subscription.updated — interval derivation, tier-aware branches, interval_changed audit, honest comment):** Still in `WebhookProcessorService.cs`, inside `HandleSubscriptionUpdatedAsync`:

  (a) After the `previousTier` resolution block ends (the closing brace of `if (string.IsNullOrEmpty(previousPriceId) == false) { ... }`, currently line 521) and before the `// Only dispatch tier-change actions when the price actually changed` comment, insert:
  ```csharp
              // Derive the billing interval of the new price so the stored BillingInterval
              // column tracks plan changes. The catalog is authoritative; the recurrence on the
              // Stripe price object is the fallback for prices not yet synced.
              BillingInterval? resolvedInterval = await (
                  from p in db.Prices
                  where p.StripePriceId == currentPriceId
                  select (BillingInterval?)p.Interval
              ).FirstOrDefaultAsync(ct);

              if ((resolvedInterval is null) || (resolvedInterval.Value == BillingInterval.None))
              {
                  string? stripeInterval = subscription.Items!.Data[0].Price?.Recurring?.Interval;
                  if ((string.IsNullOrEmpty(stripeInterval) == false) &&
                      BillingIntervalExtensions.TryFromStripeInterval(stripeInterval!, out BillingInterval parsedInterval))
                  {
                      resolvedInterval = parsedInterval;
                  }
                  else
                  {
                      resolvedInterval = null;
                  }
              }
  ```

  (b) Replace the dispatch block (currently lines 523–561):
  ```csharp
              // Only dispatch tier-change actions when the price actually changed
              if (string.Equals(currentPriceId, previousPriceId, StringComparison.Ordinal) == false)
              {
                  bool isDowngrade = (previousTier == DomainBillingTier.Team) && (resolvedTier == DomainBillingTier.Pro);

                  if (isDowngrade)
                  {
                      await fleetApiClient.SendBillingActionAsync(
                          BillingAction.DowngradeToPro, customer.TenantExternalId, null, ct);
                      await auditService.LogAsync(customer.TenantExternalId, "tier_downgrade", "webhook",
                          new { PreviousPriceId = previousPriceId, CurrentPriceId = currentPriceId },
                          new { PriceId = previousPriceId }, new { PriceId = currentPriceId }, ct);
                      _logger.LogInformation(
                          "Subscription tier downgraded to Pro for tenant {TenantExternalId}",
                          customer.TenantExternalId);
                  }
                  else if (resolvedTier == DomainBillingTier.Team)
                  {
                      await fleetApiClient.SendBillingActionAsync(
                          BillingAction.UpgradeToTeam, customer.TenantExternalId, null, ct);
                      await auditService.LogAsync(customer.TenantExternalId, "tier_upgrade", "webhook",
                          new { PreviousPriceId = previousPriceId, CurrentPriceId = currentPriceId },
                          new { PriceId = previousPriceId }, new { PriceId = currentPriceId }, ct);
                      _logger.LogInformation(
                          "Subscription tier changed to Team for tenant {TenantExternalId}",
                          customer.TenantExternalId);
                  }
                  else if (resolvedTier == DomainBillingTier.Pro)
                  {
                      await fleetApiClient.SendBillingActionAsync(
                          BillingAction.UpgradeToPro, customer.TenantExternalId, null, ct);
                      await auditService.LogAsync(customer.TenantExternalId, "tier_upgrade", "webhook",
                          new { PreviousPriceId = previousPriceId, CurrentPriceId = currentPriceId },
                          new { PriceId = previousPriceId }, new { PriceId = currentPriceId }, ct);
                      _logger.LogInformation(
                          "Subscription tier changed to Pro for tenant {TenantExternalId}",
                          customer.TenantExternalId);
                  }
              }
  ```
  with:
  ```csharp
              // Only dispatch tier-change actions when the price actually changed. A price change
              // within the same tier is an interval swap (for example Pro Monthly to Pro Annual):
              // the fleet needs no tier action for it, but it must still leave an audit trail.
              if (string.Equals(currentPriceId, previousPriceId, StringComparison.Ordinal) == false)
              {
                  bool isDowngrade = (previousTier == DomainBillingTier.Team) && (resolvedTier == DomainBillingTier.Pro);
                  bool isSameTier = (previousTier is not null) && (previousTier == resolvedTier);

                  if (isDowngrade)
                  {
                      await fleetApiClient.SendBillingActionAsync(
                          BillingAction.DowngradeToPro, customer.TenantExternalId, null, ct);
                      await auditService.LogAsync(customer.TenantExternalId, "tier_downgrade", "webhook",
                          new { PreviousPriceId = previousPriceId, CurrentPriceId = currentPriceId },
                          new { PriceId = previousPriceId }, new { PriceId = currentPriceId }, ct);
                      _logger.LogInformation(
                          "Subscription tier downgraded to Pro for tenant {TenantExternalId}",
                          customer.TenantExternalId);
                  }
                  else if (isSameTier)
                  {
                      await auditService.LogAsync(customer.TenantExternalId, "interval_changed", "webhook",
                          new { PreviousPriceId = previousPriceId, CurrentPriceId = currentPriceId },
                          new { PriceId = previousPriceId, Interval = customer.BillingInterval.ToString() },
                          new { PriceId = currentPriceId, Interval = resolvedInterval?.ToString() }, ct);
                      _logger.LogInformation(
                          "Billing interval changed to {Interval} for tenant {TenantExternalId}",
                          resolvedInterval,
                          customer.TenantExternalId);
                  }
                  else if (resolvedTier == DomainBillingTier.Team)
                  {
                      await fleetApiClient.SendBillingActionAsync(
                          BillingAction.UpgradeToTeam, customer.TenantExternalId, null, ct);
                      await auditService.LogAsync(customer.TenantExternalId, "tier_upgrade", "webhook",
                          new { PreviousPriceId = previousPriceId, CurrentPriceId = currentPriceId },
                          new { PriceId = previousPriceId }, new { PriceId = currentPriceId }, ct);
                      _logger.LogInformation(
                          "Subscription tier changed to Team for tenant {TenantExternalId}",
                          customer.TenantExternalId);
                  }
                  else if (resolvedTier == DomainBillingTier.Pro)
                  {
                      await fleetApiClient.SendBillingActionAsync(
                          BillingAction.UpgradeToPro, customer.TenantExternalId, null, ct);
                      await auditService.LogAsync(customer.TenantExternalId, "tier_upgrade", "webhook",
                          new { PreviousPriceId = previousPriceId, CurrentPriceId = currentPriceId },
                          new { PriceId = previousPriceId }, new { PriceId = currentPriceId }, ct);
                      _logger.LogInformation(
                          "Subscription tier changed to Pro for tenant {TenantExternalId}",
                          customer.TenantExternalId);
                  }
              }
  ```
  Behavioral note (intended): a same-tier price change previously fell into the `resolvedTier == Pro/Team` branch and re-dispatched a redundant idempotent tier upgrade labeled `tier_upgrade`; it is now audited as `interval_changed` with no fleet tier action, matching the ruling. No existing test asserts the old same-tier dispatch (verified: the only same-tier tests use an unchanged price).

  (c) Replace the conditional price advance (currently lines 563–572):
  ```csharp
              // Store the current price for future comparison, but only when the stored value still
              // equals the previousPriceId we read. A concurrent racer that already advanced the
              // price no-ops here, so the losing event cannot clobber a newer tier classification.
              await db.StripeCustomers
                  .Where(c => c.Id == customer.Id &&
                              ((c.CurrentPriceId == previousPriceId) ||
                               (c.CurrentPriceId == null && previousPriceId == null)))
                  .Set(c => c.CurrentPriceId, currentPriceId)
                  .Set(c => c.UpdatedAt, DateTimeOffset.UtcNow)
                  .UpdateAsync(ct);
  ```
  with:
  ```csharp
              // Store the current price for future comparison, but only when the stored value still
              // equals the previousPriceId we read. A concurrent racer that already advanced the
              // price no-ops here, so the losing event cannot clobber a newer tier classification.
              // BillingInterval rides the same conditional write so it advances with the price.
              IUpdatable<StripeCustomer> priceAdvance = db.StripeCustomers
                  .Where(c => c.Id == customer.Id &&
                              ((c.CurrentPriceId == previousPriceId) ||
                               (c.CurrentPriceId == null && previousPriceId == null)))
                  .AsUpdatable()
                  .Set(c => c.CurrentPriceId, currentPriceId)
                  .Set(c => c.UpdatedAt, DateTimeOffset.UtcNow);

              if (resolvedInterval.HasValue)
              {
                  priceAdvance = priceAdvance.Set(c => c.BillingInterval, resolvedInterval.Value);
              }

              await priceAdvance.UpdateAsync(ct);
  ```

  (d) Replace the period-end comment (currently lines 575–576):
  ```csharp
          // Send the idempotent period-end update, then mark fleet notified, all within the same
          // transaction so a crash cannot re-notify the fleet on retry.
  ```
  with:
  ```csharp
          // Send the idempotent period-end update, then mark fleet notified, in the same
          // transaction. Delivery is at-least-once: a crash after the fleet call but before the
          // commit rolls back FleetNotifiedAt and the already-notified fast path above no longer
          // applies, so the retry sends the period end again.
  ```

- [ ] **Step 7 (subscription.deleted — transaction outside the gate, audit every attempt, honest comment):** Replace the gated block (currently lines 622–655):
  ```csharp
          if (webhookEvent.FleetNotifiedAt.HasValue == false)
          {
              // The pending-action cleanup, fleet notification, FleetNotifiedAt write, and audit row
              // run in a single transaction so a crash-and-retry cannot re-notify the fleet or
              // duplicate the audit entry.
              await using DataConnectionTransaction transaction = await db.BeginTransactionAsync(ct);

              // Look up the pending action to determine the correct downgrade behavior
              Models.PendingAction? pendingAction = await db.PendingActions
                  .Where(pa => pa.TenantExternalId == customer.TenantExternalId)
                  .FirstOrDefaultAsync(ct);

              BillingAction action = BillingAction.DowngradeToFree;
              if (pendingAction is not null)
              {
                  action = pendingAction.Action.ToBillingAction();

                  // Clean up the pending action after processing
                  await db.PendingActions
                      .Where(pa => pa.TenantExternalId == customer.TenantExternalId)
                      .DeleteAsync(ct);
              }

              await fleetApiClient.SendBillingActionAsync(action, customer.TenantExternalId, null, ct);

              await db.WebhookEvents
                  .Where(e => e.EventId == webhookEvent.EventId)
                  .Set(e => e.FleetNotifiedAt, DateTimeOffset.UtcNow)
                  .UpdateAsync(ct);

              await auditService.LogAsync(customer.TenantExternalId, "subscription_deleted", "webhook", ct: ct);

              await transaction.CommitAsync(ct);
          }
  ```
  with:
  ```csharp
          // The pending-action cleanup, fleet notification, and FleetNotifiedAt write commit
          // atomically so a committed pass is never repeated. Delivery stays at-least-once: a
          // crash after the fleet call but before the commit rolls back the gate and the retry
          // notifies again. The audit row is deliberately outside the gate so every processing
          // attempt, including reprocessed events, is recorded.
          await using DataConnectionTransaction transaction = await db.BeginTransactionAsync(ct);

          if (webhookEvent.FleetNotifiedAt.HasValue == false)
          {
              // Look up the pending action to determine the correct downgrade behavior
              Models.PendingAction? pendingAction = await db.PendingActions
                  .Where(pa => pa.TenantExternalId == customer.TenantExternalId)
                  .FirstOrDefaultAsync(ct);

              BillingAction action = BillingAction.DowngradeToFree;
              if (pendingAction is not null)
              {
                  action = pendingAction.Action.ToBillingAction();

                  // Clean up the pending action after processing
                  await db.PendingActions
                      .Where(pa => pa.TenantExternalId == customer.TenantExternalId)
                      .DeleteAsync(ct);
              }

              await fleetApiClient.SendBillingActionAsync(action, customer.TenantExternalId, null, ct);

              await db.WebhookEvents
                  .Where(e => e.EventId == webhookEvent.EventId)
                  .Set(e => e.FleetNotifiedAt, DateTimeOffset.UtcNow)
                  .UpdateAsync(ct);
          }

          await auditService.LogAsync(customer.TenantExternalId, "subscription_deleted", "webhook", ct: ct);

          await transaction.CommitAsync(ct);
  ```

- [ ] **Step 8 (invoice.payment_failed — same restructure):** Replace (currently lines 686–703):
  ```csharp
          if (webhookEvent.FleetNotifiedAt.HasValue == false)
          {
              // Notify the fleet, mark the event notified, and write the audit row in one
              // transaction so a reprocessed event cannot re-notify or duplicate the audit entry.
              await using DataConnectionTransaction transaction = await db.BeginTransactionAsync(ct);

              await fleetApiClient.SendBillingActionAsync(BillingAction.SetPastDue, customer.TenantExternalId, null, ct);

              await db.WebhookEvents
                  .Where(e => e.EventId == webhookEvent.EventId)
                  .Set(e => e.FleetNotifiedAt, DateTimeOffset.UtcNow)
                  .UpdateAsync(ct);

              await auditService.LogAsync(customer.TenantExternalId, "payment_failed", "webhook",
                  new { invoice.Id }, ct: ct);

              await transaction.CommitAsync(ct);
          }
  ```
  with:
  ```csharp
          // The fleet notification and FleetNotifiedAt write commit atomically so a committed
          // pass is never repeated. Delivery stays at-least-once: a crash after the fleet call
          // but before the commit rolls back the gate and the retry notifies again. The audit
          // row is deliberately outside the gate so every processing attempt is recorded.
          await using DataConnectionTransaction transaction = await db.BeginTransactionAsync(ct);

          if (webhookEvent.FleetNotifiedAt.HasValue == false)
          {
              await fleetApiClient.SendBillingActionAsync(BillingAction.SetPastDue, customer.TenantExternalId, null, ct);

              await db.WebhookEvents
                  .Where(e => e.EventId == webhookEvent.EventId)
                  .Set(e => e.FleetNotifiedAt, DateTimeOffset.UtcNow)
                  .UpdateAsync(ct);
          }

          await auditService.LogAsync(customer.TenantExternalId, "payment_failed", "webhook",
              new { invoice.Id }, ct: ct);

          await transaction.CommitAsync(ct);
  ```

- [ ] **Step 9 (invoice.payment_succeeded — same restructure):** Replace (currently lines 735–752):
  ```csharp
          if (webhookEvent.FleetNotifiedAt.HasValue == false)
          {
              // Notify the fleet, mark the event notified, and write the audit row in one
              // transaction so a reprocessed event cannot re-notify or duplicate the audit entry.
              await using DataConnectionTransaction transaction = await db.BeginTransactionAsync(ct);

              await fleetApiClient.SendBillingActionAsync(BillingAction.SetActive, customer.TenantExternalId, null, ct);

              await db.WebhookEvents
                  .Where(e => e.EventId == webhookEvent.EventId)
                  .Set(e => e.FleetNotifiedAt, DateTimeOffset.UtcNow)
                  .UpdateAsync(ct);

              await auditService.LogAsync(customer.TenantExternalId, "payment_succeeded", "webhook",
                  new { invoice.Id }, ct: ct);

              await transaction.CommitAsync(ct);
          }
  ```
  with:
  ```csharp
          // The fleet notification and FleetNotifiedAt write commit atomically so a committed
          // pass is never repeated. Delivery stays at-least-once: a crash after the fleet call
          // but before the commit rolls back the gate and the retry notifies again. The audit
          // row is deliberately outside the gate so every processing attempt is recorded.
          await using DataConnectionTransaction transaction = await db.BeginTransactionAsync(ct);

          if (webhookEvent.FleetNotifiedAt.HasValue == false)
          {
              await fleetApiClient.SendBillingActionAsync(BillingAction.SetActive, customer.TenantExternalId, null, ct);

              await db.WebhookEvents
                  .Where(e => e.EventId == webhookEvent.EventId)
                  .Set(e => e.FleetNotifiedAt, DateTimeOffset.UtcNow)
                  .UpdateAsync(ct);
          }

          await auditService.LogAsync(customer.TenantExternalId, "payment_succeeded", "webhook",
              new { invoice.Id }, ct: ct);

          await transaction.CommitAsync(ct);
  ```

- [ ] **Step 10 (green):**
  ```bash
  dotnet build test/billing/billing.csproj -c Release --no-incremental
  ./test/billing/bin/Release/net10.0/osx-arm64/billing --treenode-filter "/*/*/WebhookProcessorServiceTests/*"
  dotnet build vord-internal.slnx -c Release
  dotnet run --project test/billing/billing.csproj -c Release
  ```
  Expected: 0 warnings; full suite 479 passed / 13 skipped / 0 failed (four new tests). Also confirm the untouched `WebhookProcessingTests`, `WebhookProcessorConcurrencyTests`, and `WebhookEndpoint*` suites are green in the full run.

- [ ] **Step 11:** Commit:
  ```bash
  cd /Users/jonathanmiller/Repositories/framlux/vord-internal
  git add src/billing-api/Services/WebhookProcessorService.cs test/billing/Services/WebhookProcessorServiceTests.cs
  git -c commit.gpgsign=false commit \
    -m "Make webhook handlers transactional and audit every processing attempt" \
    -m "Each handler now commits its fleet notification, FleetNotifiedAt gate, and database writes in a single transaction, so a committed pass is never repeated and a losing duplicate cannot clobber a newer price via the conditional CurrentPriceId advance. Audit rows are deliberately written outside the gate: every processing attempt, including reprocessed and duplicate deliveries, is recorded. Comments now state the honest contract, which is at-least-once delivery: a crash after the fleet call but before the commit re-notifies on retry." \
    -m "subscription.updated additionally derives the billing interval of the new price from the catalog (falling back to the Stripe recurrence) and advances StripeCustomers.BillingInterval with the price, and a same-tier price change is now audited as interval_changed with no redundant tier dispatch, so interval swaps such as Pro Monthly to Pro Annual leave an audit trail."
  ```
  Verify `git show --stat HEAD` lists exactly the 2 files.

---

### Task 7: T3 — Checkout claim-first race rework with empty-CustomerId repair

**Files:**
- Modify: `src/billing-api/Endpoints/CheckoutEndpoint.cs` (existing-customer branch lines 142–212 of the working tree; new helpers appended before the class close), `test/billing/Endpoints/CheckoutEndpointIntegrationTests.cs` (two new tests appended)
- Commit: those two files only (the `Configure()` rate-limit hunk is already in Task 4's commit; `git add` here picks up the remainder)
- Test: `test/billing/Endpoints/CheckoutEndpointIntegrationTests.cs`

**Interfaces:**
- Produces (private to the endpoint): `internal static readonly TimeSpan BackfillPollDelay` (150 ms), `internal const int BackfillPollAttempts` (4), `private async Task<string?> WaitForCustomerBackfillAsync(string tenantExternalId, CancellationToken ct)`, `private async Task<string> CreateAndBackfillCustomerAsync(ValidatedSession session, CancellationToken ct)`
- Consumes: `UniqueConstraintDetection.IsUniqueConstraintViolation(Exception)` (committed in Task 1), `IStripeGateway.CreateCustomerAsync(CustomerCreateOptions, CancellationToken)` → `Customer`
- New HTTP behavior: 503 + `Retry-After: 2` with an empty `CheckoutResponse` body when the race loser cannot obtain a back-filled customer id within the poll window.

- [ ] **Step 1 (TDD — crash-window self-repair test, fails today):** Append to `CheckoutEndpointIntegrationTests` (before the closing class brace):
  ```csharp
      /// <summary>
      /// A claim row whose CustomerId was never back-filled (a crash between claim and back-fill)
      /// self-repairs on the next checkout: the Stripe customer is created, the id is back-filled,
      /// and the checkout session uses the repaired id rather than an empty customer.
      /// </summary>
      [Test]
      public async Task PostCheckout_PersistedEmptyCustomerIdClaim_SelfRepairsAndProceeds()
      {
          _factory.AuthHandler.NextSession = new ValidatedSession("tenant-crashed-claim", 130, "crash@test.com");

          using (Billing.Api.Database.BillingDatabaseContext db = _factory.CreateDbContext())
          {
              await db.InsertAsync(new StripeCustomer
              {
                  TenantExternalId = "tenant-crashed-claim",
                  CustomerId = string.Empty,
                  BillingInterval = BillingInterval.Monthly,
                  CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
                  UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1)
              });
          }

          _factory.StripeGateway.CreateCustomerAsync(Arg.Any<CustomerCreateOptions>(), Arg.Any<CancellationToken>())
              .Returns(new Customer { Id = "cus_repaired" });

          SessionCreateOptions? capturedOptions = null;
          _factory.StripeGateway.CreateCheckoutSessionAsync(
                  Arg.Do<SessionCreateOptions>(o => capturedOptions = o), Arg.Any<CancellationToken>())
              .Returns(new Session { Id = "cs_repair", Url = "https://checkout.stripe.com/repair" });

          using HttpClient client = _factory.CreateAuthenticatedClient();
          HttpResponseMessage response = await client.PostAsJsonAsync(
              "/api/v1/checkout", new CheckoutRequest { Tier = BillingTier.Pro });

          await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

          await _factory.StripeGateway.Received(1)
              .CreateCustomerAsync(Arg.Any<CustomerCreateOptions>(), Arg.Any<CancellationToken>());

          await Assert.That(capturedOptions).IsNotNull();
          await Assert.That(capturedOptions!.Customer).IsEqualTo("cus_repaired");

          using Billing.Api.Database.BillingDatabaseContext verifyDb = _factory.CreateDbContext();
          StripeCustomer? stored = await verifyDb.StripeCustomers
              .Where(c => c.TenantExternalId == "tenant-crashed-claim")
              .FirstOrDefaultAsync();

          await Assert.That(stored!.CustomerId).IsEqualTo("cus_repaired");
      }
  ```

- [ ] **Step 2 (TDD — race-loser 503 test, fails today):** Append after Step 1's test:
  ```csharp
      /// <summary>
      /// While a concurrent request owns the customer-claim row and has not yet back-filled the
      /// Stripe customer id, the losing request waits briefly and then returns 503 with a
      /// Retry-After header instead of sending an empty customer id to Stripe. The winner is held
      /// inside CreateCustomerAsync by an uncompleted task so the outcome is deterministic.
      /// </summary>
      [Test]
      public async Task PostCheckout_RaceLoserWhileWinnerStillCreating_Returns503Retriable()
      {
          _factory.AuthHandler.NextSession = new ValidatedSession("tenant-race", 120, "race@test.com");

          TaskCompletionSource<Customer> customerCreation = new(TaskCreationOptions.RunContinuationsAsynchronously);
          _factory.StripeGateway.CreateCustomerAsync(Arg.Any<CustomerCreateOptions>(), Arg.Any<CancellationToken>())
              .Returns(_ => customerCreation.Task);
          _factory.StripeGateway.CreateCheckoutSessionAsync(Arg.Any<SessionCreateOptions>(), Arg.Any<CancellationToken>())
              .Returns(new Session { Id = "cs_race", Url = "https://checkout.stripe.com/race" });

          using HttpClient client = _factory.CreateAuthenticatedClient();

          // Start the winner; it claims the row and then blocks inside CreateCustomerAsync.
          Task<HttpResponseMessage> winner = client.PostAsJsonAsync(
              "/api/v1/checkout", new CheckoutRequest { Tier = BillingTier.Pro });

          // Wait until the winner's claim row is visible so the second request is a true race loser.
          bool claimVisible = false;
          for (int i = 0; (i < 100) && (claimVisible == false); i++)
          {
              using Billing.Api.Database.BillingDatabaseContext pollDb = _factory.CreateDbContext();
              StripeCustomer? claim = await pollDb.StripeCustomers
                  .Where(c => c.TenantExternalId == "tenant-race")
                  .FirstOrDefaultAsync();
              claimVisible = claim is not null;
              if (claimVisible == false)
              {
                  await Task.Delay(TimeSpan.FromMilliseconds(50));
              }
          }

          await Assert.That(claimVisible).IsTrue();

          // The loser polls for the back-fill, finds none while the winner is still blocked, and
          // reports 503 so the client retries instead of reaching Stripe with an empty customer.
          HttpResponseMessage loserResponse = await client.PostAsJsonAsync(
              "/api/v1/checkout", new CheckoutRequest { Tier = BillingTier.Pro });

          await Assert.That(loserResponse.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
          await Assert.That(loserResponse.Headers.RetryAfter).IsNotNull();

          // Release the winner; it back-fills the id and completes normally.
          customerCreation.SetResult(new Customer { Id = "cus_winner" });
          HttpResponseMessage winnerResponse = await winner;

          await Assert.That(winnerResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

          using Billing.Api.Database.BillingDatabaseContext db = _factory.CreateDbContext();
          StripeCustomer? stored = await db.StripeCustomers
              .Where(c => c.TenantExternalId == "tenant-race")
              .FirstOrDefaultAsync();

          await Assert.That(stored).IsNotNull();
          await Assert.That(stored!.CustomerId).IsEqualTo("cus_winner");
      }
  ```
  (All usings needed — `System.Net`, `System.Net.Http.Json`, `Framlux.Billing.Api.Auth`, `Framlux.Billing.Api.Models`, `NSubstitute`, `Stripe`, `Stripe.Checkout`, `LinqToDB`, `LinqToDB.Async` — are already present in this file.)

- [ ] **Step 3 (see the red):**
  ```bash
  dotnet build test/billing/billing.csproj -c Release --no-incremental
  ./test/billing/bin/Release/net10.0/osx-arm64/billing --treenode-filter "/*/*/CheckoutEndpointIntegrationTests/*"
  ```
  Expected: the two new tests FAIL (today the crash-window request sends `Customer = ""` without creating a customer, and the race loser returns 200 with `Customer = ""`); all pre-existing tests in the class pass.

- [ ] **Step 4 (implement — constants and customer resolution):** In `src/billing-api/Endpoints/CheckoutEndpoint.cs`:

  (a) Add the poll constants after the `_logger` field declaration (line 56):
  ```csharp
      /// <summary>
      /// The delay between polls while waiting for a concurrent request to back-fill the Stripe
      /// customer id onto the claim row.
      /// </summary>
      internal static readonly TimeSpan BackfillPollDelay = TimeSpan.FromMilliseconds(150);

      /// <summary>
      /// The number of times the claim row is re-read while waiting for a back-fill.
      /// </summary>
      internal const int BackfillPollAttempts = 4;
  ```

  (b) Replace the customer-resolution region (lines 142–212 of the current working tree, from `// Check for existing Stripe customer for this tenant` through the closing brace of the outer `else` block, ending just before `// If the tenant already has an active subscription, ...`):
  ```csharp
          // Check for existing Stripe customer for this tenant
          StripeCustomer? existingCustomer = await _db.StripeCustomers
              .Where(c => c.TenantExternalId == session.TenantExternalId)
              .FirstOrDefaultAsync(ct);

          string stripeCustomerId;

          if (existingCustomer is not null)
          {
              stripeCustomerId = existingCustomer.CustomerId;
          }
          else
          {
              // Claim the tenant row in the database BEFORE creating the Stripe customer. Inserting
              // first means a burst of concurrent requests loses the unique-constraint race here and
              // never reaches the Stripe customer create, so no orphaned Stripe customers are made.
              bool claimedRow;
              try
              {
                  await _db.InsertAsync(new StripeCustomer
                  {
                      TenantExternalId = session.TenantExternalId,
                      CustomerId = string.Empty,
                      BillingInterval = req.Interval,
                      CreatedAt = DateTimeOffset.UtcNow,
                      UpdatedAt = DateTimeOffset.UtcNow
                  }, token: ct);
                  claimedRow = true;
              }
              catch (Exception ex) when (UniqueConstraintDetection.IsUniqueConstraintViolation(ex))
              {
                  claimedRow = false;
              }

              if (claimedRow == false)
              {
                  // A concurrent request already claimed this tenant; reuse its (possibly
                  // still-being-populated) row rather than creating a duplicate Stripe customer.
                  StripeCustomer existingRaceCustomer = await _db.StripeCustomers
                      .Where(c => c.TenantExternalId == session.TenantExternalId)
                      .FirstAsync(ct);

                  stripeCustomerId = existingRaceCustomer.CustomerId;
              }
              else
              {
                  // This request owns the row; create the Stripe customer and back-fill the id.
                  CustomerCreateOptions customerOptions = new CustomerCreateOptions
                  {
                      Email = session.UserEmail,
                      Metadata = new Dictionary<string, string>
                      {
                          { "tenantExternalId", session.TenantExternalId }
                      }
                  };

                  Customer customer = await _stripeGateway.CreateCustomerAsync(customerOptions, ct);
                  stripeCustomerId = customer.Id;

                  await _db.StripeCustomers
                      .Where(c => c.TenantExternalId == session.TenantExternalId)
                      .Set(c => c.CustomerId, stripeCustomerId)
                      .Set(c => c.UpdatedAt, DateTimeOffset.UtcNow)
                      .UpdateAsync(ct);

                  _logger.LogInformation(
                      "Created Stripe customer {CustomerId} for tenant {TenantExternalId}",
                      stripeCustomerId,
                      session.TenantExternalId);
              }
          }
  ```
  with:
  ```csharp
          // Check for existing Stripe customer for this tenant
          StripeCustomer? existingCustomer = await _db.StripeCustomers
              .Where(c => c.TenantExternalId == session.TenantExternalId)
              .FirstOrDefaultAsync(ct);

          string stripeCustomerId;

          if (existingCustomer is not null)
          {
              if (string.IsNullOrEmpty(existingCustomer.CustomerId))
              {
                  // The row was claimed but never back-filled: either a concurrent request is
                  // between claim and back-fill right now, or an earlier request crashed in that
                  // window and left the claim permanently empty. Wait briefly for an in-flight
                  // back-fill, then self-repair by creating the Stripe customer for the claim.
                  stripeCustomerId = await WaitForCustomerBackfillAsync(session.TenantExternalId, ct)
                      ?? await CreateAndBackfillCustomerAsync(session, ct);
              }
              else
              {
                  stripeCustomerId = existingCustomer.CustomerId;
              }
          }
          else
          {
              // Claim the tenant row in the database BEFORE creating the Stripe customer. Inserting
              // first means a burst of concurrent requests loses the unique-constraint race here and
              // never reaches the Stripe customer create, so no orphaned Stripe customers are made.
              bool claimedRow;
              try
              {
                  await _db.InsertAsync(new StripeCustomer
                  {
                      TenantExternalId = session.TenantExternalId,
                      CustomerId = string.Empty,
                      BillingInterval = req.Interval,
                      CreatedAt = DateTimeOffset.UtcNow,
                      UpdatedAt = DateTimeOffset.UtcNow
                  }, token: ct);
                  claimedRow = true;
              }
              catch (Exception ex) when (UniqueConstraintDetection.IsUniqueConstraintViolation(ex))
              {
                  claimedRow = false;
              }

              if (claimedRow == false)
              {
                  // A concurrent request already claimed this tenant; wait for it to back-fill
                  // the Stripe customer id rather than creating a duplicate Stripe customer.
                  string? backfilledCustomerId = await WaitForCustomerBackfillAsync(session.TenantExternalId, ct);

                  if (backfilledCustomerId is null)
                  {
                      // The winning request has not back-filled within the wait window. Tell the
                      // client to retry shortly instead of sending an empty customer id to Stripe.
                      _logger.LogWarning(
                          "Tenant {TenantExternalId} lost the customer-claim race and the winning request has not back-filled yet",
                          session.TenantExternalId);
                      HttpContext.Response.StatusCode = 503;
                      HttpContext.Response.Headers.RetryAfter = "2";
                      await HttpContext.Response.WriteAsJsonAsync(new CheckoutResponse(), cancellationToken: ct);

                      return;
                  }

                  stripeCustomerId = backfilledCustomerId;
              }
              else
              {
                  // This request owns the row; create the Stripe customer and back-fill the id.
                  stripeCustomerId = await CreateAndBackfillCustomerAsync(session, ct);
              }
          }
  ```

- [ ] **Step 5 (implement — the two helpers):** Append inside the `CheckoutEndpoint` class, after the closing brace of `HandleAsync` and before the class's closing brace:
  ```csharp
      /// <summary>
      /// Re-reads the claim row for the tenant until its CustomerId has been back-filled by the
      /// request that owns the claim, or the poll budget is exhausted.
      /// </summary>
      /// <param name="tenantExternalId">The tenant whose claim row is being watched.</param>
      /// <param name="ct">Cancellation token.</param>
      /// <returns>The back-filled Stripe customer id, or null if it never appeared.</returns>
      private async Task<string?> WaitForCustomerBackfillAsync(string tenantExternalId, CancellationToken ct)
      {
          for (int attempt = 0; attempt < BackfillPollAttempts; attempt++)
          {
              StripeCustomer? row = await _db.StripeCustomers
                  .Where(c => c.TenantExternalId == tenantExternalId)
                  .FirstOrDefaultAsync(ct);

              if (string.IsNullOrEmpty(row?.CustomerId) == false)
              {
                  return row!.CustomerId;
              }

              await Task.Delay(BackfillPollDelay, ct);
          }

          return null;
      }

      /// <summary>
      /// Creates the Stripe customer for a claimed row and back-fills its id. The back-fill only
      /// applies while the row's CustomerId is still empty; if another request repaired or
      /// back-filled the row first, its id is adopted and the newly created Stripe customer is
      /// abandoned, which is harmless because it carries no subscription.
      /// </summary>
      /// <param name="session">The validated session identifying the tenant and user.</param>
      /// <param name="ct">Cancellation token.</param>
      /// <returns>The Stripe customer id now recorded on the tenant's row.</returns>
      private async Task<string> CreateAndBackfillCustomerAsync(ValidatedSession session, CancellationToken ct)
      {
          CustomerCreateOptions customerOptions = new CustomerCreateOptions
          {
              Email = session.UserEmail,
              Metadata = new Dictionary<string, string>
              {
                  { "tenantExternalId", session.TenantExternalId }
              }
          };

          Customer customer = await _stripeGateway.CreateCustomerAsync(customerOptions, ct);

          int backfilled = await _db.StripeCustomers
              .Where(c => c.TenantExternalId == session.TenantExternalId &&
                          (c.CustomerId == string.Empty))
              .Set(c => c.CustomerId, customer.Id)
              .Set(c => c.UpdatedAt, DateTimeOffset.UtcNow)
              .UpdateAsync(ct);

          if (backfilled == 0)
          {
              // Another request back-filled or repaired the row first; converge on its customer
              // id so all requests for this tenant use a single Stripe customer.
              StripeCustomer adopted = await _db.StripeCustomers
                  .Where(c => c.TenantExternalId == session.TenantExternalId)
                  .FirstAsync(ct);

              _logger.LogWarning(
                  "Discarding Stripe customer {OrphanCustomerId} for tenant {TenantExternalId}; adopting {CustomerId} back-filled by a concurrent request",
                  customer.Id,
                  session.TenantExternalId,
                  adopted.CustomerId);

              return adopted.CustomerId;
          }

          _logger.LogInformation(
              "Created Stripe customer {CustomerId} for tenant {TenantExternalId}",
              customer.Id,
              session.TenantExternalId);

          return customer.Id;
      }
  ```

- [ ] **Step 6 (green):**
  ```bash
  dotnet build test/billing/billing.csproj -c Release --no-incremental
  ./test/billing/bin/Release/net10.0/osx-arm64/billing --treenode-filter "/*/*/CheckoutEndpointIntegrationTests/*"
  dotnet build vord-internal.slnx -c Release
  dotnet run --project test/billing/billing.csproj -c Release
  ```
  Expected: 0 warnings; class green including both new tests; full suite 481 passed / 13 skipped / 0 failed.

- [ ] **Step 7:** Commit (staging the whole files is now correct: the index already contains the `Configure()` rate-limit line from Task 4, so this commit's diff is exactly the claim-first rework, the repair paths, and the checkout-metadata interval):
  ```bash
  cd /Users/jonathanmiller/Repositories/framlux/vord-internal
  git add src/billing-api/Endpoints/CheckoutEndpoint.cs test/billing/Endpoints/CheckoutEndpointIntegrationTests.cs
  git -c commit.gpgsign=false commit \
    -m "Claim the tenant row before creating the Stripe customer at checkout" \
    -m "Checkout now inserts the StripeCustomer row first with an empty customer id, claiming the tenant via the unique constraint, then creates the Stripe customer and back-fills the id. Concurrent bursts lose the race before reaching Stripe, so orphaned Stripe customers and the delete-orphan cleanup path are gone. A race loser that reads the claim before the back-fill polls briefly for the id and answers 503 with Retry-After rather than sending an empty customer to Stripe, and a claim row left empty by a crash between claim and back-fill self-repairs on the next checkout via a conditional back-fill that adopts a concurrent winner's id when beaten. The chosen billing interval is persisted on the claim row and carried in checkout-session metadata for the webhook handler."
  ```
  Verify `git show --stat HEAD` lists exactly the 2 files.

- [ ] **Step 8 (final verification):**
  ```bash
  cd /Users/jonathanmiller/Repositories/framlux/vord-internal
  git status
  git log --oneline -7
  dotnet build vord-internal.slnx -c Release
  dotnet run --project test/billing/billing.csproj -c Release
  ```
  Expected: `git status` shows NO modified tracked files (only unrelated untracked items such as `.superpowers/`, if any); seven new commits in the order T4, T7, T5, T6, T2, T1, T3; build 0 warnings; 481 passed / 13 skipped / 0 failed.

- [ ] **Step 9 (completion summary for Jonathan — include these items verbatim in the final report):**
  1. **CORS deploy gate (must check before deploying):** the T7 commit makes billing-api crash-loop on startup in any environment whose `Cors:Origins` is empty, missing, or contains a wildcard. The kube manifests are NOT in this repo — verify the production (and any staging) configuration supplies at least one explicit, wildcard-free origin before rolling these commits out.
  2. The webhook contract is now documented and tested as at-least-once: a crash after fleet-notify but before commit re-notifies on retry; audit rows are written on every processing attempt by design, so duplicate deliveries produce duplicate audit rows deliberately.
  3. Same-tier price swaps no longer dispatch a redundant idempotent tier upgrade to the fleet; they audit as `interval_changed` instead. If the fleet was relying on that redundant dispatch for anything, flag it.
  4. The race-loser checkout path returns 503 + Retry-After; billing-web should treat 503 from POST /checkout as retriable (it previously only saw 200/401/500).
