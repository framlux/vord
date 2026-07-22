# Small Consolidations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The remaining audit findings: billing endpoint DTO/preamble dedup, the telemetry parser's 12 identical catch blocks, health-threshold quadruplication (a real drift hazard), the vestigial `RateLimiter` inheritance, `RetryHelper`→Polly, invitation result-type merge, CPU/Memory history endpoint merge, integration endpoint gates, and relocating test doubles out of the production assembly.

**Architecture:** Each task is independent; do them in any order. None changes a wire format — the billing task explicitly preserves the (admittedly redundant) `{success, data:{success, message}}` envelope because flattening it is a coordinated `src/web` change Jonathan deferred.

**Tech Stack:** .NET 10, FastEndpoints, Polly, TUnit.

## Global Constraints

See [README.md](README.md#global-constraints). Run after plans 1–3 (billing endpoints will already use `SendApiErrorAsync` and the `RequiresTenant` tag, so their remaining duplication is exactly what Task 1 removes).

---

### Task 1: Billing action endpoints — shared response DTO + shared preamble

**Files:**
- Create: `src/server/Endpoints/Web/Billing/BillingActionResponse.cs`
- Modify: `CancelSubscriptionEndpoint.cs`, `ResumeSubscriptionEndpoint.cs`, `ReactivateSubscriptionEndpoint.cs`, `DowngradeSubscriptionEndpoint.cs`, `IntegrationTestEndpoint.cs` (all under `src/server/Endpoints/Web/`)
- Test: existing billing functional tests (assertions unchanged — that is the proof)

**Interfaces:**
- Produces: one `BillingActionResponse { bool Success; string Message }` replacing the five per-endpoint copies (`CancelSubscriptionResponse` at `CancelSubscriptionEndpoint.cs:19-26`, etc.). Property names are identical across all five today, so the wire format is untouched.

- [ ] **Step 1: Create the shared DTO** (same two properties, XML-doc'd, license header) and delete the five per-endpoint response classes, updating each endpoint's generic parameters (`EndpointWithoutRequest<ApiResponse<BillingActionResponse>>`).
- [ ] **Step 2: Extract the shared preamble.** After plans 2–3, each of the four subscription-action endpoints still repeats: billing-disabled → 404 `"Billing is not enabled"`, then subscription-null → 404 `"Subscription not found"`. Add one `internal static` helper in a new `BillingEndpointGuards.cs` (co-located in the Billing folder) that both loads and gates, so it is unit-testable per CLAUDE.md's endpoint-logic rule:

```csharp
internal static class BillingEndpointGuards
{
    /// <summary>
    /// Runs the shared billing-action preamble: 404 when billing is disabled, 404 when the
    /// tenant has no subscription. Returns the subscription when the request may proceed,
    /// or null after having written the error response.
    /// </summary>
    internal static async Task<TenantSubscription?> LoadGatedSubscriptionAsync(
        HttpContext httpContext,
        IBillingStatus billingStatus,
        ISubscriptionService subscriptionService,
        int tenantId,
        CancellationToken ct)
    {
        if (billingStatus.IsEnabled == false)
        {
            await httpContext.SendApiErrorAsync(404, "Billing is not enabled", ct);

            return null;
        }

        TenantSubscription? subscription = await subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
        if (subscription is null)
        {
            await httpContext.SendApiErrorAsync(404, "Subscription not found", ct);

            return null;
        }

        return subscription;
    }
}
```

(If plan 6 already removed `IBillingStatus`, the parameter is the concrete `BillingStatus`.) Each endpoint's preamble becomes:

```csharp
TenantSubscription? subscription = await BillingEndpointGuards.LoadGatedSubscriptionAsync(
    HttpContext, _billingStatus, _subscriptionService, tenantId, ct);
if (subscription is null)
{
    return;
}
```

- [ ] **Step 3: Unit-test the guard** (billing disabled → 404 written + null; no subscription → 404 + null; subscription present → returned, nothing written) using `DefaultHttpContext` + substitutes.
- [ ] **Step 4: Run** `dotnet build machine-info.slnx && dotnet run --project test/functional/web/functional.web.csproj -- --treenode-filter "*Billing*" && dotnet run --project test/unit/server/unit.server.csproj -- --treenode-filter "*BillingEndpointGuards*"` — all existing billing functional tests must pass **without assertion changes**.

---

### Task 2: `TelemetryPayloadParser` — one generic try-wrapper

**Files:**
- Modify: `src/services.core/Services/Machines/Projection/TelemetryPayloadParser.cs:22-355`
- Test: its existing test file (unchanged — the refactor is internal)

- [ ] **Step 1: Capture the before-state:** run its unit-test filter, note the count.
- [ ] **Step 2: Add one private generic wrapper and collapse the 12 copies:**

```csharp
private static bool TryParse<T>(string payload, Func<JsonElement, T> parse, out T? result)
{
    result = default;

    try
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        result = parse(document.RootElement);

        return true;
    }
    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
    {
        return false;
    }
}
```

**Before writing this, read one existing `TryParseX` end-to-end** and copy its exact exception filter list and its `JsonDocument`/`JsonElement` handling — the list above is representative, the file's actual filter is authoritative. Each public `TryParseX` becomes a one-line delegation to `TryParse(payload, ParseXCore, out result)` with the per-type mapping extracted into a private `static XFragment ParseXCore(JsonElement root)`.

- [ ] **Step 3: Run the same filter** — identical count, all pass. Malformed-payload tests (there are some — the catch blocks exist for them) prove the filter behavior is preserved.

---

### Task 3: Health thresholds — single source of truth + fix the overwrite

**Files:**
- Modify: `src/services.core/Services/Machines/MachineSearchService.cs:197-249`
- Reference: `src/services.core/Services/Infrastructure/HealthComputer.cs:25-70` (the canonical thresholds; do not move it)
- Test: `MachineSearchServiceTests.cs` + a new regression test

This one is behavioral, not just cosmetic: `BuildDtos` sets `HealthStatus` from the DB-computed column, then `EnrichWithHealth` overwrites it with a local recomputation that hard-codes the 95/80 thresholds a third time. If the thresholds ever drift, search results disagree with the dashboard.

- [ ] **Step 1: Write the failing regression test:** construct a search row whose DB-computed health differs from what the local thresholds would produce (e.g. DB says Critical, raw metrics say Healthy) and assert the service result keeps the Redis-online override behavior but otherwise **delegates to `HealthComputer`** (read `HealthComputer`'s public API first and assert against its actual output for the same inputs — the intent to pin: exactly one implementation decides health).
- [ ] **Step 2: Rewrite `EnrichWithHealth`** to call `HealthComputer` instead of its inline thresholds, keeping only the Redis-online override logic. Delete the local threshold constants.
- [ ] **Step 3: Run** `MachineSearchService` unit filter + functional `"*MachineSearch*"` / `"*Search*"` — all pass. Note in the summary that the two SQL dialect copies (`PostgresSqlDialect`, `SqliteSqlDialect`) still hold the thresholds for the DB-side computation; add a comment in `HealthComputer` naming both files so future threshold changes touch all three knowingly.

---

### Task 4: Flatten `RedisFixedWindowRateLimiter`

**Files:**
- Modify: `src/server/Services/Infrastructure/RedisRateLimiter.cs`
- Test: `test/unit/server/.../RedisRateLimiterTests.cs` (delete the dead-surface property/disposal tests at line 343+)

All three production consumers call `IsAllowedAsync(partitionKey)` directly; the `System.Threading.RateLimiting.RateLimiter` base-class surface is vestigial and its `AcquireAsyncCore` hardcodes partition `"unknown"` — a shared-bucket landmine if ever used.

- [ ] **Step 1: Verify consumers:** `/usr/bin/grep -rn "RedisFixedWindowRateLimiter" src --include='*.cs'` — expect construction sites plus `IsAllowedAsync` callers (`CallbackRateLimitMiddleware.cs:61`, `GrpcRateLimitingInterceptor.cs:28`) and the `RedisPartitionedRateLimiter` wrapper. Confirm **only** `RedisPartitionedRateLimiter` relies on the `RateLimiter` contract.
- [ ] **Step 2: Make it a plain `sealed` class:** drop the base class, delete `AttemptAcquireCore`/`AcquireAsyncCore`, the private `RedisRateLimitLease` type, and the `Dispose` overrides; keep `IsAllowedAsync` and the window logic byte-identical. `RedisPartitionedRateLimiter` (the legitimate `RateLimiter` adapter) stays as is — if it wrapped the flattened class through the base type, adapt it to call `IsAllowedAsync`.
- [ ] **Step 3: Delete the dead-surface tests; keep every `IsAllowedAsync` window-behavior test.** Run `"*RedisRateLimiter*"` unit filter + `dotnet build` — pass, 0 warnings.

---

### Task 5: `RetryHelper` → the existing Polly pipeline

**Files:**
- Delete: `src/services.core/Services/Infrastructure/RetryHelper.cs` (+ its test file)
- Modify: `src/services.core/Services/Machines/RedisMachinePingService.cs:37,148` (its only two call sites)
- Reference: `src/services.core/Extensions/ServiceCollectionExtensions.cs:184-193` (the existing `ResiliencePipelineBuilder` registration — reuse its registration pattern)

- [ ] **Step 1: Read the two call sites and `RetryHelper`'s policy** (attempt count, delay, exception filter) and register an equivalent named `ResiliencePipeline` (e.g. key `"redis-ping"`) alongside the existing builder at `ServiceCollectionExtensions.cs:184-193`, with identical retry semantics.
- [ ] **Step 2: Inject `ResiliencePipelineProvider<string>` (or the keyed pipeline) into `RedisMachinePingService`** and replace both `RetryHelper` calls with `pipeline.ExecuteAsync(...)`. Update its unit tests' constructor arrangements.
- [ ] **Step 3: Delete `RetryHelper` + tests; build + run services.core unit suite.** Expected: pass; retry semantics pinned by the existing ping-service tests (if none assert retry-on-transient-failure, add one).

---

### Task 6: Merge invitation result types

**Files:**
- Modify: `src/services.core/Services/Handlers/` — `InvitationCreateResult`/`InvitationResendResult` (verified field-for-field identical: Id, Email, Token, AcceptUrl, ExpiresAt, Status, ErrorMessage) merge into one `InvitationDeliveryResult`; `InvitationRevokeResult` (single `string? ErrorMessage` field) folds into `ServiceResult`'s `ErrorMessage`
- Modify: `InvitationHandler.cs` signatures + the invitation endpoints consuming them
- Test: `InvitationHandlerTests.cs`, invitation functional tests (assertions unchanged)

- [ ] **Step 1:** Rename/merge: keep one class named `InvitationDeliveryResult` (new file, delete the two old files), update `InvitationHandler.CreateAsync`/`ResendAsync` return types and both endpoints' mapping code. The duplicated 6-field response mapping in `InvitationCreateEndpoint.cs:128-136` / `InvitationResendEndpoint.cs:88-96` becomes one `internal static InvitationResponse ToResponse(InvitationDeliveryResult result)` used by both.
- [ ] **Step 2:** Replace `InvitationRevokeResult` with `ServiceResult<ApiResponse<object>>` (the pattern the handler's other methods already use — match `MemberHandler.RemoveAsync`'s signature style).
- [ ] **Step 3:** Run invitation unit + functional filters — pass, no assertion changes.

---

### Task 7: History endpoint merge + integration endpoint gates

**Files:**
- Modify: `src/server/Endpoints/Web/Machines/History/CpuHistoryEndpoint.cs`, `MemoryHistoryEndpoint.cs` (98 lines each, differ in exactly 7)
- Modify: `src/server/Endpoints/Web/Integrations/IntegrationCreateEndpoint.cs`

- [ ] **Step 1 (history):** Extract the shared body into an `internal static class ScalarHistoryHandler` with `HandleScalarHistoryAsync<TPayload>(byte telemetryTypeId, Func<TPayload, double> select, ...)` capturing the common validate→fetch→aggregate flow; both endpoints become thin delegations passing their `TelemetryTypeIds` constant and property selector. Leave Disk/Service/Ssh history endpoints alone (genuinely different shapes). Run `"*History*"` unit + functional filters — identical results.
- [ ] **Step 2 (integrations):** In `IntegrationCreateEndpoint`: add `Tags(EndpointTags.RequiresProSubscription)` and delete the inline Pro/Team gate at lines 96–104 (`ProSubscriptionPreProcessor` handles it — but **first check the response contract**: the pre-processor returns 403 with `RequiresProMessage`; if the endpoint's inline gate returned a different message that a functional test asserts, update that test's expected message and flag it in the summary). Delete the handler branches unreachable behind `CreateIntegrationValidator` (provider-parse-failure at 116–123, name-length at 150–157) — keep the enum re-parse for its value.
- [ ] **Step 3:** Run `"*Integration*"` functional filter + full unit server suite — pass.

---

### Task 8: Move test doubles out of the production assembly

**Files:**
- Move: `src/services.core/Services/Infrastructure/SqliteSqlDialect.cs` → `test/shared/` (adjust namespace to match `test/shared` siblings, e.g. next to the existing `NoOpSqlDialect`)
- Move: `src/services.core/Services/Infrastructure/NoOpAdvisoryLockProvider.cs` → `test/shared/`
- Modify: `test/shared/FunctionalTestFactory.cs:268` (using/namespace update)

- [ ] **Step 1: Verify production never references them:** `/usr/bin/grep -rn "SqliteSqlDialect\|NoOpAdvisoryLockProvider" src --include='*.cs'` — expect only their own declarations. Any production DI registration → stop, this task doesn't apply.
- [ ] **Step 2: Move both files** (keep license headers; new namespace per `test/shared` convention), fix `FunctionalTestFactory` usings, confirm `test/shared` has the needed project references (it references `services.core` already — the dialect implements `ISqlDialect` from there).
- [ ] **Step 3: Build + run functional web suite** (its SQLite host exercises both doubles at startup). Expected: pass.

---

### Task 9: `SubscriptionService` — one limit-resolution path

**Files:**
- Modify: `src/services.core/Services/Billing/SubscriptionService.cs`
- Test: `test/unit/services.core/Services/Billing/SubscriptionServiceTests.cs`

`GetEffectiveLimitAsync` (lines 307–335, three `Func<>` selector parameters) duplicates the tier/override coalescing that `GetEffectiveLimitsForTenantAsync` (lines 270–301) already performs; the four `Can*` methods (lines 89–94, 204–209, 226–231, 248–253) each call it with a lambda triple.

- [ ] **Step 1: Capture the before-state:** run the `"*SubscriptionService*"` unit filter and note the passing count — the existing `Can*` tests (limit reached / under limit / override precedence) are the regression net for this refactor; no assertion may change.
- [ ] **Step 2: Rewrite each `Can*` method** to call `GetEffectiveLimitsForTenantAsync` once and read the single field it needs (e.g. `limits.MachineLimit`), then delete `GetEffectiveLimitAsync` and its selector plumbing. Preserve the missing-tier warning log currently at line 332 by confirming `GetEffectiveLimitsForTenantAsync` emits an equivalent warning — if it does not, move that log statement into it as part of this step.
- [ ] **Step 3: Run the same filter** — identical count, all pass; ~40 LOC removed.

---

## Exit criteria

1. One `BillingActionResponse`; zero per-endpoint `{Success, Message}` response classes;
   billing functional tests pass unchanged (wire format proven stable).
2. `TelemetryPayloadParser` contains exactly one catch-filter block; parser test count identical.
3. Health thresholds exist in exactly one C# implementation (`HealthComputer`) + the two
   documented SQL dialect copies; new regression test pins the delegation.
4. `RedisFixedWindowRateLimiter` no longer inherits `RateLimiter`; window-behavior tests intact.
5. `RetryHelper` deleted; ping-service retry semantics covered by a test.
6. One invitation result type; revoke uses `ServiceResult`.
7. CPU/Memory history endpoints delegate to one shared handler; `src/services.core` and
   `src/server` contain no test-only doubles.
8. `SubscriptionService` has exactly one effective-limit resolution path
   (`GetEffectiveLimitsForTenantAsync`); `GetEffectiveLimitAsync` is gone.
9. `dotnet build machine-info.slnx` — 0 errors, 0 warnings; all six TUnit projects +
   functional suites pass. ~740 LOC removed.
