# Billing Visibility in the Fleet UI — Design

**Date:** 2026-07-20 · **Status:** Approved (Jonathan, 2026-07-20)

## Goal

Tenant admins see their plan (tier + billing interval), cost per machine, and estimated bill on
the Fleet UI billing page (`/settings/billing`), with correct rendering in every subscription
state and a visual refresh of the page. Free-tier tenants see the upgrade funnel: per-tier
pricing cards with a checkout CTA.

## Context and discoveries

- The page already renders most of this: interval (inferred heuristically from upcoming-invoice
  period length), per-machine cost (`unitAmountCents`), estimated bill (Stripe `amountDue` and a
  client-side `unitAmount × machineCount`), discounts, usage history.
- The heuristic interval breaks whenever there is no upcoming invoice (canceled, Free) and is
  derived, not authoritative. This design replaces it.
- `GetUpcomingInvoice` and `GetSubscriptionStatus` RPCs already exist; the contract gap is the
  interval and a public catalog.
- Stripe meter aggregation is **Last** (Jonathan) — pricing display can treat the reported
  machine count as the billable quantity.

## Decisions (Jonathan, 2026-07-20)

- Full review + polish of the billing page: functional completeness **and** visual refresh.
- Free tier sees a first-class plan card plus Pro/Team pricing cards (monthly/annual toggle)
  and checkout CTA — the page is the upgrade funnel.
- Data flow: extend the gRPC pull path (no push, no direct UI→billing-api reads).
- Customer-facing interval-change history is **excluded** — internal analytics only, served by
  the `interval_changed` audit events added in the Stripe-WIP plan.

## Design

### 1. Contract — `BillingService.proto`, version 1.15.0

- New `enum BillingInterval { BILLING_INTERVAL_NONE = 0; BILLING_INTERVAL_MONTHLY = 1;
  BILLING_INTERVAL_ANNUAL = 2; }` (prefix style matches the file's existing enums).
- `GetSubscriptionStatusResponse` gains `BillingInterval billing_interval = 7;`.
- New RPC on `BillingManagement`: `GetPublicCatalog(GetPublicCatalogRequest) returns
  (GetPublicCatalogResponse)` — response is `repeated CatalogPriceItem { BillingTier tier;
  BillingInterval interval; int64 unit_amount_cents; string currency; bool is_metered; }`,
  active prices only.
- Version stacks on the unpublished 1.14.0 → **1.15.0**; Jonathan publishes once (1.14.0 never
  ships to the feed; vord's pin jumps 1.14.0 → 1.15.0). Local pack handoff reuses
  `/tmp/billinggrpc-local` and the existing `billinggrpc-local` source in vord's nuget.config.

### 2. billing-api (vord-internal)

- `GetSubscriptionStatus`: derive `billing_interval` from the live subscription's price
  (CatalogPrice lookup by the subscription's current price id; unknown/missing price →
  `NONE`). No dependence on the stored `StripeCustomers.BillingInterval` column.
- `GetPublicCatalog`: read active `Prices` rows joined to products/tiers.
- Out of scope here (lives in the Stripe-WIP plan): keeping `StripeCustomers.BillingInterval`
  current on subscription.updated and the `interval_changed` audit event.

### 3. Fleet server (vord)

- `SubscriptionEndpoint` DTO gains `billingInterval` (serialized string: `"monthly"`,
  `"annual"`, absent/null when `NONE`).
- New tenant-authed catalog endpoint `/v1/api/billing/catalog` (`RequiresTenant`; deliberately
  NO Pro gate — Free tenants are the audience) proxying `GetPublicCatalog`.
- `IBillingApiClient`/`BillingApiClient` gain `GetPublicCatalogAsync`; `NoOpBillingApiClient`
  returns an empty catalog (billing-disabled installs hide pricing UI).

### 4. Web UI — functional states

Heuristic interval removed; interval comes from the subscription DTO.

| State | Renders |
|---|---|
| Active | Plan card (tier + interval), per-machine cost, estimated bill (Stripe `amountDue` as "next invoice"; `unitAmount × machineCount` as current run-rate), usage history, manage/plan-change affordances |
| Cancel-at-period-end / canceled | Banner + reactivate; no blank invoice sections |
| Past-due | Payment warning + portal link |
| Free | Free plan card with limits + Pro/Team pricing cards from the catalog, monthly/annual toggle, checkout CTA |

### 5. Web UI — visual refresh

Restructured within the existing Skeleton/Tailwind design language (no rebrand): plan-summary
card, cost-breakdown card, usage chart, pricing-cards grid. Dark mode via the app's
`:where(.dark, .dark *)` convention. Svelte 5 runes (`$derived`, `$state`).

### 6. Testing

- billing-api: unit + integration for interval derivation (incl. missing/unknown-price → NONE)
  and the catalog RPC.
- vord: unit + functional for the DTO extension and the catalog endpoint (tenant required,
  Free allowed, billing-disabled → empty).
- web: `pnpm check` green; Vitest for state-derivation logic; page-state assertions updated.

### 7. Sequencing

The Stripe-WIP commit-readiness plan (plan A) lands first — same webhook handler, and it owns
the interval column + audit. This plan (B) follows, on contract 1.15.0.
