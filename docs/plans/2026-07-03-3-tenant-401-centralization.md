# Tenant-401 Centralization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Emit the "no tenant in claims → 401" response once, from the existing global pre-processor, instead of in ~65 per-endpoint copies.

**Architecture:** Opt-in endpoint tag (`EndpointTags.RequiresTenant`) — the same declarative mechanism already used by `RequiresProSubscription` — checked inside `TenantContextPreProcessor`, which today resolves the tenant but deliberately never writes a response. Tagged endpoints then read the tenant via a new non-nullable `ITenantContext.RequireTenantId()` accessor (throws `InvalidOperationException` if invoked without the tag — a programming error, unreachable in tagged endpoints), which avoids both per-endpoint null checks and the forbidden null-forgiving `!` operator. Opt-in (not opt-out) because many endpoints legitimately have no tenant (admin, agent/API-key, auth bootstrap), and a per-endpoint declared tag matches the project's preference for per-endpoint checks over blanket gating.

**Tech Stack:** .NET 10, FastEndpoints global pre-processors, TUnit.

## Global Constraints

See [README.md](README.md#global-constraints). **Run after plan 2** — the 401 blocks being deleted are one-liners by then, and the pre-processor uses plan 2's `SendApiErrorAsync`.

---

### Task 1: `RequireTenantId()` on the tenant context (TDD)

**Files:**
- Modify: `src/server/Auth/ITenantContext.cs`
- Modify: `src/server/Auth/TenantContext.cs`
- Test: `test/unit/server/Auth/TenantContextTests.cs` (extend or create)

**Interfaces:**
- Consumes: existing `ITenantContext.TenantId` (`int?`) and `TenantContext.Set(int?, int?)`.
- Produces: `int RequireTenantId()` on `ITenantContext` — Task 3's endpoint rewrites call exactly this.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test]
public async Task RequireTenantId_WithTenant_ReturnsValue()
{
    TenantContext context = new();
    context.Set(42, 7);

    await Assert.That(context.RequireTenantId()).IsEqualTo(42);
}

[Test]
public async Task RequireTenantId_WithoutTenant_Throws()
{
    TenantContext context = new();
    context.Set(null, null);

    await Assert.That(() => context.RequireTenantId()).Throws<InvalidOperationException>();
}
```

(Match the existing test file's constructor/`Set` usage — read `TenantContext.cs` first; if `Set` has a different signature, mirror what `TenantContextPreProcessor.cs:27` calls.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/unit/server/unit.server.csproj -- --treenode-filter "*RequireTenantId*"`
Expected: compile failure — method not defined.

- [ ] **Step 3: Implement**

Interface member (in `ITenantContext.cs`):

```csharp
/// <summary>
/// Returns the current tenant id, throwing if the request carries no tenant scope.
/// Only call from endpoints tagged <c>EndpointTags.RequiresTenant</c>, where the
/// pre-processor has already rejected tenant-less requests with a 401.
/// </summary>
/// <returns>The tenant id for the current request.</returns>
/// <exception cref="InvalidOperationException">No tenant is set on this request.</exception>
int RequireTenantId();
```

Implementation (in `TenantContext.cs`):

```csharp
/// <inheritdoc/>
public int RequireTenantId()
{
    if (TenantId is null)
    {
        throw new InvalidOperationException(
            "No tenant scope on this request. Tag the endpoint with EndpointTags.RequiresTenant so the pre-processor rejects tenant-less requests before the handler runs.");
    }

    return TenantId.Value;
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/unit/server/unit.server.csproj -- --treenode-filter "*RequireTenantId*"`
Expected: PASS.

---

### Task 2: Tag constant + pre-processor enforcement (TDD)

**Files:**
- Modify: `src/server/Services/Billing/EndpointTags.cs` (add constant)
- Modify: `src/server/Services/Tenancy/TenantContextPreProcessor.cs` (enforce tag)
- Test: `test/unit/server/Services/Tenancy/TenantContextPreProcessorTests.cs` (extend or create)
- Test: `test/functional/web/Endpoints/Web/RequiresTenantEnforcementTests.cs` (create)

**Interfaces:**
- Consumes: `EndpointDefinition.EndpointTags` metadata (same lookup as `ProSubscriptionPreProcessor.cs:39-44`), `SendApiErrorAsync` from plan 2.
- Produces: `EndpointTags.RequiresTenant` constant; 401-with-`ApiResponse`-envelope behavior for tagged endpoints. Task 3 relies on both.

- [ ] **Step 1: Add the constant**

```csharp
/// <summary>
/// Endpoints with this tag require a tenant scope on the request. The
/// <see cref="Tenancy.TenantContextPreProcessor"/> rejects tenant-less requests with a
/// 401 before the handler runs, so tagged handlers may call
/// <c>ITenantContext.RequireTenantId()</c> without a null check.
/// </summary>
public const string RequiresTenant = "RequiresTenant";
```

- [ ] **Step 2: Write the failing functional test**

Pick one endpoint that will be tagged in Task 3 (use `PUT /v1/api/members/{id}/role`, `MemberRoleChangeEndpoint`). Issue a request authenticated **without a tenant claim** (see how existing tests build tenant-less principals — `MemberEndpointTests.cs` has 401 cases today) and assert 401 + envelope:

```csharp
[Test]
public async Task TaggedEndpoint_WithoutTenantClaim_Returns401Envelope()
{
    using FunctionalTestFactory factory = new();
    HttpClient client = await factory.CreateClientWithoutTenantClaimAsync();   // reuse the existing tenant-less-auth helper from MemberEndpointTests

    HttpResponseMessage response = await client.PutAsJsonAsync("/v1/api/members/1/role", new { Role = "Viewer" });
    string json = await response.Content.ReadAsStringAsync();
    using JsonDocument doc = JsonDocument.Parse(json);

    await Assert.That((int)response.StatusCode).IsEqualTo(401);
    await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();
}
```

This passes TODAY via the endpoint's own block — it is the regression pin that must stay green when the block moves into the pre-processor.

- [ ] **Step 3: Extend the pre-processor**

Replace the body of `TenantContextPreProcessor.PreProcessAsync` (keep the class doc comment but update it — it currently documents the punt):

```csharp
/// <inheritdoc/>
public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
{
    HttpContext httpContext = context.HttpContext;

    TenantContext tenantContext = httpContext.RequestServices.GetRequiredService<TenantContext>();

    int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(httpContext.User, httpContext);
    int? userId = TenantClaimHelper.GetUserIdFromClaims(httpContext.User);
    tenantContext.Set(tenantId, userId);

    if (tenantId is not null)
    {
        return;
    }

    // Endpoints that declare RequiresTenant get their 401 here so handlers can assume
    // a tenant scope. Untagged endpoints keep handling tenant-less requests themselves.
    Endpoint? endpoint = httpContext.GetEndpoint();
    EndpointDefinition? epDef = endpoint?.Metadata?.GetMetadata<EndpointDefinition>();
    if ((epDef is null) || epDef.EndpointTags?.Contains(Billing.EndpointTags.RequiresTenant) != true)
    {
        return;
    }

    if (httpContext.ResponseStarted())
    {
        return;
    }

    await httpContext.SendApiErrorAsync(401, "Unauthorized", ct);
    context.HttpContext.MarkResponseStart();
}
```

Follow `ProSubscriptionPreProcessor.cs` exactly for the `ResponseStarted()` / `MarkResponseStart()` idiom and the metadata lookup; fix namespaces/usings to match (the tag class lives in `Framlux.FleetManagement.Server.Services.Billing`).

- [ ] **Step 4: Unit-test the tag gate**

Add pre-processor unit tests mirroring how `ProSubscriptionPreProcessor` is unit-tested (find its test file under `test/unit/server/` and copy the harness): (a) tagged endpoint + null tenant → 401 written + response marked started; (b) untagged endpoint + null tenant → nothing written; (c) tagged endpoint + tenant present → nothing written.

- [ ] **Step 5: Run**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/server/unit.server.csproj -- --treenode-filter "*TenantContextPreProcessor*"
dotnet run --project test/functional/web/functional.web.csproj -- --treenode-filter "*RequiresTenantEnforcement*"
```

Expected: all pass (the functional test still passes because the endpoint's own block is still in place — belt and suspenders until Task 3 removes it).

---

### Task 3: Migrate endpoints, batch by folder

**Files:**
- Modify: the ~65 files under `src/server/Endpoints/` with the tenant-null block (discover per batch)

**Interfaces:**
- Consumes: `EndpointTags.RequiresTenant`, `ITenantContext.RequireTenantId()`.

Per-endpoint mechanical change:

```csharp
// In Configure() — add the tag:
public override void Configure()
{
    Post("/billing/cancel");
    Policies("TenantAdmin");
    Tags(EndpointTags.RequiresTenant);
    Version(1);
}

// In HandleAsync() — BEFORE (post-plan-2 shape):
int? tenantId = _tenantContext.TenantId;
if (tenantId is null)
{
    await HttpContext.SendApiErrorAsync(401, "Unauthorized", ct);

    return;
}
// ... uses tenantId.Value

// AFTER:
int tenantId = _tenantContext.RequireTenantId();
// ... uses tenantId
```

Rules:
- Only migrate endpoints whose tenant-less behavior is exactly "401 Unauthorized". If an
  endpoint does anything else with a null tenant (different message, fallback behavior),
  leave it untouched and list it in the task summary.
- Replace all subsequent `tenantId.Value` with `tenantId` in the same file.
- Do NOT remove null-tenant checks inside `services.core` handlers (e.g.
  `MemberHandler.RemoveAsync` returning 401 for null tenant). Those are defense in depth
  for a different layer and have their own tests.
- Discovery per batch: `/usr/bin/grep -rln "TenantId" src/server/Endpoints/<folder> --include='*.cs'` then check each file for the null→401 shape.

- [ ] **Step 1: Batch A — `Endpoints/Web/Billing/`**, then `dotnet build machine-info.slnx && dotnet run --project test/functional/web/functional.web.csproj -- --treenode-filter "*Billing*"`
- [ ] **Step 2: Batch B — `Endpoints/Web/Alerts/`, `Endpoints/Web/Integrations/`** (functional filters `"*Alert*"`, `"*Integration*"`)
- [ ] **Step 3: Batch C — `Endpoints/Web/Machines/`** (filter `"*Machine*"`, `"*History*"`, `"*Ssh*"`)
- [ ] **Step 4: Batch D — `Endpoints/Web/Invitations/` and all remaining Web folders** (filter `"*Member*"`, `"*Invitation*"`, then the full suite)
- [ ] **Step 5: Update functional 401 tests only if they asserted a per-endpoint message that differed from `"Unauthorized"`** — the status code and envelope shape must not change. If a test breaks on anything other than message text, that's a real regression: stop and investigate.

---

### Task 4: Final sweep

- [ ] **Step 1: Verify the pattern is gone**

```bash
/usr/bin/grep -rn -A2 "TenantId;" src/server/Endpoints --include='*.cs' | /usr/bin/grep -B1 "is null" | wc -l
```

Expected: 0 (or only the endpoints deliberately excluded in Task 3, each listed in the summary).

- [ ] **Step 2: Full suite**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
```

Expected: 0 warnings, all pass.

---

## Exit criteria

1. Every migrated endpoint carries `Tags(EndpointTags.RequiresTenant)` and contains no
   tenant-null branch; excluded endpoints are enumerated in the completion summary.
2. `RequiresTenantEnforcementTests` passes (401 + envelope via the pre-processor).
3. Pre-processor unit tests cover tagged/untagged × tenant/no-tenant.
4. Full functional suite passes; no 401 behavior changed except message unification to
   `"Unauthorized"` where per-endpoint text differed.
5. `dotnet build machine-info.slnx` — 0 errors, 0 warnings; ~400–500 LOC removed.
