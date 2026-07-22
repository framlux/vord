# Deferred Decisions Batch 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Execute four owner-approved cleanups from the deferred-decisions list: delete the one-time migration jobs, drop the dead Stripe price-ID fallback, adopt authorization-policy name constants, and flatten the billing action double-wrapped response.

**Architecture:** All four items are independent. Tasks 1–2 are pure deletions (no behavior change reachable in any environment). Task 3 is a mechanical constant adoption guarded by a new registration-parity test. Task 4 is a deliberate wire-format change to the four billing action endpoints, coordinated with the SvelteKit client.

**Tech Stack:** .NET 10 / FastEndpoints / TUnit (run as executables, never `dotnet test`) / SvelteKit + Vitest.

## Global Constraints

- Owner rulings (2026-07-22): migration jobs DELETED (no production data yet); price-ID fallback DROPPED; policy constants ADOPTED; double-wrap FLATTENED (wire change approved). Projection sharding is KEPT — do not touch it.
- Commits allowed, one per task: `git -c commit.gpgsign=false commit` with explicit file lists. **Never stage `nuget.config`** (its billinggrpc-local line is held for the 1.15.0 publish). No AI attribution, no plan/task numbers in code, comments, or commit messages.
- `dotnet build machine-info.slnx` must end with **0 warnings** after every task.
- Test commands (TUnit gotcha: `dotnet run --no-build` can replay stale results — build with `--no-incremental` and run the compiled executable):
  ```bash
  dotnet build machine-info.slnx --no-incremental
  test/unit/server/bin/Debug/net10.0/osx-arm64/unit.server
  test/unit/services.core/bin/Debug/net10.0/osx-arm64/unit.services.core
  test/unit/database/bin/Debug/net10.0/osx-arm64/unit.database
  test/functional/web/bin/Debug/net10.0/osx-arm64/functional.web
  test/functional/grpc/bin/Debug/net10.0/osx-arm64/functional.grpc
  test/functional/hangfire/bin/Debug/net10.0/osx-arm64/functional.hangfire
  # subset: append --treenode-filter "/*/*/ClassName/*"
  pnpm -C src/web check && pnpm -C src/web test && pnpm -C src/web build
  ```
- Style (`.editorconfig`, error-level): no `var`; Allman braces; file-scoped namespaces; `_camelCase` fields; XML docs on public members; alphabetical `using`s; blank line before `return` (except after a comment); no `!boolean` — compare explicitly; no null-forgiving `!`; parenthesize compound conditions; one type per file; blank line at end of file; license header on every new file:
  ```
  // Copyright (c) 2026 Framlux LLC
  // Licensed under the Functional Source License, Version 1.1, ALv2 Future License
  // See LICENSE for details.
  ```
- Deleted production code takes its orphaned tests with it. New shared code needs unit tests and, where it touches the HTTP surface, functional tests.
- No schema changes, no new migrations.
- Suite baselines before this plan: unit.server 1003, unit.services.core 1514, unit.database 478, functional.web 735, functional.grpc 94, functional.hangfire 2, Vitest 424. Counts will move only by the deltas each task states.

---

### Task 1: Delete the one-time migration jobs

Both jobs were startup migrations for deployments that predate the current schema/queue layout. The owner ruled there is no production data, so neither can ever have work to do.

**Files:**
- Delete: `src/services.core/Hangfire/LegacyRedisKeyCleanup.cs` (154 lines)
- Delete: `src/services.core/Services/Security/EncryptLegacyTenantOidcSecretsJob.cs` (97 lines)
- Delete: `test/unit/services.core/Hangfire/LegacyRedisKeyCleanupTests.cs`
- Delete: `test/unit/services.core/Services/Security/EncryptLegacyTenantOidcSecretsJobTests.cs`
- Modify: `src/services.worker/Program.cs` (registrations at ~66–72, both fire-and-forget run blocks at ~91–137)

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: nothing — later tasks do not reference these types.

- [ ] **Step 1: Delete the four files**

```bash
git rm src/services.core/Hangfire/LegacyRedisKeyCleanup.cs \
       src/services.core/Services/Security/EncryptLegacyTenantOidcSecretsJob.cs \
       test/unit/services.core/Hangfire/LegacyRedisKeyCleanupTests.cs \
       test/unit/services.core/Services/Security/EncryptLegacyTenantOidcSecretsJobTests.cs
```

- [ ] **Step 2: Remove the wiring from `src/services.worker/Program.cs`**

Remove these two registration blocks (comments included):

```csharp
// One-shot startup task — keep singleton; it has no per-request state.
builder.Services.AddSingleton<LegacyRedisKeyCleanup>();

// One-shot legacy OIDC secret migration job. Scoped because it depends on the scoped
// ITenantRepository.
builder.Services.AddScoped<EncryptLegacyTenantOidcSecretsJob>();
```

Remove both fire-and-forget blocks after the `RecurringJobRegistry.RegisterAll` scope, each starting with its lead comment (`// One-time idempotent flush of Redis keys ...` / `// One-time encryption of legacy plaintext OIDC client secrets ...`) and ending with the closing `});` of its `Task.Run`. Nothing else between them moves. If removing them orphans a `using` (e.g. for `Framlux.FleetManagement.Services.Core.Hangfire` types no longer referenced — note `HangfireSchemaReadinessProbe` in the same namespace IS still used), leave usings that still have references and delete only truly orphaned ones.

- [ ] **Step 3: Verify no references remain**

```bash
grep -rn "LegacyRedisKeyCleanup\|EncryptLegacyTenantOidcSecrets" src test --include='*.cs' | grep -v obj
```
Expected: no output.

- [ ] **Step 4: Build and run affected suites**

```bash
dotnet build machine-info.slnx --no-incremental   # 0 errors, 0 warnings
test/unit/services.core/bin/Debug/net10.0/osx-arm64/unit.services.core
test/functional/hangfire/bin/Debug/net10.0/osx-arm64/functional.hangfire
```
Expected: unit.services.core drops from 1514 by exactly the number of tests the two deleted test files contained; hangfire 2/2. Record the new count.

- [ ] **Step 5: Commit**

```bash
git add src/services.core/Hangfire src/services.core/Services/Security \
        src/services.worker/Program.cs test/unit/services.core
git -c commit.gpgsign=false commit -m "Delete the one-time legacy Redis and OIDC-secret migration jobs

Both startup jobs migrated state from deployments that predate the current
queue layout and encryption-on-write path. No deployed environment carries
that legacy state, so the jobs, their worker wiring, and their tests are
removed."
```

---

### Task 2: Drop the Stripe price-ID fallback

Tier resolution during the periodic Stripe drift check keeps exactly one path: the tier billing-api reports (backed by its `TierMappings` table). The config-based price-ID comparison can never match because none of the six `BillingOptions` price-ID properties is set in any environment.

**Files:**
- Modify: `src/services.core/Services/Billing/StripeSyncJob.cs` (locals at ~81–82, `SyncTierAsync` signature at ~145, call site at ~152, `MapPriceIdToTier` at ~256–290)
- Modify: `src/services.core/Options/BillingOptions.cs` (delete the six `Stripe*PriceId` properties at ~25–50)
- Modify: `test/unit/services.core/Services/Billing/StripeSyncJobTests.cs` (delete `MapPriceIdToTier_*` tests, fixture price-ID assignments at ~438–439 and ~483–484, and update direct `SyncTierAsync` callers)

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: `SyncTierAsync(TenantSubscription subscription, StripeSubscriptionStatus stripeStatus, CancellationToken ct)` — two string parameters removed. No other task calls it.

- [ ] **Step 1: Remove the fallback from `StripeSyncJob.cs`**

In the run loop, delete the two locals:

```csharp
string proPriceId = _billingOptions.StripeProPriceId;
string teamPriceId = _billingOptions.StripeTeamPriceId;
```

and drop the corresponding arguments from the `SyncTierAsync` call. Change the method signature from

```csharp
internal async Task SyncTierAsync(
    TenantSubscription subscription,
    StripeSubscriptionStatus stripeStatus,
    string proPriceId, string teamPriceId, CancellationToken ct)
```

to

```csharp
internal async Task SyncTierAsync(
    TenantSubscription subscription,
    StripeSubscriptionStatus stripeStatus,
    CancellationToken ct)
```

Replace the two-step resolution

```csharp
SubscriptionTier? stripeTier = MapBillingTierToSubscriptionTier(stripeStatus.Tier);
if (stripeTier is null)
{
    stripeTier = MapPriceIdToTier(stripeStatus.PriceId, proPriceId, teamPriceId);
}
```

with

```csharp
SubscriptionTier? stripeTier = MapBillingTierToSubscriptionTier(stripeStatus.Tier);
```

(the existing `if (stripeTier is null) { return; }` guard below stays). Delete the whole `MapPriceIdToTier` method. Keep `MapBillingTierToSubscriptionTier` and the Free-tier safety guard untouched.

- [ ] **Step 2: Delete the six price-ID properties from `BillingOptions.cs`**

Remove `StripeProPriceId`, `StripeTeamPriceId`, `StripeProMonthlyPriceId`, `StripeProAnnualPriceId`, `StripeTeamMonthlyPriceId`, `StripeTeamAnnualPriceId` with their XML docs. `Enabled`, `GrpcUrl`, and every other member stay.

- [ ] **Step 3: Update `StripeSyncJobTests.cs`**

Delete every `MapPriceIdToTier_*` test. Remove the `StripeProPriceId = ProPriceId` / `StripeTeamPriceId = TeamPriceId` assignments from test fixtures and any now-unused `ProPriceId`/`TeamPriceId` constants. Update direct `SyncTierAsync(...)` calls (e.g. `SyncTierAsync_StripeReturnsFreeTier_DoesNotDowngradePaidSubscription`) to the two-parameter-shorter signature. Tests that drive tier drift through `stripeStatus.Tier` (`RunAsync_TierDrift_CorrectsViaWebhookHandler`, `RunAsync_TierMatches_NoCorrection`) must remain and stay green — they pin the surviving resolution path.

- [ ] **Step 4: Verify no references remain**

```bash
grep -rn "MapPriceIdToTier\|StripeProPriceId\|StripeTeamPriceId\|StripeProMonthlyPriceId\|StripeProAnnualPriceId\|StripeTeamMonthlyPriceId\|StripeTeamAnnualPriceId" src test --include='*.cs' | grep -v obj
```
Expected: no output.

- [ ] **Step 5: Build and run the suite**

```bash
dotnet build machine-info.slnx --no-incremental   # 0 errors, 0 warnings
test/unit/services.core/bin/Debug/net10.0/osx-arm64/unit.services.core
```
Expected: green; count drops by exactly the number of deleted `MapPriceIdToTier_*` tests.

- [ ] **Step 6: Commit**

```bash
git add src/services.core/Services/Billing/StripeSyncJob.cs \
        src/services.core/Options/BillingOptions.cs \
        test/unit/services.core/Services/Billing/StripeSyncJobTests.cs
git -c commit.gpgsign=false commit -m "Drop the config-based Stripe price-ID tier fallback

Tier resolution during the drift check now uses only the tier billing-api
reports from its TierMappings table. The appsettings price-ID comparison
was unreachable — none of the six BillingOptions price-ID properties has
ever been set in any environment — so the fallback and its config surface
are removed."
```

---

### Task 3: Adopt authorization-policy name constants

73 endpoint `Configure()` calls pass policy names as raw strings (`Policies("TenantAdmin")` ×32, `"ViewOnly"` ×25, `"MachineAdmin"` ×12, `"Admin"` ×4), and `Program.cs` registers the same four by string. A typo today authorizes nothing and fails only at runtime. Constants make it a compile error, and a parity test makes an unregistered constant a test failure.

**Files:**
- Create: `src/server/Auth/AuthorizationPolicies.cs`
- Test: `test/functional/web/Auth/AuthorizationPolicyRegistrationTests.cs` (new)
- Modify: `src/server/Program.cs` (four `AddPolicy` sites at ~239–262)
- Modify: all endpoint files matched by `grep -rl 'Policies("' src/server --include='*.cs'` (73 sites)

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: `static class AuthorizationPolicies` in namespace `Framlux.FleetManagement.Server.Auth` with `public const string Admin = "Admin"; TenantAdmin = "TenantAdmin"; MachineAdmin = "MachineAdmin"; ViewOnly = "ViewOnly";`. Task 4's endpoint edits may touch files that this task also edits — run Task 3 before Task 4 and rebase Task 4 on its result.

- [ ] **Step 1: Write the failing registration-parity test**

Create `test/functional/web/Auth/AuthorizationPolicyRegistrationTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.FunctionalTest.Auth;

/// <summary>
/// Pins that every policy name constant is actually registered with the authorization
/// system, so adding a constant without a matching AddPolicy registration fails here
/// instead of surfacing as a runtime 500 on the first request that uses it.
/// </summary>
public sealed class AuthorizationPolicyRegistrationTests
{
    [Test]
    public async Task EveryPolicyConstant_IsRegistered()
    {
        using FunctionalTestFactory factory = new();
        IAuthorizationPolicyProvider provider =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        FieldInfo[] constants = typeof(AuthorizationPolicies)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && (f.FieldType == typeof(string)))
            .ToArray();

        await Assert.That(constants.Length).IsEqualTo(4);

        foreach (FieldInfo constant in constants)
        {
            string policyName = (constant.GetRawConstantValue() as string) ?? string.Empty;
            await Assert.That(policyName).IsNotEmpty();

            AuthorizationPolicy? policy = await provider.GetPolicyAsync(policyName);

            await Assert.That(policy).IsNotNull();
        }
    }
}
```

Note: if TUnit's assertion surface differs (`IsNotEmpty` naming), match the assertion helpers used elsewhere in the functional test project — the intent is: every constant is a non-empty string AND resolves to a registered policy.

- [ ] **Step 2: Verify it fails**

```bash
dotnet build machine-info.slnx --no-incremental
```
Expected: FAILS — `AuthorizationPolicies` does not exist. A compile failure is the RED state here.

- [ ] **Step 3: Create the constants class**

Create `src/server/Auth/AuthorizationPolicies.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Authorization policy names shared between the registrations in Program.cs and every
/// endpoint's Configure() call, so a misspelled policy is a compile error instead of a
/// runtime authorization failure.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>Global administrators (system-wide admin flag).</summary>
    public const string Admin = "Admin";

    /// <summary>Tenant administrators.</summary>
    public const string TenantAdmin = "TenantAdmin";

    /// <summary>Machine administrators (includes tenant administrators).</summary>
    public const string MachineAdmin = "MachineAdmin";

    /// <summary>Any tenant role with read access (viewer and above).</summary>
    public const string ViewOnly = "ViewOnly";
}
```

- [ ] **Step 4: Verify the parity test passes**

```bash
dotnet build machine-info.slnx --no-incremental
test/functional/web/bin/Debug/net10.0/osx-arm64/functional.web --treenode-filter "/*/*/AuthorizationPolicyRegistrationTests/*"
```
Expected: PASS (the four names are already registered by string — the test proves registration, then the swap below makes both sides share the constant).

- [ ] **Step 5: Swap `Program.cs` registrations to the constants**

In the `AddAuthorization` block replace `options.AddPolicy("Admin", ...)` with `options.AddPolicy(Framlux.FleetManagement.Server.Auth.AuthorizationPolicies.Admin, ...)` (or add the `using` and use the short name — match the file's existing using style), and likewise for `TenantAdmin`, `MachineAdmin`, `ViewOnly`. Do not touch the `ApiKeyAuthenticationHandler.SchemeName` policy — it already uses a constant.

- [ ] **Step 6: Swap all 73 endpoint sites**

```bash
grep -rl 'Policies("Admin")' src/server --include='*.cs' | xargs perl -pi -e 's/Policies\("Admin"\)/Policies(AuthorizationPolicies.Admin)/g'
grep -rl 'Policies("TenantAdmin")' src/server --include='*.cs' | xargs perl -pi -e 's/Policies\("TenantAdmin"\)/Policies(AuthorizationPolicies.TenantAdmin)/g'
grep -rl 'Policies("MachineAdmin")' src/server --include='*.cs' | xargs perl -pi -e 's/Policies\("MachineAdmin"\)/Policies(AuthorizationPolicies.MachineAdmin)/g'
grep -rl 'Policies("ViewOnly")' src/server --include='*.cs' | xargs perl -pi -e 's/Policies\("ViewOnly"\)/Policies(AuthorizationPolicies.ViewOnly)/g'
```

Then build; any file missing `using Framlux.FleetManagement.Server.Auth;` fails to compile — add the using in alphabetical order in each such file. Many endpoint files already import it.

- [ ] **Step 7: Verify no string sites remain and everything is green**

```bash
grep -rn 'Policies("' src/server --include='*.cs' | grep -v obj      # expected: no output
grep -rn 'AddPolicy("' src/server --include='*.cs' | grep -v obj     # expected: no output
dotnet build machine-info.slnx --no-incremental                       # 0 errors, 0 warnings
test/functional/web/bin/Debug/net10.0/osx-arm64/functional.web        # 736 (735 + 1 new)
test/unit/server/bin/Debug/net10.0/osx-arm64/unit.server
```

- [ ] **Step 8: Commit**

```bash
git add src/server/Auth/AuthorizationPolicies.cs src/server/Program.cs \
        src/server/Endpoints test/functional/web/Auth/AuthorizationPolicyRegistrationTests.cs
git -c commit.gpgsign=false commit -m "Replace authorization policy name strings with shared constants

Policy names were repeated as raw strings across 73 endpoint registrations
and the Program.cs policy definitions, where a typo silently produces an
unauthorizable endpoint at runtime. Both sides now share the
AuthorizationPolicies constants, and a registration-parity test fails if a
constant is ever added without a matching policy registration."
```

---

### Task 4: Flatten the billing action double-wrap

The four billing action endpoints (cancel, resume, reactivate, downgrade) return `{success, data: {success, message}}` — the inner `BillingActionResponse.Success` is always `true` (every failure path already goes through `SendApiErrorAsync`), so the inner object is pure redundancy. Target wire shape, matching every other envelope: `{"success": true, "data": null, "message": "...", "errors": null}`. **This is an approved breaking wire change**; the SvelteKit client changes in the same task, and nothing else consumes these endpoints.

Run after Task 3 (both touch the endpoint files).

**Files:**
- Modify: `src/server/Endpoints/Web/Billing/CancelSubscriptionEndpoint.cs`
- Modify: `src/server/Endpoints/Web/Billing/ResumeSubscriptionEndpoint.cs`
- Modify: `src/server/Endpoints/Web/Billing/ReactivateSubscriptionEndpoint.cs`
- Modify: `src/server/Endpoints/Web/Billing/DowngradeSubscriptionEndpoint.cs`
- Delete: `src/server/Endpoints/Web/Billing/BillingActionResponse.cs`
- Modify: `test/functional/web/Endpoints/Web/BillingEndpointTests.cs`
- Modify: `test/functional/web/Endpoints/Web/ResumeSubscriptionEndpointTests.cs`
- Modify: `test/functional/web/Endpoints/Web/ReactivateSubscriptionEndpointTests.cs`
- Modify: `test/functional/web/Endpoints/Web/DowngradeSubscriptionEndpointTests.cs`
- Modify: `src/web/src/lib/api/client.ts` (four methods at ~278–297)

**Interfaces:**
- Consumes: Task 3's `AuthorizationPolicies` (already applied in these files).
- Produces: wire shape `{"success":true,"data":null,"message":"<text>","errors":null}` for all four actions. The web client keeps returning `{ success: boolean; message: string }` to its callers, so `+page.server.ts` and the page component need **no** changes.
- Not in scope: the gRPC `BillingGatewayService` uses the *proto* message named `BillingActionResponse` from `Framlux.Vord.BillingGrpc` — same name, different type. Do not touch it.

- [ ] **Step 1: Update the functional tests to the flat shape (RED)**

In all four test files, every success-path assertion block of this form:

```csharp
bool outerSuccess = root.GetProperty("success").GetBoolean();
JsonElement data = root.GetProperty("data");
bool dataSuccess = data.GetProperty("success").GetBoolean();
string message = data.GetProperty("message").GetString()!;
```

becomes:

```csharp
bool outerSuccess = root.GetProperty("success").GetBoolean();
await Assert.That(root.GetProperty("data").ValueKind).IsEqualTo(JsonValueKind.Null);
string message = root.GetProperty("message").GetString()!;
```

Drop the now-orphaned `dataSuccess` assertions (`await Assert.That(dataSuccess).IsTrue();`) and keep every message-content assertion **with its exact expected text unchanged** — the messages themselves must not change in this task. `BillingEndpointTests.cs` has these blocks at ~lines 122–129, 149–156, 211–218, 242–249, 312–315, 460–467; sweep the other three files the same way (`grep -n 'GetProperty("data")'` per file to find them all). Error-path tests (403/404/502) already assert the standard error envelope and stay untouched.

- [ ] **Step 2: Run the four suites' filters to verify they fail**

```bash
dotnet build machine-info.slnx --no-incremental
test/functional/web/bin/Debug/net10.0/osx-arm64/functional.web --treenode-filter "/*/*/BillingEndpointTests/*"
test/functional/web/bin/Debug/net10.0/osx-arm64/functional.web --treenode-filter "/*/*/ResumeSubscriptionEndpointTests/*"
test/functional/web/bin/Debug/net10.0/osx-arm64/functional.web --treenode-filter "/*/*/ReactivateSubscriptionEndpointTests/*"
test/functional/web/bin/Debug/net10.0/osx-arm64/functional.web --treenode-filter "/*/*/DowngradeSubscriptionEndpointTests/*"
```
Expected: the edited success-path tests FAIL (data is still the inner object, message is still null at root).

- [ ] **Step 3: Flatten the four endpoints**

In each endpoint, change the response generic from `ApiResponse<BillingActionResponse>` to `ApiResponse<object>` (e.g. `EndpointWithoutRequest<ApiResponse<object>>`; `DowngradeSubscriptionEndpoint` keeps its request DTO in `Endpoint<TReq, ApiResponse<object>>`). Replace every send of this form:

```csharp
await Send.OkAsync(ApiResponse<BillingActionResponse>.Ok(new BillingActionResponse
{
    Success = true,
    Message = "Subscription will be canceled at the end of the current billing period."
}), cancellation: ct);
```

with:

```csharp
await Send.OkAsync(new ApiResponse<object>
{
    Success = true,
    Message = "Subscription will be canceled at the end of the current billing period."
}, cancellation: ct);
```

carrying each site's exact message text over verbatim. Then delete `src/server/Endpoints/Web/Billing/BillingActionResponse.cs`:

```bash
git rm src/server/Endpoints/Web/Billing/BillingActionResponse.cs
```

- [ ] **Step 4: Verify the four filters pass, then run the full suites**

```bash
dotnet build machine-info.slnx --no-incremental   # 0 errors, 0 warnings
test/functional/web/bin/Debug/net10.0/osx-arm64/functional.web
test/unit/server/bin/Debug/net10.0/osx-arm64/unit.server
```
Expected: functional.web back to its full count (no test count change in this task), unit.server unchanged.

- [ ] **Step 5: Update the web client to read the envelope directly**

In `src/web/src/lib/api/client.ts`, the four action methods currently `unwrap()` the response — with `data: null` that would throw. Replace each with the envelope read (same pattern for `downgradeSubscription`, `resumeSubscription`, `reactivateSubscription`):

```typescript
async cancelSubscription(): Promise<{ success: boolean; message: string }> {
	const resp = await this.post<ApiResponse<null>>('/api/v1/billing/cancel');
	return { success: resp.success, message: resp.message ?? '' };
}
```

The return type is unchanged, so `+page.server.ts`'s `{ success: result.success, message: result.message }` action returns and the page's `form.success`/`form.message` rendering keep working untouched. Confirm `ApiResponse<T>`'s TS type has an optional/nullable `message` field; if it lacks one, add `message?: string;` to the interface in `src/web/src/lib/api/types.ts`.

- [ ] **Step 6: Run the web checks**

```bash
pnpm -C src/web check    # 0 errors, 0 warnings
pnpm -C src/web test     # 424 passing
pnpm -C src/web build    # succeeds
```

- [ ] **Step 7: Commit**

```bash
git add src/server/Endpoints/Web/Billing test/functional/web/Endpoints/Web \
        src/web/src/lib/api/client.ts src/web/src/lib/api/types.ts
git -c commit.gpgsign=false commit -m "Flatten the billing action responses into the standard envelope

Cancel, resume, reactivate, and downgrade returned a nested
{success, data:{success, message}} shape whose inner success was always
true — failures already use the standard error envelope. The actions now
return the plain envelope with the message at the top level and null data,
and the web client reads the envelope directly instead of unwrapping a
redundant payload."
```

---

## Exit Criteria

1. `dotnet build machine-info.slnx` — 0 errors, 0 warnings.
2. All six TUnit suites green; functional.web at 736 (+1 parity test); unit.services.core reduced only by the deleted migration-job and price-ID tests.
3. `pnpm -C src/web check` 0/0, `pnpm -C src/web test` 424 passing, `pnpm -C src/web build` succeeds.
4. Grep gates all empty: `LegacyRedisKeyCleanup|EncryptLegacyTenantOidcSecrets`, `MapPriceIdToTier|Stripe.*PriceId` (in vord src/test), `Policies\("`, `AddPolicy\("`, and REST `BillingActionResponse` (the gRPC proto type of the same name remains, only under `Framlux.Vord.BillingGrpc`).
5. Four commits on `post-remediation-fixes`, none touching `nuget.config`.
6. Projection sharding untouched (owner ruled keep): `git diff` shows no changes under `StreamingShardCalculator.cs`, `MachineStateStreamingService.cs`, or `StreamingOptions.cs`.
