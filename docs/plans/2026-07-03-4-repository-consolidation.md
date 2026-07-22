# Repository Consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collapse the verified near-duplicate repository methods: 8 subscription mutators → 1 parameterized mutator, 2 fleet-query pipelines → 1, 4 machine-name maps → 2, plus six small twin pairs — shrinking `ISubscriptionRepository` from 15 to 7 methods and removing ~500 LOC across interface/impl/caching-decorator/tests.

**Architecture:** No layering changes. The 6 status/tier subscription mutators are all the same `UPDATE TenantSubscriptions SET (subset of Tier, Status, CurrentPeriodEnd) + UpdatedAt WHERE TenantId = @id`; they become one method with explicit parameters, built with LinqToDB's `AsUpdatable()` composition. Intent stays readable at call sites through named arguments. The caching decorator shrinks proportionally, and consolidating `ReactivateFreeSubscriptionAsync` onto a tenant-id key fixes its documented cache-invalidation gap as a side effect.

**Tech Stack:** .NET 10, LinqToDB (check `IUpdatable` API signatures against the installed package version before assuming — per CLAUDE.md), TUnit with in-memory SQLite repo tests.

## Global Constraints

See [README.md](README.md#global-constraints). Run after plan 1 (the 13 dead methods, including `InsertSubscriptionAsync`'s neighbors, are already gone). **Task 6 touches `DatabaseRepository.Tenants.cs`, which has uncommitted changes on this branch — do Task 6 last, and only after those changes are committed.**

---

### Task 1: `UpdateSubscriptionStateAsync` (TDD)

**Files:**
- Modify: `src/database/Repositories/ISubscriptionRepository.cs`
- Modify: `src/database/Repositories/DatabaseRepository.Subscriptions.cs`
- Test: `test/unit/database/Functional/Repositories/SubscriptionRepositoryTests.cs` (extend; find the existing subscription repo test file with `/usr/bin/grep -rln "UpdateSubscriptionOnCheckoutAsync" test/unit/database`)

**Interfaces:**
- Consumes: existing `_db.TenantSubscriptions` LinqToDB table.
- Produces (Tasks 2–3 call exactly this):

```csharp
Task<int> UpdateSubscriptionStateAsync(
    int tenantId,
    SubscriptionTier? tier,
    SubscriptionStatus status,
    bool clearCurrentPeriodEnd = false,
    CancellationToken cancellationToken = default);
```

- [ ] **Step 1: Write failing repo tests pinning each replaced mutator's exact semantics**

One test per old mutator's mapping, against the in-memory SQLite repo (follow the file's existing seeding pattern). These are the regression tests that prove the consolidation preserves behavior — assert **every column**, including that unrelated columns did NOT change:

```csharp
[Test]
public async Task UpdateSubscriptionStateAsync_TierAndActive_MatchesCheckoutSemantics()
{
    // seed: tenant with Free/Active subscription, CurrentPeriodEnd = null
    int updated = await repo.UpdateSubscriptionStateAsync(tenantId, SubscriptionTier.Pro, SubscriptionStatus.Active);

    TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(tenantId);
    await Assert.That(updated).IsEqualTo(1);
    await Assert.That(row?.Tier).IsEqualTo(SubscriptionTier.Pro);
    await Assert.That(row?.Status).IsEqualTo(SubscriptionStatus.Active);
    await Assert.That(row?.UpdatedAt).IsNotNull();
}

[Test]
public async Task UpdateSubscriptionStateAsync_NullTier_LeavesTierUntouched()
{
    // seed: Pro/Active with CurrentPeriodEnd set
    int updated = await repo.UpdateSubscriptionStateAsync(tenantId, null, SubscriptionStatus.PastDue);

    TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(tenantId);
    await Assert.That(row?.Tier).IsEqualTo(SubscriptionTier.Pro);           // unchanged
    await Assert.That(row?.Status).IsEqualTo(SubscriptionStatus.PastDue);
    await Assert.That(row?.CurrentPeriodEnd).IsNotNull();                    // unchanged
}

[Test]
public async Task UpdateSubscriptionStateAsync_ClearPeriodEnd_NullsCurrentPeriodEnd()
{
    // seed: Team/Active with CurrentPeriodEnd set  (RevertToFree / DowngradeToPro semantics)
    int updated = await repo.UpdateSubscriptionStateAsync(tenantId, SubscriptionTier.Free, SubscriptionStatus.Active, clearCurrentPeriodEnd: true);

    TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(tenantId);
    await Assert.That(row?.Tier).IsEqualTo(SubscriptionTier.Free);
    await Assert.That(row?.CurrentPeriodEnd).IsNull();
}

[Test]
public async Task UpdateSubscriptionStateAsync_UnknownTenant_ReturnsZero()
{
    int updated = await repo.UpdateSubscriptionStateAsync(999999, null, SubscriptionStatus.Active);

    await Assert.That(updated).IsEqualTo(0);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/unit/database/unit.database.csproj -- --treenode-filter "*UpdateSubscriptionStateAsync*"`
Expected: compile failure.

- [ ] **Step 3: Implement in `DatabaseRepository.Subscriptions.cs`**

```csharp
/// <inheritdoc/>
public async Task<int> UpdateSubscriptionStateAsync(
    int tenantId,
    SubscriptionTier? tier,
    SubscriptionStatus status,
    bool clearCurrentPeriodEnd = false,
    CancellationToken cancellationToken = default)
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    IUpdatable<TenantSubscription> update = _db.TenantSubscriptions
        .Where(s => s.TenantId == tenantId)
        .AsUpdatable()
        .Set(s => s.Status, status)
        .Set(s => s.UpdatedAt, now);

    if (tier is not null)
    {
        update = update.Set(s => s.Tier, tier.Value);
    }

    if (clearCurrentPeriodEnd)
    {
        update = update.Set(s => s.CurrentPeriodEnd, (DateTimeOffset?)null);
    }

    int updated = await update.UpdateAsync(cancellationToken);

    return updated;
}
```

Interface declaration with full XML docs documenting the parameter semantics (null `tier` = leave unchanged; `clearCurrentPeriodEnd` = set to NULL).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/unit/database/unit.database.csproj -- --treenode-filter "*UpdateSubscriptionStateAsync*"`
Expected: PASS (all cases).

---

### Task 2: Migrate all callers of the 8 old mutators

**Files:**
- Modify: every production caller (discover below — expect them in `src/services.core/Services/Billing/`, `src/services.core/Services/Handlers/BillingWebhookHandler.cs`, `src/server/Endpoints/Web/Billing/`, `src/server/Endpoints/grpc/FleetAdminService.cs`)

**Interfaces:**
- Consumes: `UpdateSubscriptionStateAsync` from Task 1.

Argument mapping (use named arguments at every call site so intent survives):

| Old call | New call |
|---|---|
| `UpdateSubscriptionOnCheckoutAsync(t, tier)` | `UpdateSubscriptionStateAsync(t, tier, SubscriptionStatus.Active)` |
| `RevertSubscriptionToFreeAsync(t)` | `UpdateSubscriptionStateAsync(t, SubscriptionTier.Free, SubscriptionStatus.Active, clearCurrentPeriodEnd: true)` |
| `SetSubscriptionPastDueAsync(t)` | `UpdateSubscriptionStateAsync(t, tier: null, SubscriptionStatus.PastDue)` |
| `SetSubscriptionActiveAsync(t)` | `UpdateSubscriptionStateAsync(t, tier: null, SubscriptionStatus.Active)` |
| `DowngradeSubscriptionToProAsync(t)` | `UpdateSubscriptionStateAsync(t, SubscriptionTier.Pro, SubscriptionStatus.Active, clearCurrentPeriodEnd: true)` |
| `DeactivateSubscriptionAsync(t)` | `UpdateSubscriptionStateAsync(t, tier: null, SubscriptionStatus.Canceled)` |
| `UpdateSubscriptionAdminAsync(t, tier, status)` | `UpdateSubscriptionStateAsync(t, tier, status)` |
| `ReactivateFreeSubscriptionAsync(subscriptionId)` | `UpdateSubscriptionStateAsync(tenantId, tier: null, SubscriptionStatus.Active)` — the caller has the subscription entity; pass its `TenantId` instead of `Id` |

- [ ] **Step 1: Enumerate callers**

```bash
/usr/bin/grep -rn "UpdateSubscriptionOnCheckoutAsync\|RevertSubscriptionToFreeAsync\|SetSubscriptionPastDueAsync\|SetSubscriptionActiveAsync\|DowngradeSubscriptionToProAsync\|DeactivateSubscriptionAsync\|UpdateSubscriptionAdminAsync\|ReactivateFreeSubscriptionAsync" src --include='*.cs' | /usr/bin/grep -v "src/database/"
```

- [ ] **Step 2: Migrate each site per the table.** For the `ReactivateFreeSubscriptionAsync` site, verify the caller has the subscription row in scope (it looks the subscription up before reactivating); use `subscription.TenantId`. This deliberately changes the key from subscription-id to tenant-id — same row, and it repairs the caching decorator's invalidation (see Task 3).
- [ ] **Step 3: Delete the 8 old methods** from `ISubscriptionRepository.cs` and `DatabaseRepository.Subscriptions.cs`. Also delete `InsertSubscriptionAsync` (interface line 68, impl lines 139–147) and migrate its callers to `CreateTenantSubscriptionAsync` — they are duplicate inserts differing only in logging.
- [ ] **Step 4: Update unit tests that mocked the old methods** — mechanical: the mock setup/`Received()` assertions move to `UpdateSubscriptionStateAsync` with the mapped arguments. Do NOT weaken assertions: `Received(1).UpdateSubscriptionStateAsync(t, SubscriptionTier.Free, SubscriptionStatus.Active, true, Arg.Any<CancellationToken>())` must pin the exact arguments.
- [ ] **Step 5: Build + run**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/database/unit.database.csproj
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/functional/web/functional.web.csproj -- --treenode-filter "*Billing*"
dotnet run --project test/functional/hangfire/functional.hangfire.csproj
```

Expected: 0 warnings, all pass.

---

### Task 3: Shrink `CachingSubscriptionRepository`

**Files:**
- Modify: `src/services.core/Services/Billing/CachingSubscriptionRepository.cs`
- Test: its existing test file (find with `/usr/bin/grep -rln "CachingSubscriptionRepository" test/`)

**Interfaces:**
- Consumes: `ISubscriptionRepository` (now 8 methods).

- [ ] **Step 1: Replace the 8 mutator overrides with one** — forward to the inner repository, then invalidate the tenant's cache entry, using the exact invalidation call the current `DeactivateSubscriptionAsync` override uses. Delete the "accepted staleness" comment block at line 162 — `UpdateSubscriptionStateAsync` is tenant-keyed, so reactivation now invalidates correctly.
- [ ] **Step 2: Add/adjust a decorator test:** `UpdateSubscriptionStateAsync` (a) forwards with identical arguments, (b) invalidates the tenant cache key, (c) a subsequent `GetSubscriptionForTenantAsync` misses the cache. Follow the file's existing test patterns.
- [ ] **Step 3: Build + full services.core unit run.** Expected: pass; `CachingSubscriptionRepository.cs` shrinks by ~100 LOC.

---

### Task 4: Merge the two fleet-query pipelines

**Files:**
- Modify: `src/database/Repositories/IMachineStateRepository.cs` (remove `GetFleetMachinePageAsync`)
- Modify: `src/database/Repositories/DatabaseRepository.MachineState.cs:429-484` (delete method)
- Modify: `src/services.core/Services/Machines/MachineStateService.cs` (its `GetFleetOverviewAsync` now builds a `FleetSearchParameters` and calls `SearchFleetMachinesAsync`)
- Test: existing dashboard functional tests + `MachineStateService` unit tests

`SearchFleetMachinesAsync` (`MachineState.cs:487-639`) is a verified strict superset: same `BuildFleetBaseQuery`, same text search, same sort keys plus more. The dashboard's status filter maps onto the search path's `HealthStatusValues`.

- [ ] **Step 1: Run the dashboard functional tests FIRST and record the passing baseline** (`--treenode-filter "*DashboardFleet*"`). These are the acceptance tests for the merge — no assertion changes allowed.
- [ ] **Step 2: Read both repository methods side by side** and write down the exact `FleetSearchParameters` field values that reproduce `GetFleetMachinePageAsync`'s query: text search, status→`HealthStatusValues` mapping, sort key names, page/size. Confirm each sort key the dashboard uses exists in the search method's sort switch (the audit found the search switch is the superset containing `"disk"`, `"updates"`, `"lastseen"`).
- [ ] **Step 3: Rewrite `MachineStateService.GetFleetOverviewAsync`** to build those parameters and call `SearchFleetMachinesAsync`, mapping the result rows to the existing return type. Update its unit tests (mock now expects `SearchFleetMachinesAsync(Arg.Is<FleetSearchParameters>(p => ...))` pinning the mapped values).
- [ ] **Step 4: Delete `GetFleetMachinePageAsync`** (interface + impl + its repo tests, which are superseded by the search method's tests — verify the search method's repo tests cover status filter + each sort key; add any missing case).
- [ ] **Step 5: Build + rerun the Step 1 baseline filters plus `test/unit/database` and `test/unit/services.core`.** Expected: identical results to baseline.

---

### Task 5: Machine-name map consolidation + small twins

**Files:**
- Modify: `src/database/Repositories/IMachineRepository.cs` / `DatabaseRepository.Machines.cs`
- Modify: `src/database/Repositories/IMachineStateRepository.cs` / `DatabaseRepository.MachineState.cs`
- Modify: `IServerConfigurationRepository` partial (`DatabaseRepository.ServerConfiguration.cs`)
- Modify: single callers as discovered

Consolidations (each: migrate the one caller, delete the loser, migrate its tests):

| Keep | Delete | Caller migration |
|---|---|---|
| `GetMachineNamesAsync(machineIds)` (`Machines.cs:490`) | `GetMachineNameMapForTenantAsync(tenantId)` (`Machines.cs:334`) and `IMachineStateRepository.GetNameMapAsync(machineIds)` (`MachineState.cs:98`) | The tenant-scoped caller fetches its machine-id list (it already has one in scope or gets it from the same repository); the `GetNameMapAsync` caller switches to `GetMachineNamesAsync` — same `Dictionary<long,string>` shape, source table moves from Summaries to Machines, which is the system of record (`UpdateSummaryNameAsync` only mirrors it) |
| `GetHostnameMapAsync` (`MachineState.cs:85`) | — (keep; hostnames only exist on summaries) | — |
| `GetActiveMachineByIdAsync` (`Machines.cs:281`) | `GetMachineAsync` (`Machines.cs:201`) | identical predicate; 4 caller files switch call name |
| `MarkKeyDeliveredAsync` (`Machines.cs:33`) | `SetKeyDeliveredAsync` (`Machines.cs:44`) | sole caller `MachineService.cs:187` runs immediately after `ReissueApiKeyAsync` nulls `KeyDeliveredAt`, so the conditional `Mark` variant behaves identically there — switch the call |
| `ListAllSettingsAsync` (ordered, `ServerConfiguration.cs:59`) | `GetAllSettingsAsync` (`ServerConfiguration.cs:16`) | one caller (gRPC admin) switches; ordering is a superset guarantee |
| `UpsertSettingAsync` (`ServerConfiguration.cs:23`) | `UpdateSettingAsync` (`ServerConfiguration.cs:47`) | the update-only caller switches to upsert; verify that caller does not depend on "row must already exist" semantics — if it does (e.g. returns 404 on missing setting), keep both and skip this row |
| direct `DisableAlertRulesForTenantAsync(tenantId, customOnly: true)` | `DisableCustomAlertRulesForTenantAsync` wrapper (`AlertRules.cs:150`) | inline at `DowngradeCleanupService.cs:51`, matching the direct call 17 lines above it |

- [ ] **Step 1: For each row: grep the delete-candidate's callers, apply the migration, delete method + interface entry, move/merge its tests onto the kept method.**

```bash
/usr/bin/grep -rn "<MethodName>" src test --include='*.cs'
```

- [ ] **Step 2: For the name-map row, add one repo test** asserting `GetMachineNamesAsync` returns names for the given ids and omits deleted machines (pin whatever `IsDeleted` filter the kept method has — read it first; if the deleted `GetNameMapAsync` filtered differently, replicate the *caller-visible* behavior and note the difference).
- [ ] **Step 3: Build + run `test/unit/database`, `test/unit/services.core`, functional web + grpc suites.** Expected: all pass.

---

### Task 6: Tenant roles merge — **after branch settles**

**Files:**
- Modify: `src/database/Repositories/ITenantRepository.cs` / `DatabaseRepository.Tenants.cs:408-460`

- [ ] **Step 1: Preconditions:** current working-tree changes to `DatabaseRepository.Tenants.cs`/`ITenantRepository.cs`/`MemberHandler.cs` are committed; rebase this task on that state.
- [ ] **Step 2: Merge `GetActiveRolesForTenantAsync(tenantId)` into `GetActiveRolesForTenantsAsync(List<int>)`** — the singular is the plural with a one-element list. Migrate the singular's callers to pass `new List<int> { tenantId }` (or add a thin `params`-style overload ONLY if more than 3 call sites make the list noise worse than the method — count first). Keep `GetMembersForTenantAsync` if its projection differs from the roles query (read both; the audit flagged them as three shapes of the same join — merge only what is provably identical).
- [ ] **Step 3: Build + run `test/unit/database` and `test/functional/web` member/invitation filters.** Expected: pass.

---

## Exit criteria

1. `ISubscriptionRepository` has exactly these members: `CreateTenantSubscriptionAsync`,
   `UpdateSubscriptionStateAsync`, `UpdateSubscriptionPeriodEndAsync`,
   `SetCancelAtPeriodEndAsync`, `GetSubscriptionForTenantAsync`, `GetPaidSubscriptionsAsync`,
   `GetSubscriptionsForTenantsAsync` (7; 8 with a justified survivor documented in the summary).
2. New repo tests pin every argument-mapping semantic (tier-null untouched, clear-period-end,
   zero-rows-unknown-tenant); decorator test proves forward + invalidate.
3. `GetFleetMachinePageAsync`, the three deleted name-map/twin methods, and the alert-rule
   wrapper no longer exist; dashboard functional baseline unchanged.
4. `dotnet build machine-info.slnx` — 0 errors, 0 warnings; all six TUnit projects pass.
5. ~500 LOC removed across `src/database`, `src/services.core`, and tests.
