# Self-Hosted Mode Remediation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Close four defects, then consolidate entitlement enforcement into one vocabulary and remove the last duplicated tier rules found by adversarial verification of the self-hosted mode work (commits `53dd41f`..`a8d798d`).

**Context:** The implemented work is green — 4575 tests, 0 failures — and no SaaS behaviour regressed. These are gaps between the spec and what shipped, not breakage.

**Prerequisite reading:** `docs/specs/2026-08-23-self-hosted-mode-design.md` and `docs/plans/2026-08-23-self-hosted-mode.md`.

## Global Constraints

Identical to the parent plan. Restated because they are load-bearing:

- No AI attribution in commit messages — no `Co-Authored-By`, no "Generated with", no AI mention.
- Never push. Local commits only.
- No task/plan/step/phase numbers in source or comments. Comments describe intent only.
- No `var`; file-scoped namespaces; Allman braces; `_camelCase` fields; no `this.`; XML docs on public members (CS1591 is an error); never `!bool`; no Yoda conditions; blank line before `return`; blank line at EOF; one type per file; alphabetical usings; licence header.
- `TreatWarningsAsErrors` is on.
- TUnit via `dotnet run`, never `dotnet test`. **The `--treenode-filter "*Name*"` form matches zero tests in this repo's TUnit version and reports "Zero tests ran" rather than failing** — use `--treenode-filter "/*/*/Name/*"`.
- Functional test hosts select mode via the `StartupEnvironment` property under `HostCreationLock`, **not** `ConfigureAppConfiguration` — the composition root reads configuration eagerly in `Program.cs` before a test factory's added sources are visible.
- Leave `.beads/interactions.jsonl` and `src/agent/internal/registration/registration_test.go` uncommitted; they were already modified before this work. Stage explicit file lists, never `git add -A`.

---

### Task 1: 404 the four unguarded billing endpoints in self-hosted

**Problem:** `CatalogEndpoint`, `InvoicesEndpoint`, `UpcomingInvoiceEndpoint` and `UsageHistoryEndpoint` carry no `DeploymentMode` guard. Only the four mutating endpoints go through `BillingEndpointGuards.LoadGatedSubscriptionAsync`. In self-hosted all four currently return **200 with empty or zeroed payloads**, because `NoOpBillingApiClient` returns `[]`, `[]`, `null` and `[]`. The spec says they must 404.

**Why 404 is right:** each endpoint's payload is Stripe data — a price catalog, issued invoices, a forthcoming invoice, a per-month invoice-amount series. A self-hosted deployment has no prices and issues no invoices. An empty 200 misrepresents "this product has no billing" as "your account has no invoices". `SubscriptionEndpoint` is deliberately **not** in this set: it returns tier and limits that `+layout.server.ts:20` fetches on every SSR load, and must keep returning 200.

**`UsageHistoryEndpoint` was missed by the spec** and is included here by decision. Its `MachineCount` series is genuine data, so this does cost self-hosters a view; the reason it still 404s is that the same response asserts `invoiceAmountCents: 0` for every month, which is a fabricated billing claim rather than an absent one. Re-exposing the machine-count history as a usage endpoint that does not present itself as billing history is separate, later work.

**Files:**
- Modify: `src/server/Endpoints/Web/Billing/CatalogEndpoint.cs`
- Modify: `src/server/Endpoints/Web/Billing/InvoicesEndpoint.cs`
- Modify: `src/server/Endpoints/Web/Billing/UpcomingInvoiceEndpoint.cs`
- Modify: `src/server/Endpoints/Web/Billing/UsageHistoryEndpoint.cs`
- Modify: `test/functional/web/Endpoints/Web/SelfHostedEndpointTests.cs`
- Modify: `test/functional/web/Endpoints/Web/BillingCatalogEndpointTests.cs`

- [ ] **Step 1: Write the failing tests**

In `SelfHostedEndpointTests.cs`, add four tests asserting `GET /api/v1/billing/catalog`, `/billing/invoices`, `/billing/upcoming-invoice` and `/billing/usage-history` each return 404 under `SelfHostedTestFactory`. Follow the file's existing conventions for host construction and auth.

A test asserting `GET /api/v1/billing/subscription` still returns **200** in self-hosted already exists — `GetBillingSubscription_InSelfHosted_StillReturns200`. Do not add a second one; move or keep it adjacent to the new 404 tests and make sure its comment says the endpoint is deliberately excluded because the application shell fetches it on every page load. Without that test sitting next to the others, a later reader will "fix the inconsistency" and break the shell.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet run --project test/functional/web/functional.web.csproj --treenode-filter "/*/*/SelfHostedEndpointTests/*"
```

Expected: the four new 404 tests FAIL with 200; the subscription test passes.

- [ ] **Step 3: Add the guard to each of the four endpoints**

Inject `DeploymentMode` and return 404 as the first action in `HandleAsync`, before `RequireTenantId()`. Match the wording `BillingEndpointGuards` already uses so the three read consistently with the four gated endpoints.

```csharp
        if (_deploymentMode.IsSelfHosted)
        {
            await HttpContext.SendApiErrorAsync(404, "Billing is not enabled", ct);

            return;
        }
```

Add `using Framlux.FleetManagement.Services.Core.Deployment;` in alphabetical position to each file. Guard the new constructor parameter with `ArgumentNullException.ThrowIfNull`.

The guard reads consistently across all four, so the three-versus-four distinction disappears from the code; only this plan records that `UsageHistoryEndpoint` was a late addition.

**`CatalogEndpoint`'s class summary (`:29-33`) currently documents the opposite decision** — "deliberately not gated on the billing-enabled flag: disabled installs simply receive an empty catalog." That was a considered choice under the old `Billing:Enabled` switch, and this task reverses it. Rewrite the summary to say the catalog is available to Free-tier tenants (it powers the upgrade pricing cards) but is absent in a self-hosted deployment, which has no prices to list. Shipping the guard under the old comment would leave the next reader with two contradicting statements of intent.

- [ ] **Step 4: Reconcile the pre-existing catalog test**

`BillingCatalogEndpointTests.cs:152` (`Catalog_BillingDisabled_Returns...`) currently asserts the empty-200 behaviour under the self-hosted factory. Update its assertion to 404 and rename the method away from the retired `BillingDisabled` vocabulary — `Catalog_SelfHosted_Returns404`.

- [ ] **Step 5: Run the functional suites**

```bash
dotnet run --project test/functional/web/functional.web.csproj
```

Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git commit -m "fix: return 404 from catalog and invoice endpoints in self-hosted mode"
```

---

### Task 2: Ingest eligibility must not depend on subscription status in self-hosted

**Problem:** `SelfHostedSubscriptionService.IsIngestEligibleAsync` delegates to the real service, which requires the row to be `Active` or `PastDue`. A self-hosted tenant whose row is `Canceled` or `None` (the enum is `None/Active/PastDue/Canceled` — there is no `Inactive`) is therefore **permanently and silently blocked from telemetry ingest**, with no remediation path — every billing endpoint 404s, and `EnsureSubscriptionExistsAsync` (the self-heal that would fix it) has **zero callers anywhere in the repository**.

**Reachability:** nothing *inside* a self-hosted install writes a non-Active row — `OnboardingHandler` writes `Free`/`Active`, the billing endpoints 404, and webhooks are unmapped. It is reached by importing a database from the hosted product, or by flipping `Deployment:SelfHosted` to true on an existing install. Both are exactly the migration stories self-hosting invites.

**The fix — and why not the obvious one.** The tempting fix is to call `EnsureSubscriptionExistsAsync` somewhere to heal the row. Reject that, for a worse reason than "it is a write on the ingest hot path": on a `Canceled` **paid** row that method rewrites the row to `Free`/`Active` with `clearCurrentPeriodEnd: true` (`SubscriptionService.cs:246`). Called from ingest, it would **irreversibly destroy imported subscription history on the first telemetry packet** — precisely for the migration scenarios this fix exists to serve, including migrating back to the hosted product. It would also race across replicas, every ingest path issuing the same UPDATE.

The real problem is that **subscription status is a SaaS concept with no meaning in a self-hosted deployment**, so gating ingest on it is incoherent there. A read-only eligibility check leaves the imported row as truth.

So the decorator should stop delegating and instead check only what actually matters in self-hosted: is the tenant active? That preserves the tenant-deactivation and pending-deletion enforcement — the entire reason the earlier review said this member must not answer a blanket `true` — while dropping the subscription-status dependency that self-hosted cannot satisfy or repair.

This supersedes the "delegate" disposition in the spec's §4 member table. The reasoning that produced "delegate" was correct about *why* (deactivation must still block) but wrong about *how*.

**Files:**
- Modify: `src/services.core/Services/Billing/SelfHostedSubscriptionService.cs`
- Modify: `src/services.core/Extensions/ServiceCollectionExtensions.cs` (decorator registration)
- Modify: `test/unit/services.core/Services/Billing/SelfHostedSubscriptionServiceTests.cs`
- Modify: `test/functional/grpc/Endpoints/Grpc/SelfHostedTelemetryTests.cs`
- Modify: `docs/specs/2026-08-23-self-hosted-mode-design.md`

- [ ] **Step 1: Write the failing tests**

In `SelfHostedSubscriptionServiceTests.cs`, replace `IsIngestEligibleAsync_DelegatesSoTenantDeactivationStillBlocks` with three tests. They need an `ITenantRepository` substitute added to the `CreateService` helper.

**Two mechanical facts, both verified — get these right up front:**

- `Tenant` has **six** required members: `ExternalId`, `Name`, `CreatedAt`, `CreatedByUserId`, `IsActive`, `LogoUrl` (`src/database/Models/Tenant.cs:26,32,38,44,56,80`). The fixtures below set all six. Omitting any is CS9035 — the same defect class that cost a round-trip on `TenantSubscription` last time.
- Changing `CreateService` to two `out` parameters breaks the other call sites in this file. `grep -c "CreateService(out"` reports thirteen, but one of those is the helper's own declaration — there are **twelve** calls, and one of them is the test being replaced, so **eleven** others need updating. Either add a one-`out` overload that discards the repository, or update them all. Do this before running anything, or the file will not compile and the "failing test" step will report a compile error rather than a red test.

Both the decorator and this test file need `using Framlux.FleetManagement.Database.Repositories;` for `ITenantRepository` — neither imports it today.

```csharp
    /// <summary>
    /// Subscription status is a hosted-product concept. A self-hosted deployment cannot change a
    /// subscription — every billing endpoint is absent — so gating ingest on a stale or imported
    /// status would block the tenant permanently with no way to recover.
    /// </summary>
    [Test]
    public async Task IsIngestEligibleAsync_CanceledRowOnActiveTenant_IsEligible()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner, out ITenantRepository tenants);
        tenants.GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(ActiveTenant(1, isActive: true));
        inner.IsIngestEligibleAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        await Assert.That(await service.IsIngestEligibleAsync(1)).IsTrue();
    }

    /// <summary>
    /// Unlocking entitlements must not unlock tenant deactivation: this is the enforcement point
    /// for a tenant pending deletion, and it has to survive in both deployment modes.
    /// </summary>
    [Test]
    public async Task IsIngestEligibleAsync_DeactivatedTenant_IsNotEligible()
    {
        SelfHostedSubscriptionService service = CreateService(out _, out ITenantRepository tenants);
        tenants.GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(ActiveTenant(1, isActive: false));

        await Assert.That(await service.IsIngestEligibleAsync(1)).IsFalse();
    }

    [Test]
    public async Task IsIngestEligibleAsync_UnknownTenant_IsNotEligible()
    {
        SelfHostedSubscriptionService service = CreateService(out _, out ITenantRepository tenants);
        tenants.GetTenantByIdAsync(99, Arg.Any<CancellationToken>()).Returns((Tenant?)null);

        await Assert.That(await service.IsIngestEligibleAsync(99)).IsFalse();
    }
```

Add this fixture helper to the test class — it sets all six required members so each test reads as one line:

```csharp
    private static Tenant ActiveTenant(int id, bool isActive)
    {
        return new Tenant
        {
            Id = id,
            Name = "Acme",
            ExternalId = $"ext-{id}",
            IsActive = isActive,
            CreatedAt = FixedNow,
            CreatedByUserId = 1,
            LogoUrl = string.Empty,
        };
    }
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "/*/*/SelfHostedSubscriptionServiceTests/*"
```

Expected: FAIL — the decorator has no `ITenantRepository` and still delegates.

- [ ] **Step 3: Implement**

Add `ITenantRepository` to the decorator's constructor (guarded), and replace the delegating `IsIngestEligibleAsync`:

```csharp
    /// <inheritdoc/>
    /// <remarks>
    /// Deliberately does not consult the subscription. Status is a hosted-product concept: a
    /// self-hosted deployment has no way to change a subscription, so a row left Canceled by a
    /// database import or a mode switch would block ingest permanently with no route to recover.
    /// The tenant's active flag is the check that does carry meaning here, and it must be kept —
    /// it is how deactivation and pending deletion stop telemetry within a single request.
    /// </remarks>
    public async Task<bool> IsIngestEligibleAsync(int tenantId, CancellationToken ct = default)
    {
        Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(tenantId, ct);

        return (tenant is not null) && tenant.IsActive;
    }
```

Update the registration in `ServiceCollectionExtensions.cs` to pass `sp.GetRequiredService<ITenantRepository>()`.

- [ ] **Step 4: Add the functional regression**

In `SelfHostedTelemetryTests.cs`, add `Ingest_ForCanceledSubscriptionOnActiveTenant_IsAccepted`, seeding a tenant with `SubscriptionStatus.Canceled` and asserting ingest succeeds. Keep the existing `Ingest_ForDeactivatedTenant_IsStillBlocked` — the pair is the point.

- [ ] **Step 5: Run the suites**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
dotnet run --project test/functional/web/functional.web.csproj
```

- [ ] **Step 6: Update the spec**

In `design.md` §4, change the `IsIngestEligibleAsync` row from "**delegate**" to "tenant active flag only — see below", and rewrite the explanatory paragraph. Keep the existing reasoning about why deactivation must still block; add why delegating was insufficient.

- [ ] **Step 7: Commit**

```bash
git commit -m "fix: base self-hosted ingest eligibility on tenant status alone"
```

---

### Task 3: Warn when a Billing section is configured but ignored

**Problem:** the spec specifies a warning when `Deployment:SelfHosted` is true with a populated `Billing` section. Never implemented. A SaaS deployment that loses its flag boots self-hosted with all billing configuration present and logs nothing. Today the mistake is caught only incidentally, because prod's ConfigMap also sets `InternalGrpc__Enabled=true`, which the validator rejects in self-hosted — protection that disappears if InternalGrpc is ever turned off.

**Where this goes, and where it does not.** Not in `DeploymentMode`'s constructor. That type is a pure value holder, and giving it a side-effecting constructor would mean adding two parameters to sixteen construction sites — including the two `Program.cs` files, which build it by hand *before the container exists*. There is no pre-container logger in this codebase to hand it: neither `Program.cs` has `Log.Logger`, `CreateBootstrapLogger` or `LoggerFactory.Create`, and every existing pre-container check (`ProductionSecretsGuard.Validate`, `CorsStartupValidator.Validate`) throws rather than logs.

Instead register a small `IHostedService` in `AddCoreOptions` that takes `DeploymentMode`, `IOptions<BillingOptions>` and `ILogger<T>` from DI and emits the warning once on start. `DeploymentMode` stays pure, no construction site changes, and the check is unit-testable with the `Substitute.For<ILogger<T>>()` + `Received().Log(...)` pattern already used in this repo.

There is no DI cycle: `EmailOptionsValidator` → `DeploymentMode` → `IOptions<DeploymentOptions>`, and `DeploymentOptionsValidator` consumes `IOptions<BillingOptions>` + `IConfiguration` with nothing consuming it back.

**Files:**
- Create: `src/services.core/Services/Deployment/IgnoredBillingConfigurationWarning.cs`
- Modify: `src/services.core/Extensions/ServiceCollectionExtensions.cs` (`AddCoreOptions`)
- Test: `test/unit/services.core/Deployment/IgnoredBillingConfigurationWarningTests.cs`

- [ ] **Step 1: Write the failing tests**

Three tests against the hosted service's `StartAsync`, using an `ILogger` substitute:

- self-hosted with `Billing:GrpcUrl` populated → one `Warning` logged
- self-hosted with an empty `Billing` section → nothing logged
- SaaS with `Billing:GrpcUrl` populated → nothing logged (this is the normal production case; a warning here would train operators to ignore it)

Assert on log level and that the message names `Deployment:SelfHosted`, following whatever `Received().Log(...)` form this repo already uses — grep for an existing logger-assertion test and copy its shape rather than inventing one.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "/*/*/IgnoredBillingConfigurationWarningTests/*"
```

- [ ] **Step 3: Implement the hosted service**

```csharp
/// <summary>
/// Emits a startup warning when billing is configured but the deployment mode means it will be
/// ignored. The mistake this catches is a hosted deployment that loses its Deployment:SelfHosted
/// setting: it would otherwise boot as self-hosted with a full billing configuration present and
/// report nothing, silently serving the product without billing.
/// </summary>
public sealed class IgnoredBillingConfigurationWarning : IHostedService
{
    // ctor takes DeploymentMode, IOptions<BillingOptions>, ILogger<IgnoredBillingConfigurationWarning>,
    // each guarded with ArgumentNullException.ThrowIfNull.

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_deploymentMode.IsSelfHosted && (string.IsNullOrWhiteSpace(_billingOptions.GrpcUrl) == false))
        {
            _logger.LogWarning(
                "Billing is configured but will be ignored because this is a self-hosted deployment. If this is the hosted deployment, Deployment:SelfHosted must be set to false.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

The second sentence is the point — it names the likely mistake rather than only reporting the state.

- [ ] **Step 4: Register it**

In `AddCoreOptions`, after the `DeploymentMode` registration:

```csharp
        services.AddHostedService<IgnoredBillingConfigurationWarning>();
```

Both `api-server` and `services-worker` call `AddCoreOptions`, so both get the check with no `Program.cs` edit. No `DeploymentMode` construction site changes — verify with `grep -rn "DeploymentMode" src/ test/ --include="*.cs"`, or csharp-lsp `findReferences` per this repo's convention. (Do **not** grep for `new DeploymentMode(`: every site uses target-typed `new()`, so that pattern matches nothing and would falsely report no call sites.)

Expect this warning to appear in the output of every self-hosted functional and integration test host: the base factory sets `Billing__GrpcUrl`, which `SelfHostedTestFactory` inherits. That is correct behaviour and harmless noise — not a sign the check is misfiring.

- [ ] **Step 5: Run all suites, then commit**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/web/functional.web.csproj
git commit -m "feat: warn when billing configuration is ignored in self-hosted mode"
```

---

### Task 4: Constructor null guards and naming cleanup

**Files:**
- Modify: `src/services.core/Options/EmailOptionsValidator.cs`, `DeploymentOptionsValidator.cs`
- Modify: `src/services.core/Services/Notifications/SmtpEmailService.cs`, `NoOpEmailService.cs`, `ResendEmailService.cs`
- Modify: nine test files carrying `BillingDisabled` method names

- [ ] **Step 1: Add the missing guards**

`ArgumentNullException.ThrowIfNull` on every injected constructor parameter of the types above, matching the pattern already used in `DeploymentMode`, `SelfHostedSubscriptionService` and `AddCoreServices`. Note `SmtpEmailService` and `ResendEmailService` dereference `emailOptions.Value` in the constructor — guard the `IOptions<T>` before reading `.Value`, or a null throws `NullReferenceException` instead of a named exception.

- [ ] **Step 2: Add a null-argument test per changed constructor**

One `[Test]` each asserting `ArgumentNullException`. The repo convention requires null-input coverage.

- [ ] **Step 3: Rename the retired `BillingDisabled` method names**

```bash
grep -rn "BillingDisabled" test/ --include="*.cs"
```

Nine methods across `SelfHostedEndpointTests.cs`, `AdminSettingsEndpointTests.cs` (2), `FleetAdminServiceTests.cs`, `BillingCatalogEndpointTests.cs` and `HangfireJobTypesTests.cs`. Rename `_BillingDisabled_` to `_SelfHosted_`. Assertions do not change — only the names, which currently describe a configuration flag that no longer exists.

**Expect eight, not nine, and do not chase the ninth.** Task 1 Step 4 already renames the `BillingCatalogEndpointTests` one, and Task 1 inserts tests into `SelfHostedEndpointTests.cs`, which shifts its line numbers. Trust the grep, not a count.

- [ ] **Step 4: Run everything, then commit**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/unit/database/unit.database.csproj
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
dotnet run --project test/functional/hangfire/functional.hangfire.csproj
git commit -m "refactor: add missing null guards and retire billing-disabled test naming"
```

---

### Task 5: One home for the subscription predicates

**Problem:** the Pro gate exists in three forms — the declarative tag covering 7 endpoints via `ProSubscriptionPreProcessor.RequiresProGate`, plus two hand-rolled copies of the identical predicate in `EventAlertService.cs:47` and `AlertEvaluationJob.cs:105`, which exist because pre-processors do not run in background jobs. Three copies of one rule. See the spec's §7 for the full audit.

**Files:**
- Create: `src/services.core/Services/Billing/SubscriptionPolicy.cs`
- Modify: `src/server/Services/Billing/ProSubscriptionPreProcessor.cs`, `SubscriptionStatusPreProcessor.cs`
- Modify: `src/services.core/Services/Alerts/EventAlertService.cs`, `AlertEvaluationJob.cs`
- Modify: `src/services.core/Services/Handlers/InvitationHandler.cs` (`:89` and `:126`)
- Modify: `src/services.core/Services/Handlers/TenantHandler.cs` (transactional provisioning)
- Test: `test/unit/services.core/Services/Billing/SubscriptionPolicyTests.cs`, `TenantHandler` and `SubscriptionStatusPreProcessor` coverage

**Two predicate shapes the first audit pass missed.** `InvitationHandler.cs:89` is a fourth shape — `(subscription is null) || (Tier == Free)` returning **402**, not 403 — that is neither the Pro predicate (no status test; Pro passes) nor the Team one. It becomes `SubscriptionPolicy.RequiresPaidTier`. `InvitationHandler.cs:126` re-derives `Tier != Team` to fork the invitee's role rather than to block; convert it to `RequiresTeam` so the predicate has one definition even where it drives a fork rather than a refusal. **Preserve the 402 status code and its message verbatim** — only the predicate moves.

- [ ] **Step 1: Write the failing tests**

`SubscriptionPolicy` is pure static functions over a nullable `TenantSubscription`, so the tests are a truth table. Cover every combination of null / each `SubscriptionTier` / each `SubscriptionStatus` for all three predicates:

Every predicate is **block-polarity** — `true` means refuse — so nobody has to track which way each one points:

- `RequiresPro(subscription)` — true when null, `Free`, or `Status != Active`
- `RequiresTeam(subscription)` — true when null or `Tier != Team`
- `RequiresPaidTier(subscription)` — true when null or `Tier == Free` (the `InvitationHandler` 402 rule)
- `BlocksMutations(subscription)` — true when null or `Status == Canceled`

`BlocksMutations` is deliberately not named `IsActive`: it returns true for `PastDue` and `None` too, so an "active" reading would be wrong, and a lone allow-polarity name beside three block-polarity ones is exactly what gets misread and then copied.

**Every predicate fails closed on null.** Assert that explicitly for each — it is the property the current code gets wrong in one place.

- [ ] **Step 2: Run to verify they fail, then implement**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "/*/*/SubscriptionPolicyTests/*"
```

Move the body of `ProSubscriptionPreProcessor.RequiresProGate` into `SubscriptionPolicy.RequiresPro` verbatim — do not restate the rule, move it, so there is no window where the two disagree.

- [ ] **Step 3: Repoint the three existing callers**

`ProSubscriptionPreProcessor` calls `SubscriptionPolicy.RequiresPro`. `EventAlertService.cs:47` and `AlertEvaluationJob.cs:105` replace their inlined `(subscription is null) || (Tier == Free) || (Status != Active)` with the same call. Behaviour is identical by construction; the point is that there is now one place to change it.

- [ ] **Step 4: Close the fail-open — and stop producing the state it lets through**

`SubscriptionStatusPreProcessor.cs:66` returns early when the subscription is null, permitting the mutation — the only gate that fails open. Replace with `SubscriptionPolicy.BlocksMutations`, which fails closed. Add a functional test for a row-less tenant.

**Give the null case its own message.** Do not reuse the `Canceled` text ("Subscription is canceled. Your account is in read-only mode. Please reactivate from the billing page.") — for a *missing* row there is nothing to reactivate, and in self-hosted the billing page 404s, so that message sends the operator somewhere that does not exist. Something like "No subscription found for this tenant." is accurate for both modes.

**This is not defence-in-depth, and hardening alone would be a regression.** The spec's original claim that transactional creation covers every path is wrong: there are **three** tenant-creation paths. `OnboardingHandler.cs:96` and `InvitationHandler.cs:254` provision the subscription in the same transaction as the `Tenant` insert (`:122`, `:266`). `TenantHandler.CreateAsync` does not — it opens a transaction at `:87`, inserts the tenant at `:89`, writes an audit row, commits at `:109`, and the class holds no subscription dependency at all. It is live: `TenantCreateEndpoint.cs:49-50` maps `POST /tenants` under the Admin policy and calls it at `:65`, and members can be attached afterwards through `MemberHandler`.

So a global-admin-created tenant permanently has no subscription row today. Closing the gate without closing the source would turn every such tenant read-only with no route to recovery — the billing endpoints 404 in self-hosted, and Task 1 widens that.

Therefore, in the same commit: give `TenantHandler.CreateAsync` the same transactional provisioning the other two paths have — create the `Free`/`Active` `TenantSubscription` inside the existing transaction, before the commit at `:109`, so a failure rolls the tenant back with it. Match `OnboardingHandler.cs:122` for the row's shape rather than inventing one. Add a functional test that a tenant created through `POST /tenants` can immediately perform a mutation, which is the regression a later reader would otherwise reintroduce by "simplifying" the handler.

- [ ] **Step 5: Run all suites, then commit**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/web/functional.web.csproj
git commit -m "refactor: give the subscription predicates a single home and provision every tenant"
```

---

### Task 6: Make the Team gate declarative

**Problem:** nine sites each load the subscription, inline `(subscription is null) || (Tier != SubscriptionTier.Team)`, and write their own 403 message. There is no test that can catch a Team feature whose gate was never written — the omission is invisible.

The Pro gate already solves this declaratively: `Tags(EndpointTags.RequiresProSubscription)` plus `Options(b => b.WithMetadata(new RequiresProFeatureMessage(...)))`, read by a global pre-processor. This task builds the Team equivalent, modelled on it exactly rather than inventing a second pattern.

**Files:** — note these live beside the Pro trio, not under `Endpoints/`
- Create: `src/server/Services/Billing/TeamSubscriptionPreProcessor.cs`, `src/server/Services/Billing/RequiresTeamFeatureMessage.cs`
- Modify: `src/server/Services/Billing/EndpointTags.cs`, `src/server/Program.cs` (configurator, `:441-445`)
- Modify: four endpoint gates and the five direct-call sites
- Test: unit tests for the pre-processor; SaaS regression test per gated endpoint

- [ ] **Step 1: Write the failing SaaS regression tests first — with discriminating fixtures**

**This is the highest-risk task in the batch, and the obvious test does not catch the risk.** A `TeamSubscriptionPreProcessor` that is not registered in the configurator fails silently and *open*: a tag is inert metadata, so with the inline checks deleted every converted endpoint becomes available to every tier in **SaaS**.

The trap: `AlertRuleCreateEndpoint:53` and `AlertRuleUpdateEndpoint:90` are **also Pro-tagged**. A `Free`-row tenant is refused by the *Pro* gate whether or not the Team gate exists, so a Free-row test on those two endpoints passes with the Team pre-processor unregistered — it proves nothing.

Use the fixture that discriminates:

| Endpoint | Fixture | Why |
| --- | --- | --- |
| alert-rule create, alert-rule update (custom) | **`Pro` / `Active` row** | passes the Pro gate; only the Team gate can refuse it |
| SSH key add, command send, audit log list | `Free` row | these carry no other gate |

Each asserts 403 and the endpoint's exact message string. These must pass before *and* after the refactor.

- [ ] **Step 2: Build the trio**

`EndpointTags.RequiresTeamSubscription`; `RequiresTeamFeatureMessage` carrying the per-feature message; `TeamSubscriptionPreProcessor` calling `SubscriptionPolicy.RequiresTeam` from Task 5. Copy the shape of `ProSubscriptionPreProcessor` including its `ResponseStarted()` short-circuit and its metadata lookup.

- [ ] **Step 3: Register it — after Pro**

`server/Program.cs:441-445` already registers three pre-processors in order: `TenantContextPreProcessor`, `SubscriptionStatusPreProcessor`, `ProSubscriptionPreProcessor`. Add the Team one **last**.

Order is observable, not cosmetic. `AlertRuleCreateEndpoint` will carry both the Pro and Team tags, and today a Free tenant receives the *Pro* message because the Pro gate fires before the handler's Team check is ever reached. Registering Team ahead of Pro changes which message that tenant sees, and functional tests assert on message strings.

**Verify with a test, not by reading** — the failure mode here is silent.

- [ ] **Step 4: Convert the four taggable endpoint gates**

`AlertRuleCreateEndpoint:69`, `MachineAuthorizedKeyAddEndpoint:62`, `CommandSendEndpoint:49`, `AuditLogListEndpoint:75`. Each loses its inline check and gains a `Tags(...)` entry plus a `RequiresTeamFeatureMessage`.

Preserve each endpoint's existing wording verbatim:

| Endpoint | Message |
| --- | --- |
| alert-rule create | "Custom alert rules require a Team subscription" |
| SSH key add | "Remote commands require a Team subscription" |
| command send | "Remote commands require a Team subscription" |
| audit log list | "Audit log requires a Team subscription" |

The SSH-key and command-send strings are **identical**, which is a pre-existing copy-paste in the SSH key endpoint. Preserve it exactly; do not improve it to mention SSH keys during a refactor whose contract is bit-for-bit behaviour preservation. Fix it separately if it is worth fixing.

`AlertRuleUpdateEndpoint:124` stays inline: its gate is conditional on the loaded `rule.IsCustom`, which a tag cannot express. Convert it to call `SubscriptionPolicy.RequiresTeam`.

- [ ] **Step 5: Convert the four non-endpoint gates — watching polarity at two of them**

`TenantOidcHandler:65,107` and `MemberHandler:158` are not endpoints, so a pre-processor cannot reach them. They are block-form and call `SubscriptionPolicy.RequiresTeam` directly.

**Two sites are grant-form and must be inverted.** The spec's original prose listed `DataExportHandler:163` among the "nine identical copies" and flagged only `AuthProviderChallengeEndpoint:98` as positive-form. That is wrong, and it is the most dangerous error in this batch because the wrong version compiles:

| Site | Actual source | Convert to |
| --- | --- | --- |
| `AuthProviderChallengeEndpoint:98` | `bool teamTier = (subscription is not null) && (subscription.Tier == SubscriptionTier.Team);` | `RequiresTeam(subscription) == false` |
| `DataExportHandler:163` | `if (subscription is not null && subscription.Tier == SubscriptionTier.Team)` guarding `ExportAuditLogAsync` | `if (SubscriptionPolicy.RequiresTeam(subscription) == false)` |

`DataExportHandler` grants an inclusion — it returns no 403 and takes no early exit, and it runs inside a background export job. Writing the bare block-polarity predicate there exports audit logs for non-Team tenants and withholds them from Team ones, with no test failing unless one is written. Write that test: a Team tenant's export contains the audit log, a Free tenant's does not.

- [ ] **Step 6: Pin the validation-versus-gate ordering**

Moving a gate from the handler into a pre-processor changes *when* it fires relative to request validation, so pin the result with a test rather than reasoning about it.

An earlier draft of this step argued from `IPreProcessorContext.ValidationFailures` that pre-processors run *before* the automatic 400, and that an invalid body from a non-Team tenant on `CommandSend` might therefore flip from 400 to 403. That inference is backwards. In the FastEndpoints version in use, `Endpoint.ExecAsync` runs bind → `OnBeforeValidate` → `ValidateRequest` → `OnAfterValidate` → pre-processors → handler, and `ValidateRequest` throws `ValidationFailureException` (caught and rendered as the automatic 400) before any pre-processor runs. `ValidationFailures` is exposed on the context so a pre-processor can inspect or add to failures that did not throw — not because it precedes the 400.

So validation still wins, and the conversion moves the gate earlier relative to the *handler* only. Expect 400 both before and after. Still write the test — one functional test posting an invalid body as a non-Team tenant — and record the code the framework actually produces rather than the one this paragraph predicts.

- [ ] **Step 7: Run everything, then commit**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
git commit -m "refactor: gate team features declaratively like pro features"
```

---

### Task 7: Delete `EnsureSubscriptionExistsAsync`

The decision and its full reasoning are recorded in the spec's §7. Read that before starting — the summary is that the method has never been called, its safe branches duplicate transactional provisioning, its `Canceled`-paid branch performs exactly the locally-derived downgrade that `StripeSyncJob.cs:160-162` explicitly refuses to make (guarding `:163-170`), and in self-hosted it would destroy imported subscription history.

**Files:**
- Modify: `src/services.core/Services/Billing/ISubscriptionService.cs` (remove `:57`)
- Modify: `src/services.core/Services/Billing/SubscriptionService.cs` (remove `:217-258`)
- Modify: `src/services.core/Services/Billing/SelfHostedSubscriptionService.cs` (remove the delegation)
- Modify: `test/unit/services.core/Services/Billing/SubscriptionServiceTests.cs` (five call sites: :400,422,444,827,854)
- Modify: `test/unit/services.core/Services/Billing/SelfHostedSubscriptionServiceTests.cs`
- Modify: `docs/specs/2026-08-23-self-hosted-mode-design.md`, `CLAUDE.md`

- [ ] **Step 1: Delete the member and its implementations**

Compiler-guided throughout. In `SelfHostedSubscriptionServiceTests`, `ProvisioningMembers_DelegateToInner` covers **both** `EnsureSubscriptionExistsAsync` and `ProvisionFreeSubscriptionAsync` — keep the second half.

- [ ] **Step 2: Update the thirteen-member prose**

"All thirteen members" is load-bearing and becomes **twelve** in: the spec's §4 member table and surrounding text, vord CLAUDE.md's deployment-mode paragraph, and the parent plan's Task 3 Step 6 grep check, which asserts both files report 13. The shipped decorator's XML remarks do not name a count — check anyway, but do not manufacture an edit there.

```bash
grep -rn "thirteen\|13 members\| 13$" docs/ CLAUDE.md src/services.core/Services/Billing/SelfHostedSubscriptionService.cs
```

Missing one leaves documentation that fails its own audit.

- [ ] **Step 3: Run everything, then commit**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
git commit -m "refactor: remove the uncalled subscription reconciliation helper"
```

---

### Task 8: Stop `MachineService` reimplementing the billing sync

**Problem:** `MachineService.cs:380-403` reimplements inline the entire operation that `IMachineBillingSync.ReportActiveMachineUsageAsync` already provides — load subscription, test the `(Tier == Pro) || (Tier == Team)` allowlist, load tenant, compute billable count, report quantity, same best-effort `try`/`catch`. Even the comment explaining the `Tier.None` case is duplicated verbatim (`MachineBillingSync.cs:59-62` and `MachineService.cs:383-386`).

The two are independent copies, not a call: `MachineService` does not reference `IMachineBillingSync` today, and `ReportActiveMachineUsageAsync`'s only caller is `MachineHandler.cs:124` on the machine *deletion* path. After this task one implementation serves both.

This is the last duplicated tier rule after Task 6, and it is deleted rather than extracted — see the spec's §7 for why a shared `IsBillableTier` helper would preserve the wrong boundary.

**Files:**
- Modify: `src/services.core/Services/Machines/MachineService.cs`
- Test: `test/unit/services.core/Services/Machines/MachineServiceTests.cs`

- [ ] **Step 1: Confirm the substitution is exact**

Read `MachineBillingSync.ReportActiveMachineUsageAsync` (`:52-82`) against `MachineService.cs:380-403` and confirm the two produce the same billing outcome for the same tenant state.

They differ in three accepted ways, all already checked — do **not** stop on these: `MachineService` resolves `ITenantRepository` from the scope inside its `try` while `MachineBillingSync` takes it by constructor (same scoped instance either way); `MachineBillingSync` re-reads the subscription rather than reusing one loaded earlier; and the two log under different categories. Stop and report only if you find a *fourth* difference that changes the reported quantity or whether it is reported at all.

- [ ] **Step 2: Write the failing test**

In `MachineServiceTests`, assert that registering a machine invokes `IMachineBillingSync.ReportActiveMachineUsageAsync` for the tenant, using a substitute. Add the negative case: a failing sync must not fail registration — the existing behaviour is best-effort, and `ReportActiveMachineUsageAsync` swallows its own exceptions, so the call site must not reintroduce a throw.

- [ ] **Step 3: Replace the inline block**

Delete `MachineService.cs:380-403` in full — the lead comment, the `try`/`catch` **including the catch body**, the allowlist, the tenant load, the quantity call. Note the range: deleting `:381-400` instead leaves the orphaned `catch` body at `:401-403` and does not compile.

Also delete the subscription load at `:313`. Its only readers are `:387`, `:388` and `:395`, all inside the deleted block; `effectiveLimits` and `machineLimit` at `:314-315` are separate and stay. Leaving it behind is an assigned-but-never-read local and a wasted round-trip, and `TreatWarningsAsErrors` will reject it. With `:313` gone the substitution is read-neutral: one subscription read before, one after.

Then call the service **at the position the deleted block occupied** — after the machine row is committed by `CreateMachineWithKeyAsync` and after the Redis pending-key write, immediately before the `return`:

```csharp
        IMachineBillingSync billingSync = scope.ServiceProvider.GetRequiredService<IMachineBillingSync>();
        await billingSync.ReportActiveMachineUsageAsync(token.TenantId, cancellationToken);
```

**Position is load-bearing, not stylistic.** The reported quantity is `Math.Max(activeMachineCount, tierFloor)` computed from a live count. Hoisting the call up with the other scoped resolutions — which all sit before `CreateMachineWithKeyAsync` — reports a count short by one, silently, because `ReportActiveMachineUsageAsync` swallows its own failures and any quantity is accepted downstream. Resolving the service early is fine; awaiting it early is not.

`ReportActiveMachineUsageAsync` already wraps itself in best-effort `try`/`catch` and logs, so do **not** add another wrapper — that would double-log and hide nothing extra.

- [ ] **Step 4: Drop the now-unused billing dependency**

`:396` is the only use of `_billingApiClient` in `MachineService`. Remove the field, the constructor parameter and the assignment, then update every test that constructs `MachineService`. The point of this task is that the machine service stops knowing about billing at all — leaving the dependency in place would keep the wrong boundary while deleting only the symptom.

- [ ] **Step 5: Run everything, then commit**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
dotnet run --project test/functional/web/functional.web.csproj
git commit -m "refactor: report machine usage through the billing sync service"
```

---

## Nothing deferred

Every question this work surfaced is now either decided in the spec or scoped as a task above. For
the record, so none of these get re-raised as open items:

- *The delete-vs-wire-up decision for `EnsureSubscriptionExistsAsync`* — decided, reasoning in the
  spec's §7, work scoped as Task 7.
- *`SubscriptionStatusPreProcessor`'s null fail-open* — fixed in Task 5 Step 4.
- *The duplicated billable-tier allowlist* — fixed in Task 8, by deletion rather than extraction.
- *The states left unreconciled after Task 7* (a missing row; a `Free` row drifted non-`Active`) — a
  documented non-goal in §7, not pending work. Neither arises from normal operation, and the only
  writer that can produce them is the same admin surface that can correct them.

## Verification Checklist

- [ ] Self-hosted: catalog, invoices, upcoming-invoice and usage-history return 404; subscription returns 200.
- [ ] Self-hosted: a `Canceled` subscription row on an active tenant ingests; a deactivated tenant does not.
- [ ] SaaS: all four endpoints behave exactly as before — no `DeploymentMode` guard fires.
- [ ] A self-hosted process with `Billing:GrpcUrl` set logs the warning at startup; one without logs nothing.
- [ ] `grep -rn "BillingDisabled" test/` returns nothing.
- [ ] Every new constructor parameter has a null guard and a test.
- [ ] `dotnet build machine-info.slnx` — zero errors (the pre-existing SSH.NET NU1903 warnings are unrelated).
- [ ] `SubscriptionPolicy` is the only place the Pro, Team, paid-tier and mutation-blocking predicates are expressed; no site re-derives them. The billable-tier allowlist in `MachineBillingSync`/`MachineService` is a documented exclusion, not an oversight.
- [ ] Every predicate in `SubscriptionPolicy` is block-polarity (`true` means refuse).
- [ ] Every predicate fails closed on a null subscription, including `SubscriptionStatusPreProcessor`.
- [ ] Both grant-form sites — `AuthProviderChallengeEndpoint` and `DataExportHandler` — call `RequiresTeam(...) == false`, proven by a test that a Team tenant's export contains the audit log and a Free tenant's does not.
- [ ] `TenantHandler.CreateAsync` provisions a subscription in the same transaction as the tenant insert; a tenant created through `POST /tenants` can immediately mutate.
- [ ] Machine registration still reports the billable quantity **after** the machine row is committed, not before.
- [ ] `TeamSubscriptionPreProcessor` is registered **after** `ProSubscriptionPreProcessor`, proven by a SaaS regression test per gated endpoint — not by reading the registration.
- [ ] The alert-rule regression tests use a **Pro/Active** fixture, not Free — a Free fixture is refused by the Pro gate and would pass with the Team pre-processor unregistered.
- [ ] `InvitationHandler:89` still returns **402** with its original message.
- [ ] SaaS: a Free-row tenant is still refused by every Team feature after the refactor.
- [ ] If Task 7 ran: no occurrence of "thirteen members" survives in docs, CLAUDE.md, the decorator's XML remarks, or the parent plan's grep check.
- [ ] `MachineService` no longer references `IBillingApiClient`, and machine registration still reports billable quantity.
- [ ] No AI attribution in any commit; nothing pushed.
