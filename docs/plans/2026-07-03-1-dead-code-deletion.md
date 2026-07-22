# Dead Code Deletion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete every type, method, and config key the 2026-07-03 audit verified as having zero production callers — pure deletion, zero behavior change.

**Architecture:** No design changes. Each task deletes a coherent group (server, services.core, database, test cruft), re-verifies zero references immediately before deleting, and proves the build/tests stay green after.

**Tech Stack:** .NET 10 / C#, TUnit, LinqToDB.

## Global Constraints

See [README.md](README.md#global-constraints). In particular: never `git commit`; zero build warnings; deleted production code takes its orphaned tests with it.

**Verification rule for every deletion:** immediately before deleting symbol `X`, run
`grep -rn "X" src test --include='*.cs'` (use `/usr/bin/grep` if `grep` is shadowed).
The only acceptable hits are the declaration itself, its `<see cref>` XML docs, and tests
that exist solely to exercise it (which get deleted in the same task). Any *other* hit
means the audit finding is stale — **stop and report instead of deleting.**

---

### Task 1: Server dead code

**Files:**
- Delete: `src/server/Endpoints/Web/ApiErrorCodes.cs`
- Delete: `src/server/Auth/AuthorizationPolicies.cs`
- Modify: `src/services.core/Models/ApiResponse.cs` (remove `ErrorCode` property, lines 32–36, and the 3-arg `Error` overload, lines 64–76)
- Modify: `src/server/Endpoints/Web/Machines/History/HistoryTimeRange.cs` (remove `TryParse`, `GetRangeDays`, `IsValid`; keep `TryResolve`)
- Modify: `src/server/Services/Infrastructure/RedisRateLimiterExtensions.cs:72-79,106-112` (remove the no-op "callback" named policy registration and its warning comment; keep `CreateCallbackLimiter` and `CallbackRateLimitMiddleware` untouched)
- Test: `test/unit/server/Endpoints/Web/Machines/History/HistoryTimeRangeTests.cs` (remove tests for the three deleted methods, keep `TryResolve` tests)

**Interfaces:**
- Consumes: nothing.
- Produces: `ApiResponse<T>` now has exactly one `Error(string message, List<string>? errors = null)` factory — later plans (2, 3, 7) rely on this being the only error factory.

- [ ] **Step 1: Verify zero references for each symbol**

```bash
G=/usr/bin/grep
$G -rn "ApiErrorCodes" src test --include='*.cs' | $G -v "ApiErrorCodes.cs"          # expect 0
$G -rn "AuthorizationPolicies" src test --include='*.cs' | $G -v "AuthorizationPolicies.cs"  # expect 0
$G -rn "ErrorCode" src test --include='*.cs' | $G -v "ApiResponse.cs" | $G -v "ApiErrorCodes" # expect 0 assignments/reads
$G -rn "HistoryTimeRange.TryParse\|GetRangeDays\|HistoryTimeRange.IsValid" src --include='*.cs' | $G -v "HistoryTimeRange.cs"  # expect 0
```

Expected: each command returns nothing (or only the test file being trimmed in the same task).

- [ ] **Step 2: Delete the two dead files and the dead members**

In `ApiResponse.cs`, remove the `ErrorCode` property and the second `Error` overload so the class ends with the single two-parameter `Error` factory. In `HistoryTimeRange.cs`, remove the three dead methods and any private helpers only they used. In `RedisRateLimiterExtensions.cs`, remove the `AddPolicy("callback", ...)` registration block and its "does NOT enforce" comment.

- [ ] **Step 3: Trim `HistoryTimeRangeTests.cs`**

Delete every `[Test]` whose subject is `TryParse`, `GetRangeDays`, or `IsValid`. Keep all `TryResolve` tests unchanged.

- [ ] **Step 4: Build and run affected test projects**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/web/functional.web.csproj
```

Expected: 0 warnings, all tests pass.

---

### Task 2: services.core dead types

**Files:**
- Delete: `src/services.core/Services/Machines/JsonbFilterExpressions.cs`
- Delete: `src/services.core/Models/Telemetry/CpuInfoPayload.cs`
- Delete: `src/services.core/Models/Telemetry/MemoryInfoPayload.cs`
- Delete: `src/services.core/Models/Telemetry/DiskInfoPayload.cs`
- Delete: `src/services.core/Models/Telemetry/DiskInfoEntryDto.cs`
- Delete: `src/services.core/Services/Machines/SearchScalarRow.cs`
- Delete: `src/services.core/Services/Machines/StateUpdateMessage.cs`
- Delete: `src/services.core/Models/Dashboard/FleetOverviewDto.cs`
- Delete: `src/services.core/Services/Infrastructure/RetentionDateHelper.cs` (+ its test file under `test/unit/services.core/`)
- Delete: `src/services.core/Logging/SensitiveDestructuringPolicy.cs` and `src/services.core/Logging/SensitiveAttribute.cs`
- Modify: `src/services.core/Services/Machines/StreamingShardCalculator.cs` (remove `OwnsMachine` method, lines 18–29; **keep** `LockNameForShard`)
- Modify: `src/services.core/Extensions/ServiceCollectionExtensions.cs:57` (remove the `SensitiveDestructuringPolicy` Serilog registration line)
- Modify: `src/server/appsettings.json:26-27` (remove the dead `Telemetry:RetentionDays` key)
- Test: delete the test files/tests that exclusively cover the deleted types (locate with the greps below)

**Interfaces:**
- Consumes: nothing.
- Produces: nothing later plans depend on.

- [ ] **Step 1: Verify zero references per type**

```bash
G=/usr/bin/grep
for s in JsonbFilterExpressions CpuInfoPayload MemoryInfoPayload DiskInfoPayload DiskInfoEntryDto SearchScalarRow StateUpdateMessage RetentionDateHelper SensitiveDestructuringPolicy SensitiveAttribute; do
  echo "== $s"; $G -rln "$s" src test --include='*.cs' | $G -v "/$s.cs"
done
echo "== FleetOverviewDto (exclude Paginated)"; $G -rn "FleetOverviewDto" src test --include='*.cs' | $G -v "PaginatedFleetOverviewDto" | $G -v "/FleetOverviewDto.cs"
echo "== OwnsMachine"; $G -rn "OwnsMachine" src --include='*.cs' | $G -v "StreamingShardCalculator.cs"
```

Expected: for each symbol, hits only in its own file, its own test file, or `ServiceCollectionExtensions.cs:57` (for the destructuring policy). `FleetOverviewDto` and `OwnsMachine` checks return nothing.

- [ ] **Step 2: Delete the files, the `OwnsMachine` method, the DI registration line, and the appsettings key**

Also delete the test files found in Step 1 (e.g. the `SensitiveDestructuringPolicy` test fixture and `RetentionDateHelperTests`), and remove `OwnsMachine` tests from the shard-calculator test file while keeping `LockNameForShard` tests.

- [ ] **Step 3: Build and run affected tests**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/functional/web/functional.web.csproj
```

Expected: 0 warnings, all tests pass.

---

### Task 3: Dead repository methods (13)

**Files:**
- Modify: `src/database/Repositories/ITenantRepository.cs:151` + `src/database/Repositories/DatabaseRepository.Tenants.cs:386` — remove `QueryTenantsAsync`
- Modify: `src/database/Repositories/IUserRepository.cs:18,45,50,97` + `src/database/Repositories/DatabaseRepository.Users.cs:200` — remove `GetUserByExternalIdAsync`, `DoAnyUsersExistAsync`, `GetUserByEmailAsync`, `QueryUsersAsync`
- Modify: `src/database/Repositories/IMachineRepository.cs:114` + `src/database/Repositories/DatabaseRepository.Machines.cs:312` — remove `GetTenantForMachineAsync`
- Modify: `src/database/Repositories/IAlertRuleRepository.cs` + `src/database/Repositories/DatabaseRepository.AlertRules.cs:200` — remove `GetMachineIdsForRuleAsync` (keep the plural `GetMachineIdsForRulesAsync`)
- Modify: `src/database/Repositories/IAlertEventRepository.cs` + its impl partial — remove `HasActiveEventForRuleMachineAsync`
- Modify: `src/database/Repositories/IIntegrationRepository.cs` + its impl partial — remove `UpdateIntegrationEnabledAsync`, `UpdateIntegrationNameAsync` (superseded by `UpdateIntegrationAsync`)
- Modify: `src/database/Repositories/IMachineStateRepository.cs:83,129` + `src/database/Repositories/DatabaseRepository.MachineState.cs:111,311` — remove `GetSummariesByMachineIdsAsync` (dictionary variant; keep `GetSummaryListByMachineIdsAsync`) and `GetTelemetryByMachineIdsAndTypeAsync` (keep the paged variant)
- Modify: `IServerSettingsCache` + `src/services.core/.../ServerSettingsCache.cs:82` — remove `SetSettingAsync` (keep `UpsertSettingAsync`)
- Modify: `src/database/Repositories/ITierFeatureLimitRepository.cs` + `src/database/Repositories/DatabaseRepository.TierFeatureLimits.cs:36` — remove `UpdateLimitsForTierAsync`
- Test: delete/trim the unit tests that exclusively cover these 13 methods (in `test/unit/database/` and `test/unit/services.core/`)
- Test: `test/unit/server/.../SocialAuthEventsTests.cs:478` — remove the `DidNotReceive()` assertion on `GetUserByExternalIdAsync` (the compile reference disappears with the method)

**Interfaces:**
- Consumes: nothing.
- Produces: slimmer repository interfaces; plan 4 assumes these members are already gone.

**Caution:** `DatabaseRepository.Tenants.cs` and `ITenantRepository.cs` have uncommitted changes on this branch — rebase this task on the committed state of those files.

- [ ] **Step 1: Verify each method has no production caller**

```bash
G=/usr/bin/grep
for m in QueryTenantsAsync QueryUsersAsync GetTenantForMachineAsync GetMachineIdsForRuleAsync HasActiveEventForRuleMachineAsync UpdateIntegrationEnabledAsync UpdateIntegrationNameAsync GetSummariesByMachineIdsAsync GetTelemetryByMachineIdsAndTypeAsync SetSettingAsync UpdateLimitsForTierAsync DoAnyUsersExistAsync GetUserByExternalIdAsync; do
  echo "== $m"; $G -rn "$m" src --include='*.cs' | $G -v "src/database/" | $G -v "ServerSettingsCache"
done
```

Expected: zero production hits for every method. Watch two footguns: `GetMachineIdsForRuleAsync` must not match the plural `GetMachineIdsForRulesAsync` (check hits by eye); `GetUserByExternalIdAsync` must not match `GetUserByExternalIdForProviderAsync`.

- [ ] **Step 2: Remove interface declarations and implementations**

Delete each method from its interface and from the `DatabaseRepository` partial (or `ServerSettingsCache`). Remove now-unused `using`s.

- [ ] **Step 3: Fix the tests that referenced the deleted methods**

Delete tests whose subject *is* a deleted method. In `SocialAuthEventsTests.cs`, remove only the `DidNotReceive()` line for `GetUserByExternalIdAsync` — the test's remaining assertions (that the provider-scoped lookup was used) stay and still guard the account-takeover regression.

- [ ] **Step 4: Build and run all test projects**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/database/unit.database.csproj
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
```

Expected: 0 warnings, all pass.

---

### Task 4: Test-tree cruft

**Files:**
- Delete: `test/unit/orleans/` (entire directory — contains only stale `bin/`/`obj/` output, no sources, not in the solution)

- [ ] **Step 1: Confirm the directory has no sources and is not referenced**

```bash
/usr/bin/find test/unit/orleans -name '*.cs' -o -name '*.csproj' | wc -l   # expect 0
/usr/bin/grep -rn "orleans" machine-info.slnx                              # expect 0
```

- [ ] **Step 2: Delete the directory**

```bash
rm -rf test/unit/orleans
```

- [ ] **Step 3: Build the solution**

```bash
dotnet build machine-info.slnx
```

Expected: 0 errors, 0 warnings.

---

## Exit criteria

1. Every symbol in this plan's inventory returns zero grep hits in `src/` and `test/`.
2. `dotnet build machine-info.slnx` — 0 errors, 0 warnings.
3. All six TUnit projects pass.
4. `src/` production LOC reduced by ≥1,200 versus baseline; no behavior change (no
   functional test needed modification other than deletions and the one
   `SocialAuthEventsTests` assertion removal).
