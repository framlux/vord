# Billing Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Sequencing:** This plan runs **AFTER** `docs/plans/2026-07-20-stripe-wip-commit-readiness.md` (plan A) has been fully executed and committed in vord-internal. Plan A owns the same webhook handler, the `StripeCustomers.BillingInterval` column upkeep, and the `interval_changed` audit event; it leaves the contract at the (unpublished) 1.14.0. Verify vord-internal's working tree is clean before starting Task 1 — if `git -C ~/Repositories/framlux/vord-internal status --short` still shows the Stripe-WIP modifications, stop and run plan A first.

**Spec:** `docs/specs/2026-07-20-billing-visibility-design.md` (approved 2026-07-20). Customer-facing interval-change history is **out of scope**.

**Goal:** Tenant admins see their plan (tier + authoritative billing interval), per-machine cost, and estimated bill on `/settings/billing`, correct in every subscription state. Free tenants see the upgrade funnel: Pro/Team pricing cards from a real catalog with a monthly/annual toggle and checkout CTA. The period-length interval heuristic in the web UI is deleted.

**Architecture:** Extend the gRPC pull path end to end: `BillingService.proto` gains a `BillingInterval` enum, `GetSubscriptionStatusResponse.billing_interval = 7`, and a new `GetPublicCatalog` RPC (package → **1.15.0**). billing-api derives the interval from the **live subscription's price** (CatalogPrice lookup by current price id; unknown → NONE — never the `StripeCustomers.BillingInterval` column) and serves the catalog from active `Prices` joined to `TierMappings`. vord's fleet server proxies both: `SubscriptionDto` gains `billingInterval` (`"monthly"`/`"annual"`/null), and a new tenant-authed `/v1/api/billing/catalog` endpoint (RequiresTenant, deliberately **no** Pro gate) serves the pricing cards. The SvelteKit page consumes both and gets a visual refresh within the existing Skeleton/Tailwind language.

**Tech Stack:** proto3/Grpc.Tools (vord-internal `src/billingGrpc`, NuGet `Framlux.Vord.BillingGrpc`), .NET 10 + FastEndpoints + LinqToDB + TUnit + NSubstitute (both repos), SvelteKit + Svelte 5 runes + Tailwind + Vitest (vord `src/web`).

## Global Constraints

- Both repos: license header on every code file, no `var`, Allman braces, explicit boolean comparisons (`== false`, never `!x`), parens around compound conditions, blank line before `return` (unless preceded by a comment), alphabetical `using`s (aliases last), XML docs on public members, blank line at end of file, one type per file (FastEndpoints Request/Response DTOs may co-locate with their endpoint).
- Builds must finish with **0 errors, 0 warnings** (`TreatWarningsAsErrors` is on in vord-internal src projects).
- TUnit runs with `dotnet run --project …`, **never** `dotnet test`. Filter gotcha: if `--treenode-filter` misbehaves or results look stale, rebuild with `dotnet build --no-incremental` and run the compiled executable directly with `--treenode-filter "/*/*/ClassName/*"`.
- TDD for every behavior-bearing change: write the failing test, see it fail, implement, see it pass.
- Commits ARE allowed, one (or more) per task, always `git -c commit.gpgsign=false commit`, natural-language messages, **no AI attribution/Co-Authored-By footers**. vord-internal and vord commits are separate.
- vord: **NEVER stage `nuget.config` or anything under `docs/`.** `nuget.config` already carries the uncommitted `billinggrpc-local` source (`/tmp/billinggrpc-local`) — do not re-add it, do not commit it, do not revert it. Always `git add` explicit file lists; never `git add .` / `git add -A`.
- csharp-lsp (LSP tool) for C# symbol lookup (callers of `StripeSubscriptionStatus`, etc.), not grep.
- Package-cache gotcha: if the proto is edited again after Task 1's pack, delete the cached package before re-packing: `rm -rf ~/.nuget/packages/framlux.vord.billinggrpc/1.15.0 && dotnet pack src/billingGrpc -c Release -o /tmp/billinggrpc-local`.

---

### Task 1: Proto contract 1.15.0 + local pack + vord pin bump

**Files:**
- Modify `vord-internal/src/billingGrpc/protos/BillingService.proto` (enum after line 23; RPC in service block lines 38–47; field 7 in `GetSubscriptionStatusResponse` lines 90–97; new messages after line 97)
- Modify `vord-internal/src/billingGrpc/billingGrpc.csproj` (line 10: `<Version>1.14.0</Version>`)
- Modify `vord-internal/src/billing-api/Services/BillingManagementService.cs` (usings, line 399)
- Modify `vord-internal/src/billing-api/Services/WebhookProcessorService.cs` (usings only)
- Modify `vord-internal/src/billing-api/Endpoints/Admin/MigrationMeteredEndpoint.cs` (usings only)
- Modify `vord-internal/test/billing/Services/BillingManagementServiceTests.cs` (usings, lines 1278 and 1347)
- Modify `vord/src/services.core/services.core.csproj` (line 36: package pin)

**Interfaces:** Produces proto enum `BillingInterval` (C#: `Framlux.Vord.BillingGrpc.BillingInterval.{None,Monthly,Annual}`), `GetSubscriptionStatusResponse.BillingInterval` (field 7), `BillingManagement.GetPublicCatalog(GetPublicCatalogRequest) returns (GetPublicCatalogResponse)` with `CatalogPriceItem { BillingTier Tier; BillingInterval Interval; long UnitAmountCents; string Currency; bool IsMetered; }`, NuGet `Framlux.Vord.BillingGrpc 1.15.0`.

- [ ] **Step 1:** In `BillingService.proto`, add the interval enum directly after the `BillingTier` enum (after line 23), matching the file's UPPER_SNAKE prefix style. Note the deliberate `NONE` (not `UNSPECIFIED`) zero value per the approved spec:

```proto
enum BillingInterval {
  BILLING_INTERVAL_NONE = 0;
  BILLING_INTERVAL_MONTHLY = 1;
  BILLING_INTERVAL_ANNUAL = 2;
}
```

- [ ] **Step 2:** Add the RPC to the `BillingManagement` service block (currently lines 38–47), after the `ListInvoices` line:

```proto
service BillingManagement {
  rpc UpdateSubscriptionQuantity(UpdateQuantityRequest) returns (UpdateQuantityResponse);
  rpc ReportMachineUsage(ReportMachineUsageRequest) returns (ReportMachineUsageResponse);
  rpc CancelSubscription(CancelSubscriptionRequest) returns (CancelSubscriptionResponse);
  rpc GetSubscriptionStatus(GetSubscriptionStatusRequest) returns (GetSubscriptionStatusResponse);
  rpc SwapSubscriptionPrice(SwapSubscriptionPriceRequest) returns (SwapSubscriptionPriceResponse);
  rpc ResumeSubscription(ResumeSubscriptionRequest) returns (ResumeSubscriptionResponse);
  rpc GetUpcomingInvoice(GetUpcomingInvoiceRequest) returns (GetUpcomingInvoiceResponse);
  rpc ListInvoices(ListInvoicesRequest) returns (ListInvoicesResponse);
  rpc GetPublicCatalog(GetPublicCatalogRequest) returns (GetPublicCatalogResponse);
}
```

- [ ] **Step 3:** Extend `GetSubscriptionStatusResponse` (currently ends at field 6, line 96) and add the catalog messages after it (after line 97):

```proto
message GetSubscriptionStatusResponse {
  bool cancel_at_period_end = 1;
  string stripe_status = 2;
  string price_id = 3;
  int32 quantity = 4;
  google.protobuf.Timestamp current_period_end = 5;
  BillingTier tier = 6;
  BillingInterval billing_interval = 7;
}

// Public pricing catalog (active prices with their tier and interval)
message GetPublicCatalogRequest {}
message GetPublicCatalogResponse {
  repeated CatalogPriceItem items = 1;
}
message CatalogPriceItem {
  BillingTier tier = 1;
  BillingInterval interval = 2;
  int64 unit_amount_cents = 3;
  string currency = 4;
  bool is_metered = 5;
}
```

- [ ] **Step 4:** Bump `src/billingGrpc/billingGrpc.csproj` line 10: `<Version>1.14.0</Version>` → `<Version>1.15.0</Version>`. Confirm codegen: `dotnet build src/billingGrpc` (from vord-internal root) — expect Build succeeded, and generated `Framlux.Vord.BillingGrpc.BillingInterval` with members `None`, `Monthly`, `Annual` (prefix stripped by protoc's C# codegen).
- [ ] **Step 5:** Fix the `BillingInterval` name collision the new proto enum creates (four files import both `Framlux.Billing.Api.Models` and `Framlux.Vord.BillingGrpc` and use the bare name → CS0104):
  - `src/billing-api/Services/WebhookProcessorService.cs` and `src/billing-api/Endpoints/Admin/MigrationMeteredEndpoint.cs` — zero body changes; add one using-alias after the regular usings:

```csharp
using BillingInterval = Framlux.Billing.Api.Models.BillingInterval;
```

  - `src/billing-api/Services/BillingManagementService.cs` — this file will need both types (Tasks 2–3), so follow its existing `Domain*` alias convention (lines 14–15). Add alongside `DomainBillingTier`/`DomainPendingActionType`:

```csharp
using DomainBillingInterval = Framlux.Billing.Api.Models.BillingInterval;
```

  and change line 399 from `p.Interval == BillingInterval.Monthly` to `p.Interval == DomainBillingInterval.Monthly`.
  - `test/billing/Services/BillingManagementServiceTests.cs` — add the same `DomainBillingInterval` alias to the usings and change the two seed sites (lines 1278 and 1347) from `Interval = BillingInterval.Monthly,` to `Interval = DomainBillingInterval.Monthly,`.
- [ ] **Step 6:** Verify vord-internal: `dotnet build vord-internal.slnx -c Release` (0 errors, 0 warnings), then `dotnet run --project test/billing/billing.csproj` — expect all tests passing, 0 failed.
- [ ] **Step 7:** Pack the local handoff package: `dotnet pack src/billingGrpc -c Release -o /tmp/billinggrpc-local` — expect `Successfully created package '/tmp/billinggrpc-local/Framlux.Vord.BillingGrpc.1.15.0.nupkg'`.
- [ ] **Step 8:** In vord, bump the pin in `src/services.core/services.core.csproj` line 36:

```xml
<PackageReference Include="Framlux.Vord.BillingGrpc" Version="1.15.0" />
```

  Then from vord root: `dotnet restore machine-info.slnx` (must resolve 1.15.0 from the existing `billinggrpc-local` source — do **not** touch `nuget.config`), then `dotnet build machine-info.slnx` — 0 errors, 0 warnings (the contract change is additive; nothing in vord breaks).
- [ ] **Step 9:** Commit both repos:

```bash
cd ~/Repositories/framlux/vord-internal
git add src/billingGrpc/protos/BillingService.proto src/billingGrpc/billingGrpc.csproj \
  src/billing-api/Services/BillingManagementService.cs src/billing-api/Services/WebhookProcessorService.cs \
  src/billing-api/Endpoints/Admin/MigrationMeteredEndpoint.cs test/billing/Services/BillingManagementServiceTests.cs
git -c commit.gpgsign=false commit -m "Add billing interval and public catalog to the billing gRPC contract (1.15.0)"

cd ~/Repositories/framlux/vord
git add src/services.core/services.core.csproj
git -c commit.gpgsign=false commit -m "Consume billing gRPC contract 1.15.0"
git status --short   # nuget.config must still show as modified-unstaged; docs/ untracked
```

### Task 2: billing-api — derive billing_interval from the live subscription's price (TDD)

**Files:**
- Modify `vord-internal/test/billing/Services/BillingManagementServiceTests.cs` (new tests near the existing `GetSubscriptionStatus` tier-mapping tests, lines 1256–1374)
- Modify `vord-internal/src/billing-api/Services/BillingManagementService.cs` (`GetSubscriptionStatus`, lines 339–363)

**Interfaces:** Consumes `_db.Prices` (`CatalogPrice`: `Id`, `StripePriceId`, `Interval` (domain `BillingInterval`), `IsActive`), `_db.TierMappings` (`PriceId`, `Tier`). Produces `GetSubscriptionStatusResponse.BillingInterval` set from `CatalogPrice.Interval`; unknown/missing price → stays `BillingInterval.None` (proto default). No read of `StripeCustomers.BillingInterval`.

- [ ] **Step 1 (failing tests first):** Add to `BillingManagementServiceTests` (the file's `SeedCustomer`, `CreateStripeSubscription`, `CreateService`, `CreateCallContext` helpers already exist; seed pattern mirrors `GetSubscriptionStatus_KnownPriceId_ResolvesTierFromTierMappings` at line 1259):

```csharp
    // --- Billing interval derivation in GetSubscriptionStatus ---

    private static async Task<int> SeedCatalogPrice(
        BillingTestDatabaseFactory dbFactory,
        string stripePriceId,
        DomainBillingInterval interval,
        Framlux.Billing.Api.Models.BillingTier tier,
        long unitAmountCents = 599,
        bool isActive = true)
    {
        int productId = await dbFactory.Context.InsertWithInt32IdentityAsync(new CatalogProduct
        {
            StripeProductId = $"prod_{stripePriceId}",
            Name = $"Product for {stripePriceId}",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        int priceId = await dbFactory.Context.InsertWithInt32IdentityAsync(new CatalogPrice
        {
            StripePriceId = stripePriceId,
            ProductId = productId,
            Interval = interval,
            UnitAmountCents = unitAmountCents,
            Currency = "usd",
            IsMetered = true,
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await dbFactory.Context.InsertAsync(new TierMapping
        {
            PriceId = priceId,
            Tier = tier,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        return priceId;
    }

    [Test]
    public async Task GetSubscriptionStatus_MonthlyCatalogPrice_ReturnsMonthlyInterval()
    {
        using BillingTestDatabaseFactory dbFactory = new();
        await SeedCustomer(dbFactory);
        await SeedCatalogPrice(dbFactory, "price_pro_123", DomainBillingInterval.Monthly, Framlux.Billing.Api.Models.BillingTier.Pro);

        IStripeGateway stripeGateway = Substitute.For<IStripeGateway>();
        stripeGateway.GetSubscriptionAsync("sub_test123", Arg.Any<CancellationToken>())
            .Returns(CreateStripeSubscription(priceId: "price_pro_123"));
        BillingManagementService service = CreateService(dbFactory, stripeGateway);

        GetSubscriptionStatusResponse result = await service.GetSubscriptionStatus(
            new GetSubscriptionStatusRequest { TenantExternalId = "tenant-ext-1" },
            CreateCallContext());

        await Assert.That(result.BillingInterval).IsEqualTo(Framlux.Vord.BillingGrpc.BillingInterval.Monthly);
        await Assert.That(result.Tier).IsEqualTo(Framlux.Vord.BillingGrpc.BillingTier.Pro);
    }

    [Test]
    public async Task GetSubscriptionStatus_AnnualCatalogPrice_ReturnsAnnualInterval()
    {
        using BillingTestDatabaseFactory dbFactory = new();
        await SeedCustomer(dbFactory);
        await SeedCatalogPrice(dbFactory, "price_pro_annual", DomainBillingInterval.Annual, Framlux.Billing.Api.Models.BillingTier.Pro, unitAmountCents: 5990);

        IStripeGateway stripeGateway = Substitute.For<IStripeGateway>();
        stripeGateway.GetSubscriptionAsync("sub_test123", Arg.Any<CancellationToken>())
            .Returns(CreateStripeSubscription(priceId: "price_pro_annual"));
        BillingManagementService service = CreateService(dbFactory, stripeGateway);

        GetSubscriptionStatusResponse result = await service.GetSubscriptionStatus(
            new GetSubscriptionStatusRequest { TenantExternalId = "tenant-ext-1" },
            CreateCallContext());

        await Assert.That(result.BillingInterval).IsEqualTo(Framlux.Vord.BillingGrpc.BillingInterval.Annual);
    }

    [Test]
    public async Task GetSubscriptionStatus_UnknownPriceId_ReturnsNoneInterval()
    {
        using BillingTestDatabaseFactory dbFactory = new();
        await SeedCustomer(dbFactory);

        // No catalog row exists for this price id; interval must stay NONE, not throw
        IStripeGateway stripeGateway = Substitute.For<IStripeGateway>();
        stripeGateway.GetSubscriptionAsync("sub_test123", Arg.Any<CancellationToken>())
            .Returns(CreateStripeSubscription(priceId: "price_unknown_xyz"));
        BillingManagementService service = CreateService(dbFactory, stripeGateway);

        GetSubscriptionStatusResponse result = await service.GetSubscriptionStatus(
            new GetSubscriptionStatusRequest { TenantExternalId = "tenant-ext-1" },
            CreateCallContext());

        await Assert.That(result.BillingInterval).IsEqualTo(Framlux.Vord.BillingGrpc.BillingInterval.None);
    }

    [Test]
    public async Task GetSubscriptionStatus_NoSubscriptionId_ReturnsNoneInterval()
    {
        using BillingTestDatabaseFactory dbFactory = new();
        await SeedCustomer(dbFactory, subscriptionId: null);
        IStripeGateway stripeGateway = Substitute.For<IStripeGateway>();
        BillingManagementService service = CreateService(dbFactory, stripeGateway);

        GetSubscriptionStatusResponse result = await service.GetSubscriptionStatus(
            new GetSubscriptionStatusRequest { TenantExternalId = "tenant-ext-1" },
            CreateCallContext());

        await Assert.That(result.BillingInterval).IsEqualTo(Framlux.Vord.BillingGrpc.BillingInterval.None);
    }
```

  Run `dotnet run --project test/billing/billing.csproj -- --treenode-filter "/*/*/BillingManagementServiceTests/*"` — the two interval-assertion tests on seeded prices must FAIL (interval stays None) before the implementation; the None-path tests pass already (that is fine — they are regression guards).
- [ ] **Step 2:** Implement in `BillingManagementService.GetSubscriptionStatus`. Replace the tier-resolution block (lines 348–362, `// Resolve tier from TierMappings...` through its closing brace) with a single price-row lookup that yields both interval and tier. The interval comes from the **live subscription's** price id — deliberately not from `StripeCustomers.BillingInterval`:

```csharp
                // Resolve interval and tier from the catalog using the live subscription's price id.
                // The stored StripeCustomers.BillingInterval column is intentionally not consulted:
                // the subscription's actual price is the source of truth.
                if (string.IsNullOrEmpty(priceId) == false)
                {
                    CatalogPrice? catalogPrice = await _db.Prices
                        .Where(p => p.StripePriceId == priceId)
                        .FirstOrDefaultAsync(context.CancellationToken);

                    if (catalogPrice is not null)
                    {
                        response.BillingInterval = catalogPrice.Interval.ToProtoBillingInterval();

                        DomainBillingTier? resolvedTier = await _db.TierMappings
                            .Where(tm => tm.PriceId == catalogPrice.Id)
                            .Select(tm => (DomainBillingTier?)tm.Tier)
                            .FirstOrDefaultAsync(context.CancellationToken);

                        if ((resolvedTier is not null) && (resolvedTier.Value != DomainBillingTier.None))
                        {
                            response.Tier = resolvedTier.Value.ToProtoBillingTier();
                        }
                    }
                }
```

- [ ] **Step 3:** Add the domain→proto conversion to `BillingIntervalExtensions` in `src/billing-api/Models/BillingInterval.cs` (append inside the existing static class; fully-qualified proto names, no new using — the file has no BillingGrpc import so there is no ambiguity):

```csharp
    /// <summary>
    /// Converts a domain <see cref="BillingInterval"/> to the proto contract's
    /// <see cref="Framlux.Vord.BillingGrpc.BillingInterval"/>. Unknown values map to None.
    /// </summary>
    /// <param name="interval">The domain billing interval to convert.</param>
    /// <returns>The corresponding proto billing interval.</returns>
    public static Framlux.Vord.BillingGrpc.BillingInterval ToProtoBillingInterval(this BillingInterval interval)
    {
        return interval switch
        {
            BillingInterval.Monthly => Framlux.Vord.BillingGrpc.BillingInterval.Monthly,
            BillingInterval.Annual => Framlux.Vord.BillingGrpc.BillingInterval.Annual,
            _ => Framlux.Vord.BillingGrpc.BillingInterval.None,
        };
    }
```

- [ ] **Step 4:** Verify: `dotnet build vord-internal.slnx -c Release` (0 warnings), `dotnet run --project test/billing/billing.csproj` — all tests pass, including the four new ones and the three pre-existing tier-mapping tests (the tier lookup was restructured; those tests are the regression proof).
- [ ] **Step 5:** Commit:

```bash
cd ~/Repositories/framlux/vord-internal
git add src/billing-api/Services/BillingManagementService.cs src/billing-api/Models/BillingInterval.cs \
  test/billing/Services/BillingManagementServiceTests.cs
git -c commit.gpgsign=false commit -m "Derive subscription billing interval from the live price's catalog entry"
```

### Task 3: billing-api — GetPublicCatalog RPC (TDD)

**Files:**
- Modify `vord-internal/test/billing/Services/BillingManagementServiceTests.cs` (new tests, reusing Task 2's `SeedCatalogPrice`)
- Modify `vord-internal/src/billing-api/Services/BillingManagementService.cs` (new `GetPublicCatalog` override at end of class)

**Interfaces:** Produces `override Task<GetPublicCatalogResponse> GetPublicCatalog(GetPublicCatalogRequest request, ServerCallContext context)` — active prices only, joined to `TierMappings`; rows without a tier mapping (or mapped to `None`) are skipped; output ordered by tier then interval.

- [ ] **Step 1 (failing tests first):** Append to `BillingManagementServiceTests`:

```csharp
    // --- GetPublicCatalog ---

    [Test]
    public async Task GetPublicCatalog_ActivePrices_ReturnsTierIntervalAmountCurrencyAndMetered()
    {
        using BillingTestDatabaseFactory dbFactory = new();
        await SeedCatalogPrice(dbFactory, "price_pro_m", DomainBillingInterval.Monthly, Framlux.Billing.Api.Models.BillingTier.Pro, unitAmountCents: 300);
        await SeedCatalogPrice(dbFactory, "price_pro_a", DomainBillingInterval.Annual, Framlux.Billing.Api.Models.BillingTier.Pro, unitAmountCents: 3000);
        await SeedCatalogPrice(dbFactory, "price_team_m", DomainBillingInterval.Monthly, Framlux.Billing.Api.Models.BillingTier.Team, unitAmountCents: 500);
        IStripeGateway stripeGateway = Substitute.For<IStripeGateway>();
        BillingManagementService service = CreateService(dbFactory, stripeGateway);

        GetPublicCatalogResponse result = await service.GetPublicCatalog(
            new GetPublicCatalogRequest(), CreateCallContext());

        await Assert.That(result.Items).Count().IsEqualTo(3);

        CatalogPriceItem proMonthly = result.Items.Single(i =>
            (i.Tier == Framlux.Vord.BillingGrpc.BillingTier.Pro) &&
            (i.Interval == Framlux.Vord.BillingGrpc.BillingInterval.Monthly));
        await Assert.That(proMonthly.UnitAmountCents).IsEqualTo(300);
        await Assert.That(proMonthly.Currency).IsEqualTo("usd");
        await Assert.That(proMonthly.IsMetered).IsTrue();

        CatalogPriceItem proAnnual = result.Items.Single(i =>
            (i.Tier == Framlux.Vord.BillingGrpc.BillingTier.Pro) &&
            (i.Interval == Framlux.Vord.BillingGrpc.BillingInterval.Annual));
        await Assert.That(proAnnual.UnitAmountCents).IsEqualTo(3000);
    }

    [Test]
    public async Task GetPublicCatalog_InactivePrice_IsExcluded()
    {
        using BillingTestDatabaseFactory dbFactory = new();
        await SeedCatalogPrice(dbFactory, "price_pro_m", DomainBillingInterval.Monthly, Framlux.Billing.Api.Models.BillingTier.Pro, unitAmountCents: 300);
        await SeedCatalogPrice(dbFactory, "price_pro_old", DomainBillingInterval.Monthly, Framlux.Billing.Api.Models.BillingTier.Pro, unitAmountCents: 250, isActive: false);
        IStripeGateway stripeGateway = Substitute.For<IStripeGateway>();
        BillingManagementService service = CreateService(dbFactory, stripeGateway);

        GetPublicCatalogResponse result = await service.GetPublicCatalog(
            new GetPublicCatalogRequest(), CreateCallContext());

        await Assert.That(result.Items).Count().IsEqualTo(1);
        await Assert.That(result.Items[0].UnitAmountCents).IsEqualTo(300);
    }

    [Test]
    public async Task GetPublicCatalog_PriceWithoutTierMapping_IsExcluded()
    {
        using BillingTestDatabaseFactory dbFactory = new();

        // A price with no TierMapping row cannot be shown as a plan card; it must be skipped
        int productId = await dbFactory.Context.InsertWithInt32IdentityAsync(new CatalogProduct
        {
            StripeProductId = "prod_orphan",
            Name = "Orphan",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await dbFactory.Context.InsertWithInt32IdentityAsync(new CatalogPrice
        {
            StripePriceId = "price_orphan",
            ProductId = productId,
            Interval = DomainBillingInterval.Monthly,
            UnitAmountCents = 100,
            Currency = "usd",
            IsMetered = true,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        IStripeGateway stripeGateway = Substitute.For<IStripeGateway>();
        BillingManagementService service = CreateService(dbFactory, stripeGateway);

        GetPublicCatalogResponse result = await service.GetPublicCatalog(
            new GetPublicCatalogRequest(), CreateCallContext());

        await Assert.That(result.Items).Count().IsEqualTo(0);
    }

    [Test]
    public async Task GetPublicCatalog_EmptyCatalog_ReturnsEmptyItems()
    {
        using BillingTestDatabaseFactory dbFactory = new();
        IStripeGateway stripeGateway = Substitute.For<IStripeGateway>();
        BillingManagementService service = CreateService(dbFactory, stripeGateway);

        GetPublicCatalogResponse result = await service.GetPublicCatalog(
            new GetPublicCatalogRequest(), CreateCallContext());

        await Assert.That(result.Items).Count().IsEqualTo(0);
    }
```

  Run the filter — all four must fail to compile / fail until the override exists.
- [ ] **Step 2:** Implement at the end of `BillingManagementService` (after `ResumeSubscription`). Two small queries + in-memory join keeps the projection in explicit types (no `var` rule; the catalog is a handful of rows):

```csharp
    /// <summary>
    /// Returns the public pricing catalog: active prices with their tier and interval.
    /// Prices without a tier mapping are omitted because they cannot be presented as a plan.
    /// </summary>
    public override async Task<GetPublicCatalogResponse> GetPublicCatalog(
        GetPublicCatalogRequest request, ServerCallContext context)
    {
        List<CatalogPrice> activePrices = await _db.Prices
            .Where(p => p.IsActive == true)
            .ToListAsync(context.CancellationToken);

        List<TierMapping> tierMappings = await _db.TierMappings
            .ToListAsync(context.CancellationToken);

        Dictionary<int, DomainBillingTier> tierByPriceId = tierMappings
            .GroupBy(tm => tm.PriceId)
            .ToDictionary(g => g.Key, g => g.First().Tier);

        GetPublicCatalogResponse response = new();
        foreach (CatalogPrice price in activePrices.OrderBy(p => p.Id))
        {
            if (tierByPriceId.TryGetValue(price.Id, out DomainBillingTier tier) == false)
            {
                continue;
            }

            if (tier == DomainBillingTier.None)
            {
                continue;
            }

            response.Items.Add(new CatalogPriceItem
            {
                Tier = tier.ToProtoBillingTier(),
                Interval = price.Interval.ToProtoBillingInterval(),
                UnitAmountCents = price.UnitAmountCents,
                Currency = price.Currency,
                IsMetered = price.IsMetered,
            });
        }

        return response;
    }
```

- [ ] **Step 3:** Verify: `dotnet build vord-internal.slnx -c Release` (0 warnings), `dotnet run --project test/billing/billing.csproj` — all green.
- [ ] **Step 4:** Commit:

```bash
cd ~/Repositories/framlux/vord-internal
git add src/billing-api/Services/BillingManagementService.cs test/billing/Services/BillingManagementServiceTests.cs
git -c commit.gpgsign=false commit -m "Serve the public pricing catalog over the billing management gRPC service"
```

### Task 4: vord — client surface + SubscriptionDto.billingInterval (TDD)

**Files:**
- Create `vord/src/services.core/Services/Billing/CatalogItemResult.cs`
- Modify `vord/src/services.core/Services/Billing/StripeSubscriptionStatus.cs` (add `Interval` param)
- Modify `vord/src/services.core/Services/Billing/IBillingApiClient.cs` (add `GetPublicCatalogAsync`)
- Modify `vord/src/services.core/Services/Billing/BillingApiClient.cs` (map interval at line ~151; add `GetPublicCatalogAsync`)
- Modify `vord/src/services.core/Services/Billing/NoOpBillingApiClient.cs` (lines 33–36; add empty catalog)
- Create `vord/src/server/Endpoints/Web/Billing/BillingIntervalFormat.cs`
- Modify `vord/src/server/Endpoints/Web/Billing/SubscriptionEndpoint.cs` (DTO property; handler lines 111–136)
- Modify `vord/test/shared/FunctionalTestFactory.cs` (mock defaults, lines 499–522)
- Modify `vord/test/unit/services.core/Services/Billing/BillingApiClientTests.cs`, `NoOpBillingApiClientTests.cs`
- Modify `vord/test/functional/web/Endpoints/Web/BillingEndpointTests.cs` (lines 111, 339: record ctor)
- Create `vord/test/unit/server/Endpoints/Web/Billing/BillingIntervalFormatTests.cs`
- Modify `vord/test/functional/web/Endpoints/Web/SubscriptionEndpointTests.cs` (two new tests)

**Interfaces:**
- `public sealed record CatalogItemResult(BillingTier Tier, BillingInterval Interval, long UnitAmountCents, string Currency, bool IsMetered);` (proto enums)
- `Task<List<CatalogItemResult>> GetPublicCatalogAsync(CancellationToken ct);` on `IBillingApiClient`
- `StripeSubscriptionStatus(bool CancelAtPeriodEnd, string StripeStatus, string PriceId, int Quantity, DateTimeOffset? CurrentPeriodEnd, BillingTier Tier, BillingInterval Interval)`
- `internal static string? BillingIntervalFormat.ToWireString(BillingInterval interval)` → `"monthly"` / `"annual"` / `null`
- `SubscriptionDto.BillingInterval` (`string?`, JSON `billingInterval`, null when NONE or Free)

- [ ] **Step 1:** Create `src/services.core/Services/Billing/CatalogItemResult.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.Vord.BillingGrpc;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// A single public catalog price entry from the billing API.
/// </summary>
/// <param name="Tier">The billing tier this price applies to.</param>
/// <param name="Interval">The billing recurrence interval.</param>
/// <param name="UnitAmountCents">Per-machine price in cents.</param>
/// <param name="Currency">Three-letter ISO currency code.</param>
/// <param name="IsMetered">Whether the price is metered (billed on reported usage).</param>
public sealed record CatalogItemResult(
    BillingTier Tier,
    BillingInterval Interval,
    long UnitAmountCents,
    string Currency,
    bool IsMetered);
```

- [ ] **Step 2:** Extend `StripeSubscriptionStatus.cs` — add a final `Interval` parameter with its doc line:

```csharp
/// <param name="Interval">The billing interval of the live subscription's price, or None when unresolved.</param>
public sealed record StripeSubscriptionStatus(
    bool CancelAtPeriodEnd,
    string StripeStatus,
    string PriceId,
    int Quantity,
    DateTimeOffset? CurrentPeriodEnd,
    BillingTier Tier,
    BillingInterval Interval);
```

  Then fix every construction site (use LSP `findReferences` on `StripeSubscriptionStatus` to confirm the list is exactly these):
  - `BillingApiClient.GetSubscriptionStatusAsync` success path (line ~151): append `response.BillingInterval` as the last argument; catch path (line ~165) and `NoOpBillingApiClient` (line 35): append `BillingInterval.None`.
  - `test/shared/FunctionalTestFactory.cs` line 510 and `test/functional/web/Endpoints/Web/BillingEndpointTests.cs` lines 111 and 339: append `BillingInterval.None` (add `using Framlux.Vord.BillingGrpc;` if the file lacks it — FunctionalTestFactory and BillingEndpointTests already have it via `BillingTier.Unspecified`).
- [ ] **Step 3 (failing tests first — client):** Add to `IBillingApiClient.cs`:

```csharp
    /// <summary>
    /// Gets the public pricing catalog (active prices with tier and interval).
    /// Returns an empty list when the catalog is unavailable.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The catalog entries, or an empty list on error.</returns>
    Task<List<CatalogItemResult>> GetPublicCatalogAsync(CancellationToken ct);
```

  Add to `test/unit/services.core/Services/Billing/BillingApiClientTests.cs` (uses the file's existing `CreateSut`/`CreateAsyncCall`/`CreateFaultedCall` helpers):

```csharp
    // --- GetPublicCatalogAsync ---

    [Test]
    public async Task GetPublicCatalogAsync_MapsAllFields()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();
        GetPublicCatalogResponse response = new();
        response.Items.Add(new CatalogPriceItem
        {
            Tier = BillingTier.Pro,
            Interval = BillingInterval.Monthly,
            UnitAmountCents = 300,
            Currency = "usd",
            IsMetered = true,
        });
        response.Items.Add(new CatalogPriceItem
        {
            Tier = BillingTier.Team,
            Interval = BillingInterval.Annual,
            UnitAmountCents = 5000,
            Currency = "usd",
            IsMetered = false,
        });
        grpc.GetPublicCatalogAsync(
                Arg.Any<GetPublicCatalogRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(response));

        List<CatalogItemResult> result = await client.GetPublicCatalogAsync(CancellationToken.None);

        await Assert.That(result).Count().IsEqualTo(2);
        await Assert.That(result[0].Tier).IsEqualTo(BillingTier.Pro);
        await Assert.That(result[0].Interval).IsEqualTo(BillingInterval.Monthly);
        await Assert.That(result[0].UnitAmountCents).IsEqualTo(300);
        await Assert.That(result[0].Currency).IsEqualTo("usd");
        await Assert.That(result[0].IsMetered).IsTrue();
        await Assert.That(result[1].Interval).IsEqualTo(BillingInterval.Annual);
    }

    [Test]
    public async Task GetPublicCatalogAsync_GrpcError_ReturnsEmptyList()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();
        grpc.GetPublicCatalogAsync(
                Arg.Any<GetPublicCatalogRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<GetPublicCatalogResponse>(new RpcException(new Status(StatusCode.Unavailable, "down"))));

        List<CatalogItemResult> result = await client.GetPublicCatalogAsync(CancellationToken.None);

        await Assert.That(result).Count().IsEqualTo(0);
    }

    [Test]
    public async Task GetSubscriptionStatusAsync_MapsBillingInterval()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();
        grpc.GetSubscriptionStatusAsync(
                Arg.Any<GetSubscriptionStatusRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(new GetSubscriptionStatusResponse
            {
                StripeStatus = "active",
                Tier = BillingTier.Pro,
                BillingInterval = BillingInterval.Annual,
            }));

        StripeSubscriptionStatus result = await client.GetSubscriptionStatusAsync("tenant-ext-1", CancellationToken.None);

        await Assert.That(result.Interval).IsEqualTo(BillingInterval.Annual);
    }
```

  Add to `NoOpBillingApiClientTests.cs`:

```csharp
    [Test]
    public async Task GetSubscriptionStatusAsync_ReturnsNoneInterval()
    {
        StripeSubscriptionStatus status = await _client.GetSubscriptionStatusAsync("tenant-123", CancellationToken.None);

        await Assert.That(status.Interval).IsEqualTo(BillingInterval.None);
    }

    [Test]
    public async Task GetPublicCatalogAsync_ReturnsEmptyList()
    {
        List<CatalogItemResult> result = await _client.GetPublicCatalogAsync(CancellationToken.None);

        await Assert.That(result).Count().IsEqualTo(0);
    }
```

- [ ] **Step 4:** Implement. `BillingApiClient` — append after `ListInvoicesAsync`:

```csharp
    /// <inheritdoc/>
    public async Task<List<CatalogItemResult>> GetPublicCatalogAsync(CancellationToken ct)
    {
        try
        {
            GetPublicCatalogResponse response = await _grpcClient.GetPublicCatalogAsync(
                new GetPublicCatalogRequest(),
                deadline: DateTime.UtcNow.Add(GrpcDeadline),
                cancellationToken: ct);

            return response.Items.Select(i => new CatalogItemResult(
                i.Tier,
                i.Interval,
                i.UnitAmountCents,
                i.Currency,
                i.IsMetered)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting public billing catalog");
            return [];
        }
    }
```

  `NoOpBillingApiClient` — append:

```csharp
    /// <inheritdoc/>
    public Task<List<CatalogItemResult>> GetPublicCatalogAsync(CancellationToken ct)
    {
        return Task.FromResult<List<CatalogItemResult>>([]);
    }
```

  `FunctionalTestFactory.CreateDefaultBillingApiClientMock` — the `GetSubscriptionStatusAsync` default already gets `BillingInterval.None` from Step 2; add after the `ListInvoicesAsync` default (line ~521):

```csharp
        mock.GetPublicCatalogAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<List<CatalogItemResult>>([]));
```

- [ ] **Step 5 (failing tests first — DTO formatting):** Create `test/unit/server/Endpoints/Web/Billing/BillingIntervalFormatTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Endpoints.Web.Billing;
using Framlux.Vord.BillingGrpc;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Billing;

/// <summary>
/// Tests for <see cref="BillingIntervalFormat"/>.
/// </summary>
public sealed class BillingIntervalFormatTests
{
    [Test]
    public async Task ToWireString_Monthly_ReturnsMonthly()
    {
        await Assert.That(BillingIntervalFormat.ToWireString(BillingInterval.Monthly)).IsEqualTo("monthly");
    }

    [Test]
    public async Task ToWireString_Annual_ReturnsAnnual()
    {
        await Assert.That(BillingIntervalFormat.ToWireString(BillingInterval.Annual)).IsEqualTo("annual");
    }

    [Test]
    public async Task ToWireString_None_ReturnsNull()
    {
        await Assert.That(BillingIntervalFormat.ToWireString(BillingInterval.None)).IsNull();
    }

    [Test]
    public async Task ToWireString_UndefinedEnumValue_ReturnsNull()
    {
        await Assert.That(BillingIntervalFormat.ToWireString((BillingInterval)99)).IsNull();
    }
}
```

  (Match the namespace to the sibling `BillingEndpointGuardsTests.cs` in that folder if it differs.) Create `src/server/Endpoints/Web/Billing/BillingIntervalFormat.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.Vord.BillingGrpc;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// Maps the proto billing interval to the wire string used by billing DTOs.
/// </summary>
internal static class BillingIntervalFormat
{
    /// <summary>
    /// Returns "monthly" or "annual" for known intervals, or null when the interval is None or unknown.
    /// </summary>
    internal static string? ToWireString(BillingInterval interval)
    {
        return interval switch
        {
            BillingInterval.Monthly => "monthly",
            BillingInterval.Annual => "annual",
            _ => null,
        };
    }
}
```

- [ ] **Step 6:** Extend `SubscriptionEndpoint`. Add to `SubscriptionDto` (after `CancelAtPeriodEnd`, line 38):

```csharp
    /// <summary>Billing interval of the live subscription ("monthly" or "annual"), or null when not applicable.</summary>
    public string? BillingInterval { get; set; }
```

  In `HandleAsync`, replace the cancellation-state block (lines 111–121) and the DTO assignment:

```csharp
        // Retrieve cancellation state and billing interval from billing-api (source of truth for Stripe state)
        bool cancelAtPeriodEnd = false;
        string? billingInterval = null;
        if (subscription.Tier != SubscriptionTier.Free)
        {
            Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(tenantId, ct);
            if (tenant is not null)
            {
                StripeSubscriptionStatus stripeStatus = await _billingApiClient.GetSubscriptionStatusAsync(tenant.ExternalId, ct);
                cancelAtPeriodEnd = stripeStatus.CancelAtPeriodEnd;
                billingInterval = BillingIntervalFormat.ToWireString(stripeStatus.Interval);
            }
        }
```

  and add `BillingInterval = billingInterval,` to the `SubscriptionDto` initializer (after `CancelAtPeriodEnd = cancelAtPeriodEnd,`).
- [ ] **Step 7 (functional tests):** Add to `test/functional/web/Endpoints/Web/SubscriptionEndpointTests.cs` (add usings `Framlux.FleetManagement.Services.Core.Billing`, `Framlux.Vord.BillingGrpc`, and `NSubstitute`; reuse the file's `SeedTenantWithSubscription`, `BuildViewerClient`, `ExtractDataElement` helpers):

```csharp
    [Test]
    public async Task GetSubscription_ProTenantWithMonthlyPrice_ReturnsBillingIntervalMonthly()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Pro, null, 60);

        factory.BillingApiClientMock.GetSubscriptionStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StripeSubscriptionStatus(
                false, "active", "price_pro_123", 3, null, BillingTier.Pro, BillingInterval.Monthly)));

        HttpClient client = BuildViewerClient(factory, tenantId);
        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonElement data = await ExtractDataElement(response);
        await Assert.That(data.GetProperty("billingInterval").GetString()).IsEqualTo("monthly");
    }

    [Test]
    public async Task GetSubscription_FreeTier_BillingIntervalIsNull()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Free, 3, 1);

        HttpClient client = BuildViewerClient(factory, tenantId);
        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonElement data = await ExtractDataElement(response);
        await Assert.That(data.GetProperty("billingInterval").ValueKind).IsEqualTo(JsonValueKind.Null);
    }
```

  (If `SeedTenantWithSubscription`'s actual parameter list differs from `(db, tier, machineLimit, retentionDays)`, match the file's existing call sites at lines 26/48.)
- [ ] **Step 8:** Verify:

```bash
cd ~/Repositories/framlux/vord
dotnet build machine-info.slnx                                        # 0 warnings
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/web/functional.web.csproj
```

- [ ] **Step 9:** Commit:

```bash
cd ~/Repositories/framlux/vord
git add src/services.core/Services/Billing/CatalogItemResult.cs \
  src/services.core/Services/Billing/StripeSubscriptionStatus.cs \
  src/services.core/Services/Billing/IBillingApiClient.cs \
  src/services.core/Services/Billing/BillingApiClient.cs \
  src/services.core/Services/Billing/NoOpBillingApiClient.cs \
  src/server/Endpoints/Web/Billing/BillingIntervalFormat.cs \
  src/server/Endpoints/Web/Billing/SubscriptionEndpoint.cs \
  test/shared/FunctionalTestFactory.cs \
  test/unit/services.core/Services/Billing/BillingApiClientTests.cs \
  test/unit/services.core/Services/Billing/NoOpBillingApiClientTests.cs \
  test/unit/server/Endpoints/Web/Billing/BillingIntervalFormatTests.cs \
  test/functional/web/Endpoints/Web/BillingEndpointTests.cs \
  test/functional/web/Endpoints/Web/SubscriptionEndpointTests.cs
git -c commit.gpgsign=false commit -m "Expose authoritative billing interval on the subscription endpoint and add catalog client"
```

### Task 5: vord — /v1/api/billing/catalog endpoint (TDD, functional)

**Files:**
- Create `vord/src/server/Endpoints/Web/Billing/CatalogEndpoint.cs` (endpoint + co-located `CatalogItemDto`)
- Create `vord/test/functional/web/Endpoints/Web/BillingCatalogEndpointTests.cs`

**Interfaces:** GET `/api/v1/billing/catalog`, `Policies("ViewOnly")`, `Tags(EndpointTags.RequiresTenant)`, `Version(1)` — deliberately **no** Pro gate and **no** `BillingEndpointGuards` 404-when-disabled guard (Free tenants are the audience; billing-disabled installs get an empty list, mirroring `NoOpBillingApiClient`). Response `ApiResponse<List<CatalogItemDto>>` with `tier` (`"Pro"`/`"Team"`), `interval` (`"monthly"`/`"annual"`/null), `unitAmountCents`, `currency`, `isMetered`.

- [ ] **Step 1 (failing tests first):** Create `test/functional/web/Endpoints/Web/BillingCatalogEndpointTests.cs` (seed helpers follow `BillingDisplayEndpointTests.cs`):

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.Vord.BillingGrpc;
using LinqToDB;
using NSubstitute;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the billing catalog endpoint. The catalog is deliberately available
/// to Free-tier tenants (it powers the upgrade pricing cards) and returns an empty list
/// when billing is disabled.
/// </summary>
public sealed class BillingCatalogEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedTenantAndUser(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Free)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Catalog Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-catalog-{Guid.NewGuid():N}",
            Username = $"catalog-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        UserTenantRole role = new()
        {
            UserId = user.Id,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.Viewer,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        return (tenant.Id, user.Id);
    }

    private static HttpClient BuildViewerClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();
    }

    private static List<CatalogItemResult> SampleCatalog()
    {
        return
        [
            new CatalogItemResult(BillingTier.Pro, BillingInterval.Monthly, 300, "usd", true),
            new CatalogItemResult(BillingTier.Pro, BillingInterval.Annual, 3000, "usd", true),
            new CatalogItemResult(BillingTier.Team, BillingInterval.Monthly, 500, "usd", true),
        ];
    }

    [Test]
    public async Task Catalog_Unauthenticated_Returns401Or403()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/catalog");

        bool isUnauthorized = (response.StatusCode == HttpStatusCode.Unauthorized) ||
                              (response.StatusCode == HttpStatusCode.Forbidden);
        await Assert.That(isUnauthorized).IsTrue();
    }

    [Test]
    public async Task Catalog_NoTenantClaim_IsRejected()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/catalog");

        bool isRejected = (response.StatusCode == HttpStatusCode.Unauthorized) ||
                          (response.StatusCode == HttpStatusCode.Forbidden);
        await Assert.That(isRejected).IsTrue();
    }

    [Test]
    public async Task Catalog_FreeTierTenant_ReturnsMappedItems()
    {
        // Free tenants are deliberately allowed: the catalog powers the upgrade pricing cards
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db, SubscriptionTier.Free);
        factory.BillingApiClientMock.GetPublicCatalogAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SampleCatalog()));
        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/catalog");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetArrayLength()).IsEqualTo(3);
        await Assert.That(data[0].GetProperty("tier").GetString()).IsEqualTo("Pro");
        await Assert.That(data[0].GetProperty("interval").GetString()).IsEqualTo("monthly");
        await Assert.That(data[0].GetProperty("unitAmountCents").GetInt64()).IsEqualTo(300);
        await Assert.That(data[0].GetProperty("currency").GetString()).IsEqualTo("usd");
        await Assert.That(data[0].GetProperty("isMetered").GetBoolean()).IsTrue();
        await Assert.That(data[1].GetProperty("interval").GetString()).IsEqualTo("annual");
        await Assert.That(data[2].GetProperty("tier").GetString()).IsEqualTo("Team");
    }

    [Test]
    public async Task Catalog_BillingDisabled_ReturnsOkWithEmptyList()
    {
        // Billing-disabled installs must get an empty catalog (UI hides pricing), not a 404
        using BillingDisabledTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAndUser(db, SubscriptionTier.Free);
        HttpClient client = BuildViewerClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/catalog");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        await Assert.That(doc.RootElement.GetProperty("data").GetArrayLength()).IsEqualTo(0);
    }
}
```

  Run `dotnet run --project test/functional/web/functional.web.csproj -- --treenode-filter "/*/*/BillingCatalogEndpointTests/*"` — all four fail (route does not exist → 404).
- [ ] **Step 2:** Create `src/server/Endpoints/Web/Billing/CatalogEndpoint.cs` (pattern: `UpcomingInvoiceEndpoint.cs`):

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// A single public catalog price entry returned to the UI.
/// </summary>
public sealed class CatalogItemDto
{
    /// <summary>The subscription tier this price applies to (e.g. "Pro", "Team").</summary>
    public string Tier { get; set; } = string.Empty;

    /// <summary>The billing interval ("monthly" or "annual"), or null when unknown.</summary>
    public string? Interval { get; set; }

    /// <summary>Per-machine price in cents.</summary>
    public long UnitAmountCents { get; set; }

    /// <summary>Three-letter currency code.</summary>
    public string Currency { get; set; } = "usd";

    /// <summary>Whether the price is metered (billed on reported machine usage).</summary>
    public bool IsMetered { get; set; }
}

/// <summary>
/// Returns the public pricing catalog. Deliberately available to Free-tier tenants —
/// the catalog powers the upgrade pricing cards — and deliberately not gated on the
/// billing-enabled flag: disabled installs simply receive an empty catalog.
/// </summary>
public sealed class CatalogEndpoint : EndpointWithoutRequest<ApiResponse<List<CatalogItemDto>>>
{
    private readonly IBillingApiClient _billingApiClient;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="CatalogEndpoint"/> class.
    /// </summary>
    public CatalogEndpoint(IBillingApiClient billingApiClient, ITenantContext tenantContext)
    {
        _billingApiClient = billingApiClient;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/billing/catalog");
        Policies("ViewOnly");
        Tags(EndpointTags.RequiresTenant);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        _tenantContext.RequireTenantId();

        List<CatalogItemResult> items = await _billingApiClient.GetPublicCatalogAsync(ct);

        List<CatalogItemDto> dtos = items.Select(i => new CatalogItemDto
        {
            Tier = i.Tier.ToString(),
            Interval = BillingIntervalFormat.ToWireString(i.Interval),
            UnitAmountCents = i.UnitAmountCents,
            Currency = i.Currency,
            IsMetered = i.IsMetered,
        }).ToList();

        await Send.OkAsync(ApiResponse<List<CatalogItemDto>>.Ok(dtos), cancellation: ct);
    }
}
```

  (Verify the `ITenantContext` using/namespace against `SubscriptionEndpoint.cs` — it resolves via the same imports used there.)
- [ ] **Step 3:** Verify:

```bash
cd ~/Repositories/framlux/vord
dotnet build machine-info.slnx                                        # 0 warnings
dotnet run --project test/functional/web/functional.web.csproj        # all green incl. BillingCatalogEndpointTests
dotnet run --project test/unit/server/unit.server.csproj
```

- [ ] **Step 4:** Commit:

```bash
cd ~/Repositories/framlux/vord
git add src/server/Endpoints/Web/Billing/CatalogEndpoint.cs \
  test/functional/web/Endpoints/Web/BillingCatalogEndpointTests.cs
git -c commit.gpgsign=false commit -m "Add tenant-facing billing catalog endpoint for upgrade pricing"
```

### Task 6: Web — functional page states (heuristic removed, catalog-driven pricing cards)

**Files:**
- Modify `vord/src/web/src/lib/api/types.ts` (SubscriptionDto line ~333; new `CatalogItemDto`)
- Modify `vord/src/web/src/lib/api/client.ts` (new `getBillingCatalog`; `createCheckoutSession` line 750)
- Create `vord/src/web/src/lib/utils/billing-state.ts` and `billing-state.test.ts`
- Modify `vord/src/web/src/routes/(app)/settings/billing/+page.server.ts` (load lines 29–42; checkout action lines 61–65)
- Modify `vord/src/web/src/routes/(app)/settings/billing/+page.svelte` (script lines 35–66; Free upgrade section lines 425–462)

**Interfaces:** Consumes `/api/v1/billing/catalog` and `SubscriptionDto.billingInterval` from Tasks 4–5. Produces `deriveBillingPageState`, `billingIntervalLabel`, `findCatalogPrice`, `monthlyEquivalentCents` (Vitest-covered), `data.catalog` on the page, checkout action forwarding `interval`.

- [ ] **Step 1:** `types.ts` — add to `SubscriptionDto` after `cancelAtPeriodEnd: boolean;`:

```ts
	billingInterval: string | null;
```

  and add after the `LineItemDto` interface:

```ts
export interface CatalogItemDto {
	tier: string;
	interval: string | null;
	unitAmountCents: number;
	currency: string;
	isMetered: boolean;
}
```

- [ ] **Step 2:** `client.ts` — add next to `getUsageHistory` (line ~307):

```ts
	async getBillingCatalog(): Promise<CatalogItemDto[]> {
		const resp = await this.get<ApiResponse<CatalogItemDto[]>>('/api/v1/billing/catalog');
		return this.unwrap(resp);
	}
```

  (add `CatalogItemDto` to the types import at the top of the file) and change `createCheckoutSession` (line 750) to carry the interval — the billing-api `CheckoutEndpoint` already accepts `{ tier, interval }` and defaults to monthly:

```ts
	async createCheckoutSession(tier: string, interval: string = 'monthly'): Promise<{ checkoutUrl: string }> {
		return this.post<{ checkoutUrl: string }>('/api/v1/checkout', { tier, interval });
	}
```

- [ ] **Step 3 (failing tests first):** Create `src/web/src/lib/utils/billing-state.test.ts`:

```ts
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import {
	deriveBillingPageState,
	billingIntervalLabel,
	findCatalogPrice,
	monthlyEquivalentCents
} from './billing-state';
import type { SubscriptionDto, CatalogItemDto } from '$lib/api/types';

function makeSub(overrides: Partial<SubscriptionDto> = {}): SubscriptionDto {
	return {
		tier: 'Pro',
		status: 'Active',
		machineLimit: 1000,
		machineCount: 3,
		retentionDays: 30,
		currentPeriodEnd: null,
		cancelAtPeriodEnd: false,
		billingInterval: 'monthly',
		pendingAction: null,
		alertRuleLimit: 10,
		alertRuleCount: 0,
		webhookLimit: 3,
		webhookCount: 0,
		...overrides
	};
}

describe('deriveBillingPageState', () => {
	it('returns free when there is no subscription record', () => {
		expect(deriveBillingPageState(null)).toBe('free');
	});

	it('returns free for an active Free-tier subscription', () => {
		expect(deriveBillingPageState(makeSub({ tier: 'Free', billingInterval: null }))).toBe('free');
	});

	it('returns active for an active paid subscription', () => {
		expect(deriveBillingPageState(makeSub())).toBe('active');
	});

	it('returns pending-change when cancel-at-period-end is set', () => {
		expect(deriveBillingPageState(makeSub({ cancelAtPeriodEnd: true }))).toBe('pending-change');
	});

	it('returns past-due for a past-due subscription', () => {
		expect(deriveBillingPageState(makeSub({ status: 'PastDue' }))).toBe('past-due');
	});

	it('canceled wins over cancel-at-period-end and past-due', () => {
		expect(
			deriveBillingPageState(makeSub({ status: 'Canceled', cancelAtPeriodEnd: true }))
		).toBe('canceled');
	});

	it('pending-change wins over past-due', () => {
		expect(
			deriveBillingPageState(makeSub({ status: 'PastDue', cancelAtPeriodEnd: true }))
		).toBe('pending-change');
	});
});

describe('billingIntervalLabel', () => {
	it('maps monthly to Monthly', () => {
		expect(billingIntervalLabel('monthly')).toBe('Monthly');
	});

	it('maps annual to Annual', () => {
		expect(billingIntervalLabel('annual')).toBe('Annual');
	});

	it('returns null for null', () => {
		expect(billingIntervalLabel(null)).toBeNull();
	});

	it('returns null for unknown values', () => {
		expect(billingIntervalLabel('weekly')).toBeNull();
	});
});

const catalog: CatalogItemDto[] = [
	{ tier: 'Pro', interval: 'monthly', unitAmountCents: 300, currency: 'usd', isMetered: true },
	{ tier: 'Pro', interval: 'annual', unitAmountCents: 3000, currency: 'usd', isMetered: true },
	{ tier: 'Team', interval: 'monthly', unitAmountCents: 500, currency: 'usd', isMetered: true }
];

describe('findCatalogPrice', () => {
	it('finds a matching tier and interval', () => {
		expect(findCatalogPrice(catalog, 'Pro', 'annual')?.unitAmountCents).toBe(3000);
	});

	it('returns null when there is no match', () => {
		expect(findCatalogPrice(catalog, 'Team', 'annual')).toBeNull();
	});

	it('returns null for an empty catalog', () => {
		expect(findCatalogPrice([], 'Pro', 'monthly')).toBeNull();
	});
});

describe('monthlyEquivalentCents', () => {
	it('returns the unit amount for monthly prices', () => {
		expect(monthlyEquivalentCents(catalog[0])).toBe(300);
	});

	it('divides annual prices by twelve, rounded', () => {
		expect(monthlyEquivalentCents(catalog[1])).toBe(250);
	});
});
```

  `pnpm -C src/web test` — fails (module missing). Then create `src/web/src/lib/utils/billing-state.ts`:

```ts
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { SubscriptionDto, CatalogItemDto } from '$lib/api/types';

export type BillingPageState = 'free' | 'active' | 'pending-change' | 'past-due' | 'canceled';

/**
 * Derives the top-level billing page state from the subscription DTO.
 * Precedence: canceled > pending-change > past-due > free > active.
 */
export function deriveBillingPageState(sub: SubscriptionDto | null): BillingPageState {
	if (sub === null) return 'free';
	if (sub.status === 'Canceled') return 'canceled';
	if (sub.cancelAtPeriodEnd) return 'pending-change';
	if (sub.status === 'PastDue') return 'past-due';
	if (sub.tier === 'Free') return 'free';

	return 'active';
}

/** Maps the wire interval ("monthly"/"annual") to its display label, or null when absent. */
export function billingIntervalLabel(interval: string | null): string | null {
	if (interval === 'monthly') return 'Monthly';
	if (interval === 'annual') return 'Annual';

	return null;
}

/** Finds the catalog entry for a tier + interval, or null when the catalog has no match. */
export function findCatalogPrice(
	catalog: CatalogItemDto[],
	tier: string,
	interval: string
): CatalogItemDto | null {
	return catalog.find((i) => i.tier === tier && i.interval === interval) ?? null;
}

/** Per-machine monthly-equivalent price in cents (annual prices divided by twelve). */
export function monthlyEquivalentCents(item: CatalogItemDto): number {
	if (item.interval === 'annual') {
		return Math.round(item.unitAmountCents / 12);
	}

	return item.unitAmountCents;
}
```

  `pnpm -C src/web test` — green.
- [ ] **Step 4:** `+page.server.ts` — fetch the catalog in the load (replace lines 31–42):

```ts
		// Fetch billing data in parallel
		const [upcomingInvoice, invoices, usageHistory, catalog] = await Promise.all([
			api.getUpcomingInvoice().catch(() => null),
			api.getInvoices().catch(() => []),
			api.getUsageHistory(6).catch(() => []),
			api.getBillingCatalog().catch(() => [])
		]);

		return {
			upcomingInvoice,
			invoices,
			usageHistory,
			catalog,
			billingEnabled: !!env.PUBLIC_BILLING_URL
		};
```

  and forward the interval in the checkout action (lines 61–65):

```ts
			const formData = await request.formData();
			const tier = (formData.get('tier') as string) || 'pro';
			const interval = (formData.get('interval') as string) || 'monthly';

			try {
				const data = await billingApi.createCheckoutSession(tier, interval);
```

- [ ] **Step 5:** `+page.svelte` script — delete the heuristic `billingInterval` `$derived.by` block (lines 35–54) and replace with the authoritative DTO value plus catalog state (imports go next to the existing `types` import):

```ts
	import type { SubscriptionDto, UpcomingInvoiceDto, InvoiceDto, UsagePointDto, CatalogItemDto } from '$lib/api/types';
	import { billingIntervalLabel, findCatalogPrice, monthlyEquivalentCents } from '$lib/utils/billing-state';

	// Authoritative billing interval from the subscription DTO (billing-api derives it
	// from the live subscription's price) — replaces the old period-length heuristic.
	const billingInterval: string | null = $derived(
		billingIntervalLabel(subscription?.billingInterval ?? null)
	);

	const catalog: CatalogItemDto[] = $derived(data.catalog ?? []);
	const catalogHasPrices = $derived(
		findCatalogPrice(catalog, 'Pro', 'monthly') !== null ||
		findCatalogPrice(catalog, 'Team', 'monthly') !== null
	);
	let selectedInterval: 'monthly' | 'annual' = $state('monthly');
	const upgradeTiers = ['Pro', 'Team'] as const;

	const tierTaglines: Record<string, string> = {
		Pro: 'Unlimited machines, 30-day retention, default alert rules.',
		Team: 'Everything in Pro plus custom alert rules, audit log, and SSO.'
	};
```

  The existing "Billing Interval" display block (lines 377–384) keeps working unchanged — it reads the new `billingInterval` value.
- [ ] **Step 6:** `+page.svelte` markup — replace the Free-tier upgrade block (the `{#if isFree}` branch's "Upgrade Your Plan" flex at lines 427–462, keeping the Cancel Account section below it) with catalog-driven pricing cards + monthly/annual toggle, falling back to the previous static buttons when the catalog is empty:

```svelte
				{#if isFree}
					<!-- Free tier: upgrade funnel + cancel -->
					<div class="space-y-6">
						{#if catalogHasPrices}
							<div class="flex flex-wrap items-center justify-between gap-4">
								<div>
									<h3 class="text-lg font-semibold text-surface-900 dark:text-surface-50">
										Upgrade Your Plan
									</h3>
									<p class="mt-1 text-sm text-surface-500 dark:text-surface-400">
										Unlock unlimited machines, extended data retention, alerting, and more.
									</p>
								</div>
								<div class="inline-flex rounded-lg border border-surface-300 p-0.5 dark:border-surface-600" role="group" aria-label="Billing interval">
									<button
										type="button"
										onclick={() => selectedInterval = 'monthly'}
										class="rounded-md px-3 py-1 text-sm font-medium transition-colors {selectedInterval === 'monthly' ? 'bg-primary-500 text-white' : 'text-surface-600 hover:text-surface-900 dark:text-surface-300 dark:hover:text-surface-100'}"
									>
										Monthly
									</button>
									<button
										type="button"
										onclick={() => selectedInterval = 'annual'}
										class="rounded-md px-3 py-1 text-sm font-medium transition-colors {selectedInterval === 'annual' ? 'bg-primary-500 text-white' : 'text-surface-600 hover:text-surface-900 dark:text-surface-300 dark:hover:text-surface-100'}"
									>
										Annual
									</button>
								</div>
							</div>
							<div class="grid gap-4 sm:grid-cols-2">
								{#each upgradeTiers as tierName}
									{@const item = findCatalogPrice(catalog, tierName, selectedInterval)}
									{#if item !== null}
										<div class="flex flex-col rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
											<span class="inline-flex w-fit items-center rounded-full px-2.5 py-0.5 text-xs font-medium {getTierBadgeClasses(tierName)}">
												{tierName}
											</span>
											<p class="mt-3 text-3xl font-bold text-surface-900 dark:text-surface-50">
												{formatCents(monthlyEquivalentCents(item), item.currency)}<span class="text-sm font-normal text-surface-500">/host/mo</span>
											</p>
											{#if selectedInterval === 'annual'}
												<p class="mt-1 text-xs text-surface-500 dark:text-surface-400">
													Billed annually at {formatCents(item.unitAmountCents, item.currency)}/host/yr
												</p>
											{/if}
											<p class="mt-3 flex-1 text-sm text-surface-500 dark:text-surface-400">
												{tierTaglines[tierName]}
											</p>
											<form method="POST" action="?/checkout" class="mt-4">
												<input type="hidden" name="tier" value={tierName.toLowerCase()} />
												<input type="hidden" name="interval" value={selectedInterval} />
												<button
													type="submit"
													class="inline-flex w-full items-center justify-center gap-2 rounded-lg px-5 py-2.5 text-sm font-medium text-white transition-colors {tierName === 'Pro' ? 'bg-blue-600 hover:bg-blue-700 dark:bg-blue-500 dark:hover:bg-blue-600' : 'bg-purple-600 hover:bg-purple-700 dark:bg-purple-500 dark:hover:bg-purple-600'}"
												>
													<CircleArrowUp class="h-4 w-4" />
													Upgrade to {tierName}
												</button>
											</form>
										</div>
									{/if}
								{/each}
							</div>
						{:else}
							<!-- Catalog unavailable: keep the plain upgrade CTAs (billing-web resolves the price) -->
							<div class="flex items-start gap-4">
								<div class="rounded-lg bg-blue-100 p-3 dark:bg-blue-900/30">
									<CircleArrowUp class="h-6 w-6 text-blue-600 dark:text-blue-400" />
								</div>
								<div class="flex-1">
									<h3 class="text-lg font-semibold text-surface-900 dark:text-surface-50">
										Upgrade Your Plan
									</h3>
									<p class="mt-1 text-sm text-surface-500 dark:text-surface-400">
										Unlock unlimited machines, extended data retention, alerting, and more.
									</p>
									<div class="mt-4 flex flex-wrap gap-3">
										<form method="POST" action="?/checkout">
											<input type="hidden" name="tier" value="pro" />
											<button type="submit" class="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-blue-700 dark:bg-blue-500 dark:hover:bg-blue-600">
												<CircleArrowUp class="h-4 w-4" />
												Upgrade to Pro
											</button>
										</form>
										<form method="POST" action="?/checkout">
											<input type="hidden" name="tier" value="team" />
											<button type="submit" class="inline-flex items-center gap-2 rounded-lg bg-purple-600 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-purple-700 dark:bg-purple-500 dark:hover:bg-purple-600">
												<CircleArrowUp class="h-4 w-4" />
												Upgrade to Team
											</button>
										</form>
									</div>
								</div>
							</div>
						{/if}
```

  Keep the existing `<!-- Cancel Account (Free tier: immediate) -->` block after it, unchanged, inside the same `space-y-6` div. The past-due banner (lines 218–240), pending-action banner (243–276), and canceled banner (279–325) already satisfy the spec's other three states — leave them; the canceled and past-due states show no blank invoice sections because the Current Period / Invoice History cards are already conditional on `upcomingInvoice?.hasInvoice` / `invoices.length > 0`.
- [ ] **Step 7:** Verify web:

```bash
cd ~/Repositories/framlux/vord
pnpm -C src/web check     # 0 errors, 0 warnings
pnpm -C src/web test      # all Vitest suites green
```

- [ ] **Step 8:** Commit:

```bash
cd ~/Repositories/framlux/vord
git add src/web/src/lib/api/types.ts src/web/src/lib/api/client.ts \
  src/web/src/lib/utils/billing-state.ts src/web/src/lib/utils/billing-state.test.ts \
  "src/web/src/routes/(app)/settings/billing/+page.server.ts" \
  "src/web/src/routes/(app)/settings/billing/+page.svelte"
git -c commit.gpgsign=false commit -m "Drive billing page from authoritative interval and catalog pricing cards"
```

### Task 7: Web — visual refresh of the billing page

**Files:**
- Modify `vord/src/web/src/routes/(app)/settings/billing/+page.svelte` only

**Interfaces:** No data-shape changes. Restructure within the existing Skeleton/Tailwind design language (no rebrand). Svelte 5 runes only; dark mode stays on the app's `dark:` utility classes (backed by the global `:where(.dark, .dark *)` variant).

- [ ] **Step 1 — Plan summary card:** Rework the "Current Plan" card header (lines 331–334) into a plan summary that leads with the plan identity and the next charge. Replace the header block and, in the paid branch, add the invoice figure to the right of the header:

```svelte
	<!-- Plan Summary -->
	<div class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
		<div class="mb-6 flex flex-wrap items-start justify-between gap-4">
			<div class="flex items-center gap-3">
				<CreditCard class="h-5 w-5 text-surface-400 dark:text-surface-500" />
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Your Plan</h2>
			</div>
			{#if upcomingInvoice?.hasInvoice}
				<div class="text-right">
					<p class="text-xs text-surface-500 dark:text-surface-400">Next invoice</p>
					<p class="text-2xl font-bold text-surface-900 dark:text-surface-50">
						{formatCents(upcomingInvoice.amountDueCents, upcomingInvoice.currency)}
					</p>
					{#if upcomingInvoice.nextPaymentAttempt}
						<p class="text-xs text-surface-500 dark:text-surface-400">
							on {formatShortDate(upcomingInvoice.nextPaymentAttempt)}
						</p>
					{/if}
				</div>
			{/if}
		</div>
```

  The tier/status/interval/retention/period-end facts row and the machine-usage progress bar stay as-is inside this card. In the `subscription === null` Free branch, extend the facts row with the Free limits already shown (machines 0/3, retention 1 day) — no change needed beyond the header swap.
- [ ] **Step 2 — Cost breakdown card:** Replace the "Current Period" card body (lines 709–728, the `flex flex-wrap items-baseline` block) with labeled rows so per-machine cost and run-rate read as a breakdown instead of a string of inline spans:

```svelte
		<div class="mt-4 grid gap-4 sm:grid-cols-3">
			<div>
				<p class="text-xs text-surface-500 dark:text-surface-400">Amount due this period</p>
				<p class="mt-1 text-2xl font-bold text-surface-900 dark:text-surface-50">
					{formatCents(upcomingInvoice.amountDueCents, upcomingInvoice.currency)}
				</p>
			</div>
			{#if upcomingInvoice.unitAmountCents > 0}
				<div>
					<p class="text-xs text-surface-500 dark:text-surface-400">Per machine</p>
					<p class="mt-1 text-2xl font-bold text-surface-900 dark:text-surface-50">
						{formatCents(upcomingInvoice.unitAmountCents, upcomingInvoice.currency)}<span class="text-sm font-normal text-surface-500">/mo</span>
					</p>
				</div>
			{/if}
			{#if projectedCostCents !== null}
				<div>
					<p class="text-xs text-surface-500 dark:text-surface-400">
						Current run rate ({subscription?.machineCount ?? 0} {subscription?.machineCount === 1 ? 'machine' : 'machines'})
					</p>
					<p class="mt-1 text-2xl font-bold text-surface-900 dark:text-surface-50">
						{formatCents(projectedCostCents, upcomingInvoice.currency)}<span class="text-sm font-normal text-surface-500">/mo</span>
					</p>
				</div>
			{/if}
		</div>
		{#if upcomingInvoice.nextPaymentAttempt}
			<p class="mt-3 text-sm text-surface-500 dark:text-surface-400">
				Next charge: {formatShortDate(upcomingInvoice.nextPaymentAttempt)}
			</p>
		{/if}
```

  The discount chip, breakdown toggle, and line-item list below it stay unchanged.
- [ ] **Step 3 — Catalog-driven prices in calculator and comparison:** Replace the hardcoded `$3`/`$5` math in the Cost Calculator (lines 957–970) and the Plan Comparison price row (lines 924–929) with catalog values, falling back to the historical defaults when the catalog is empty. Script additions:

```ts
	// Catalog-backed monthly per-machine prices with legacy fallbacks for installs
	// that have not synced a catalog yet.
	const proMonthlyCents = $derived(findCatalogPrice(catalog, 'Pro', 'monthly')?.unitAmountCents ?? 300);
	const teamMonthlyCents = $derived(findCatalogPrice(catalog, 'Team', 'monthly')?.unitAmountCents ?? 500);
```

  Calculator output cells become:

```svelte
					<div>
						<p class="text-sm text-surface-500 dark:text-surface-400">Pro</p>
						<p class="text-2xl font-bold text-blue-600 dark:text-blue-400">
							{formatCents(machineCount * proMonthlyCents)}<span class="text-sm font-normal">/mo</span>
						</p>
					</div>
					<div>
						<p class="text-sm text-surface-500 dark:text-surface-400">Team</p>
						<p class="text-2xl font-bold text-purple-600 dark:text-purple-400">
							{formatCents(machineCount * teamMonthlyCents)}<span class="text-sm font-normal">/mo</span>
						</p>
					</div>
```

  Comparison price row cells: `$0`, `{formatCents(proMonthlyCents)}/host/mo`, `{formatCents(teamMonthlyCents)}/host/mo`.
- [ ] **Step 4:** Also update the hardcoded `— $3/host/mo` / `— $5/host/mo` suffixes if any remain in the catalog-empty fallback CTAs from Task 6 (they were intentionally dropped there; verify none remain: `grep -n '\$3\|\$5' "src/web/src/routes/(app)/settings/billing/+page.svelte"` should only match nothing or the calculator fallbacks via cents constants).
- [ ] **Step 5:** Verify:

```bash
cd ~/Repositories/framlux/vord
pnpm -C src/web check     # 0 errors
pnpm -C src/web test
pnpm -C src/web build     # production build succeeds
```

- [ ] **Step 6:** Commit:

```bash
cd ~/Repositories/framlux/vord
git add "src/web/src/routes/(app)/settings/billing/+page.svelte"
git -c commit.gpgsign=false commit -m "Refresh billing page layout: plan summary, cost breakdown, catalog-driven pricing"
```

### Task 8: Full verification, both repos

- [ ] **Step 1:** vord-internal:

```bash
cd ~/Repositories/framlux/vord-internal
dotnet build vord-internal.slnx -c Release          # 0 errors, 0 warnings
dotnet run --project test/billing/billing.csproj    # 0 failed
git status --short                                   # clean (all task commits made)
```

- [ ] **Step 2:** vord — all six TUnit projects plus web:

```bash
cd ~/Repositories/framlux/vord
dotnet build machine-info.slnx                       # 0 errors, 0 warnings
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/database/unit.database.csproj
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
dotnet run --project test/functional/hangfire/functional.hangfire.csproj
pnpm -C src/web check && pnpm -C src/web test && pnpm -C src/web build
```

  (Integration tests under `test/integration/` need Docker/Podman; run them too if a container runtime is available — nothing in this plan touches migrations, so they are not on the critical path.)
- [ ] **Step 3:** Confirm hygiene: `git -C ~/Repositories/framlux/vord status --short` shows `nuget.config` modified-but-unstaged and `docs/` untracked — neither was ever committed.
- [ ] **Step 4:** Completion summary must state: (a) `Framlux.Vord.BillingGrpc` **1.15.0** was packed locally to `/tmp/billinggrpc-local` and Jonathan publishes only 1.15.0 to the real feed (1.14.0 never ships); (b) vord's `nuget.config` local source is still uncommitted and must be stripped when the real package is published; (c) any deviations from this plan.

## Exit criteria

1. `BillingService.proto` defines `BillingInterval` (NONE=0/MONTHLY=1/ANNUAL=2), `GetSubscriptionStatusResponse.billing_interval = 7`, and `GetPublicCatalog` on `BillingManagement`; package version 1.15.0; vord pins 1.15.0.
2. billing-api derives `billing_interval` from the live subscription's price via CatalogPrice (unknown/missing → NONE) — no read of `StripeCustomers.BillingInterval` — and serves active-price catalog items with tier/interval/amount/currency/metered.
3. vord: `SubscriptionDto.billingInterval` is `"monthly"`/`"annual"`/null; `/api/v1/billing/catalog` requires a tenant, allows Free, has no Pro gate, and returns an empty list on billing-disabled installs.
4. Web: the period-length heuristic is gone; the four page states (Active / pending-change / past-due / Free-with-pricing-cards) render per the spec; checkout carries the selected interval; Vitest covers the state derivation; page visually restructured within the existing design language.
5. Both repos: builds 0 errors/0 warnings; vord-internal `test/billing` green; vord's six TUnit projects and `pnpm check`/`test`/`build` green; vord's `nuget.config` and `docs/` never staged.
