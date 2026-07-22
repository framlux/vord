# Interface Removal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete the 16 single-implementation interfaces in `services.core` (plus `ITenantOidcHandler` in the server) that the audit verified are never mocked, faked, or referenced by any test — they exist solely as a DI registration line — and inject the concrete classes instead.

**Architecture:** No design change. CLAUDE.md's interface mandate covers *repository* interfaces only; these are service/handler interfaces whose unit tests already construct the concrete classes directly. DI keeps working: `AddScoped<IFoo, Foo>()` becomes `AddScoped<Foo>()`, and consumers inject `Foo`. If a future test genuinely needs to stub one of these, the interface can be reintroduced for that one seam — this plan removes only ceremony that nothing consumes.

**Tech Stack:** .NET 10, Microsoft.Extensions.DependencyInjection.

## Global Constraints

See [README.md](README.md#global-constraints). Run **after** plans 2–3 (they edit the same endpoint files that inject these types; doing this last avoids churn). Keep untouched: `IBillingWebhookHandler`, `IDataExportHandler` (mocked by job tests), `ISigningKeyService` (borderline — keeper), and every interface with real mock usage (`ISubscriptionService`, `IRoleCacheInvalidator`, `IBillingApiClient`, all `I*Repository`, etc.).

---

### Task 1: Re-verify zero test usage per interface

**The deletion list (17):**

| Interface | Location |
|---|---|
| `IAdminHandler`, `IAuthMeHandler`, `IDashboardHandler`, `IInvitationHandler`, `IMachineDetailHandler`, `IMachineHandler`, `IMemberHandler`, `IOnboardingHandler`, `IRegistrationTokenHandler`, `ITenantHandler`, `IUserHandler` | `src/services.core/Services/Handlers/` |
| `IBillingStatus`, `IDowngradeGuardService` | `src/services.core/Services/Billing/` |
| `IMachineAuthorizedKeyService`, `IMachineSearchService` | `src/services.core/Services/Machines/` |
| `IRemoteCommandService` | `src/services.core/Services/Commands/` |
| `ITenantOidcHandler` | `src/server/Services/Handlers/` |

- [ ] **Step 1: For each interface, confirm no test references it and count production consumers**

```bash
G=/usr/bin/grep
for i in IAdminHandler IAuthMeHandler IDashboardHandler IInvitationHandler IMachineDetailHandler IMachineHandler IMemberHandler IOnboardingHandler IRegistrationTokenHandler ITenantHandler IUserHandler IBillingStatus IDowngradeGuardService IMachineAuthorizedKeyService IMachineSearchService IRemoteCommandService ITenantOidcHandler; do
  t=$($G -rln "\b$i\b" test --include='*.cs' | wc -l)
  echo "$i: test-files=$t"
done
```

Expected: `test-files=0` for every row. **Any nonzero → drop that interface from this plan and note it** (a test started using it since the audit).

---

### Task 2: Remove, one interface at a time

**Files (per interface):**
- Delete: the `I*.cs` file
- Modify: the concrete class (drop `: IFoo` from its declaration; keep `sealed`; move any constants or XML `<see cref>`s that lived on the interface onto the class)
- Modify: `src/services.core/Extensions/ServiceCollectionExtensions.cs` — `AddScoped<IFoo, Foo>()` → `AddScoped<Foo>()` (same lifetime; some registrations are in the handler/billing registration methods around lines 263–275)
- Modify: every production consumer — constructor parameter and field types change `IFoo` → `Foo` (consumers are endpoints in `src/server/Endpoints/` and a few services; find with the grep below)

**Interfaces:**
- Consumes: nothing.
- Produces: concrete-typed constructors — plan 5's builders (if not yet done) use the concrete types.

Per-interface procedure (repeat for all 17):

- [ ] **Step 1: Enumerate consumers**

```bash
/usr/bin/grep -rln "\bIMemberHandler\b" src --include='*.cs'
```

- [ ] **Step 2: Apply the three edits** (delete file; un-implement; retarget DI registration) and update each consumer:

```csharp
// BEFORE
private readonly IMemberHandler _memberHandler;
public MemberRoleChangeEndpoint(IMemberHandler memberHandler, ...)

// AFTER
private readonly MemberHandler _memberHandler;
public MemberRoleChangeEndpoint(MemberHandler memberHandler, ...)
```

- [ ] **Step 3: Build after each interface** — `dotnet build machine-info.slnx` (0 warnings). A compile error here means a consumer was missed; fix before the next interface.

Order tip: do the 11 handler interfaces first (uniform shape), then the 5 service interfaces, then `ITenantOidcHandler`.

---

### Task 3: Full verification

- [ ] **Step 1: Confirm none of the 17 names appear anywhere**

```bash
/usr/bin/grep -rn "IAdminHandler\|IAuthMeHandler\|IDashboardHandler\|IInvitationHandler\|IMachineDetailHandler\|IMachineHandler\|IMemberHandler\|IOnboardingHandler\|IRegistrationTokenHandler\|ITenantHandler\|IUserHandler\|IBillingStatus\|IDowngradeGuardService\|IMachineAuthorizedKeyService\|IMachineSearchService\|IRemoteCommandService\|ITenantOidcHandler" src test --include='*.cs'
```

Expected: 0 hits.

- [ ] **Step 2: Run everything** — all six TUnit projects (functional tests exercise the real DI container, so they are the proof the registrations still resolve):

```bash
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/database/unit.database.csproj
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
dotnet run --project test/functional/hangfire/functional.hangfire.csproj
```

Expected: all pass. Pay attention to the functional suites — a missed DI retarget fails at host startup, which every functional test exercises.

---

## Exit criteria

1. All 17 interface files deleted (minus any excluded in Task 1, enumerated in the summary);
   grep from Task 3 returns 0.
2. `ServiceCollectionExtensions.cs` has no two-type registration for any deleted interface.
3. All six TUnit projects pass; functional suites prove DI resolution.
4. `dotnet build machine-info.slnx` — 0 errors, 0 warnings.
5. ~600 LOC removed; no test assertions changed anywhere (only type names in plan-5 builders
   if that plan ran first).
