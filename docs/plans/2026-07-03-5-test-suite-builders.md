# Test Suite Builders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collapse the hand-rolled mock-graph duplication in the ~12 worst unit-test files with per-file builder methods (the pattern `SubscriptionServiceTests.BuildService` already uses), remove the functional tests that re-prove unit-tested validator boundaries, and merge the duplicated repository-test files.

**Architecture:** No shared test framework is introduced (CLAUDE.md: simplest approach first). Each offender file gets one `private static` builder with defaulted nullable parameters — a test passes only the mocks it configures, everything else defaults to a fresh substitute. Test *behavior* must not change: same test count (except deliberate deletions in Task 3), same assertions, same mocks-by-reference where a test asserts `Received()` on a mock.

**Tech Stack:** TUnit, NSubstitute.

## Global Constraints

See [README.md](README.md#global-constraints). Two sequencing rules: `MemberHandlerTests.cs` is modified on the current branch — do its refactor only after that work is committed. If plan 6 (interface removal) runs first, builders reference concrete classes instead of interfaces for the removed ones; the builder pattern is unaffected either way.

**Non-negotiable invariant for every refactored file:** the test count reported by the TUnit run must be identical before and after, and no assertion may be weakened. Capture the "before" count per file with:
`dotnet run --project <proj> -- --treenode-filter "*<FileClassName>*"` and compare after.

---

### Task 1: Builder for `MemberHandlerTests` (worked example — repeat shape for all files)

**Files:**
- Modify: `test/unit/services.core/Services/Handlers/MemberHandlerTests.cs` (428 LOC, 18 tests, 107 substitutes)

**Interfaces:**
- Consumes: `MemberHandler(IDatabaseTransactionProvider, IAuditLogRepository, ITenantRepository, ISubscriptionService, IRoleCacheInvalidator, IUserSecurityStampService)` — verify the current constructor first; it changed on this branch.
- Produces: the builder shape below, which Tasks 2+ replicate per file.

- [ ] **Step 1: Add the builder at the top of the class**

```csharp
private static MemberHandler BuildHandler(
    IDatabaseTransactionProvider? transactionProvider = null,
    IAuditLogRepository? auditLog = null,
    ITenantRepository? tenantRepository = null,
    ISubscriptionService? subscriptionService = null,
    IRoleCacheInvalidator? roleCacheInvalidator = null,
    IUserSecurityStampService? securityStampService = null)
{
    return new MemberHandler(
        transactionProvider ?? Substitute.For<IDatabaseTransactionProvider>(),
        auditLog ?? Substitute.For<IAuditLogRepository>(),
        tenantRepository ?? Substitute.For<ITenantRepository>(),
        subscriptionService ?? Substitute.For<ISubscriptionService>(),
        roleCacheInvalidator ?? Substitute.For<IRoleCacheInvalidator>(),
        securityStampService ?? Substitute.For<IUserSecurityStampService>());
}
```

- [ ] **Step 2: Rewrite each test to pass only the mocks it configures or asserts on**

```csharp
// BEFORE (current shape, repeated 18×):
IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
ISubscriptionService subService = Substitute.For<ISubscriptionService>();
MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>(), Substitute.For<IUserSecurityStampService>());

// AFTER — a test that only cares about the outcome:
MemberHandler handler = BuildHandler();

// AFTER — a test that stubs and verifies specific mocks keeps them as locals:
ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
tenantRepository.GetMembersForTenantAsync(2, Arg.Any<CancellationToken>()).Returns(members);
MemberHandler handler = BuildHandler(tenantRepository: tenantRepository);
```

Rule: if a test calls `.Returns(...)` or asserts `Received()`/`DidNotReceive()` on a mock, that mock stays a named local passed by parameter. Everything else drops.

- [ ] **Step 3: Verify — identical test count, all pass**

Run: `dotnet run --project test/unit/services.core/unit.services.core.csproj -- --treenode-filter "*MemberHandlerTests*"`
Expected: 18 passed (same as the before-count), 0 failed.

---

### Task 2: Builders for the remaining offender files

**Files (verified counts from the audit; same procedure as Task 1 for each):**

| File | Tests | Builder target |
|---|---|---|
| `test/unit/services.core/Services/Machines/MachineServiceTests.cs` (1,813 LOC, 188 substitutes; the 6-line redis+billing block repeats 35×) | 44 | `BuildService(...)` — note the redis mock needs its `GetDatabase(...)` stub inside the builder default: `IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>(); redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());` |
| `test/unit/services.core/Services/Billing/UsageHeartbeatJobTests.cs` (590 LOC) | 19 | `BuildJob(...)` |
| `test/unit/services.core/Services/Handlers/InvitationHandlerTests.cs` (797 LOC, 8-arg ctor repeated 26×) | 36 | `BuildHandler(...)` |
| `test/unit/services.core/Services/Handlers/OnboardingHandlerTests.cs` | 9 | `BuildHandler(...)` |
| `test/unit/services.core/Services/Handlers/TenantHandlerTests.cs` | — | `BuildHandler(...)` |
| `test/unit/services.core/Services/Alerts/AlertDeliveryServiceTests.cs` | — | `BuildService(...)` |
| `test/unit/services.core/.../DataExportCleanupJobTests.cs`, `HealthSweepTenantJobTests.cs`, `IntegrationDeliveryJobTests.cs`, `HealthSweepCoordinatorJobTests.cs` | — | `BuildJob(...)` |

- [ ] **Step 1: For each file: capture before-count → add builder → rewrite arrangements → run filter → identical count, all pass.** One file at a time; run its filter before moving on.
- [ ] **Step 2: After the last file, run the whole project**

Run: `dotnet run --project test/unit/services.core/unit.services.core.csproj`
Expected: same total test count as the pre-task baseline, all pass.

---

### Task 3: Trim functional validator-boundary duplicates (alert rules only)

**Files:**
- Modify: `test/functional/web/Endpoints/Web/AlertRuleEndpointTests.cs` (2,206 LOC, 59 tests)
- Reference (do not modify): `test/unit/server/Endpoints/Web/Alerts/CreateAlertRuleValidatorTests.cs` (34 tests), `UpdateAlertRuleValidatorTests.cs` (27 tests)

The audit verified ~22–24 functional tests re-prove per-field boundaries (threshold >100, negative duration, name length, invalid enum values, per-metric duration minimums) already exhaustively covered by the two validator unit-test files. FastEndpoints auto-registers validators, so wiring cannot silently diverge per-rule — one wiring proof per endpoint suffices.

- [ ] **Step 1: Build the deletion list.** For each functional test in the file whose name encodes a field boundary (`*ThresholdOver100*`, `*NegativeDuration*`, `*NameTooLong*`, `*DurationBelowMinimum*`, `*InvalidMetric*`, `*InvalidOperator*`, `*InvalidSeverity*`, and similar), confirm the same boundary is asserted in one of the two validator unit-test files (match on the rule, not the name). Anything without a unit-level twin **stays**.
- [ ] **Step 2: Keep the wiring proofs.** Ensure that after deletion, the file retains for EACH endpoint (create + update): at least one 400-on-invalid-request test and one 2xx happy-path test, both asserting status code AND payload. If deletion would leave an endpoint without a 400 test, keep its simplest boundary test as the wiring proof.
- [ ] **Step 3: Delete the confirmed duplicates and run**

Run: `dotnet run --project test/functional/web/functional.web.csproj -- --treenode-filter "*AlertRule*"`
Expected: remaining tests all pass; file drops ~450–550 LOC. Record the kept/deleted lists in the completion summary.

---

### Task 4: Merge duplicated repository-test files

**Files:**
- `test/unit/database/Repositories/AlertEventRepositoryTests.cs` + `test/unit/database/Functional/Repositories/AlertEventRepositoryTests.cs`
- `test/unit/database/Repositories/RegistrationTokenRepositoryTests.cs` + `test/unit/database/Functional/Repositories/RegistrationTokenRepositoryTests.cs`

Both directories test the same repositories against the same in-memory SQLite via `TestDatabaseFactory`; `RegistrationTokenRepositoryTests` has 3 verbatim-duplicate tests across the pair.

- [ ] **Step 1: Pick the canonical location** — `test/unit/database/Functional/Repositories/` (where the bulk of the repo tests live; verify with `ls` and put the merged file wherever its siblings are).
- [ ] **Step 2: For each pair:** move unique tests from the non-canonical file into the canonical one; delete exact-duplicate test methods (byte-compare bodies — if bodies differ at all, keep both and rename for the distinct intent); delete the emptied file.
- [ ] **Step 3: Run**

Run: `dotnet run --project test/unit/database/unit.database.csproj`
Expected: total count = before-count minus exactly the number of verbatim duplicates deleted (record the number); all pass.

---

## Exit criteria

1. Each refactored unit-test file: identical test count, all passing, and at most **4**
   `Substitute.For<>` calls per average test (spot-check: `grep -c "Substitute.For" <file>`
   drops by ≥50% in each Task 1–2 file).
2. `AlertRuleEndpointTests.cs`: every deleted test has a documented unit-level twin; every
   endpoint keeps ≥1 400-wiring test + ≥1 happy-path test.
3. No duplicate test-class names remain under `test/unit/database/`
   (`grep -rh "^public class" test/unit/database --include='*Tests.cs' | sort | uniq -d` → empty).
4. Full unit + functional suites pass; total suite LOC reduced by ~2,000–3,000.
5. No production files modified by this plan.
