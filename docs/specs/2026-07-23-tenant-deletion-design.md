# Tenant Deletion — Design

**Date:** 2026-07-23
**Status:** Approved (pending spec review)
**Goal:** Give an operator a tool in the internal admin panel to honor a customer's account-deletion request within the privacy policy's 30-day window — erasing personal data and reclaiming storage while keeping a valid audit trail.

## Motivation

The privacy policy (as of 2026-07-22) promises: "you may request account deletion by contacting support; deletion requests are completed within 30 days." No tenant/account-deletion path exists in either repo today. This design adds one, driven from the internal admin surface, that reconciles two goals that pull in opposite directions:

- **Erasure** — actually remove the customer's personal data (the deletion promise; privacy-law right-to-erasure).
- **Audit record-keeping** — keep the audit log referentially valid and human-readable after the account is gone.

The reconciliation is **erase the personal data and bulk operational data, but retain the identity skeleton (masked) so audit FKs stay valid.**

## Core decisions (owner-ratified)

1. **Two-phase: deactivate now, hard-purge after a fixed 30-day grace.** The operator's action deactivates the tenant immediately (ingest stops, login blocked, billing canceled). A scheduled job performs the irreversible purge exactly 30 days later. No per-deletion override — the grace is always 30 days.
2. **Restore is available for the full 30 days.** Because deactivation is non-destructive, an operator can cancel a pending deletion any time before purge (an operator-error escape hatch, not a billing rewind).
3. **Keep identity rows, erase everything else.** At purge: keep the `Tenants` row (disabled, org name intact — an org name is not personal data), keep `UserAccounts` rows but **mask the personal fields** of any user orphaned by this deletion, keep the `AuditLog`. Purge all bulk operational data (telemetry, machines, alerts, integrations, etc.). This makes the identity data "unusable for any purpose except the audit linkage" — the row's numeric id keeps every audit FK valid, but the person is no longer identifiable even in the raw database.
4. **Users are global; only this tenant's membership is removed.** A user still active in another fleet keeps their account and PII untouched — only their `UserTenantRoles` rows for *this* tenant are deleted. A user left with zero active memberships anywhere gets their PII masked.
5. **Billing torn down.** Subscription canceled immediately at deactivation (stop charging someone who asked to be deleted); the Stripe customer object and `StripeCustomers` row are deleted at purge.
6. **Home: the internal admin SPA.** Operator-only, never tenant self-serve. Driven through the existing `FleetAdminService` gRPC + billing-api proxy the admin SPA already uses.

## Architecture

Three layers, spanning both repos:

```
vord-internal/src/admin (SvelteKit SPA)
  fleet/tenants/[id]   — tenant detail + "Delete tenant" action
  fleet/deletions      — pending/completed deletions + "Restore"
        │  REST /api/v1/admin/fleet/*
        ▼
vord-internal/src/billing-api (proxy endpoints → FleetAdminClient gRPC)
        │  gRPC
        ▼
vord/src/server FleetAdminService  — new RPCs: RequestTenantDeletion, RestoreTenant, ListTenantDeletions
        │
        ├─ Phase 1 (deactivation, synchronous): TenantDeletionHandler
        │     → BillingApiClient.CancelSubscription (immediate)  ──► billing-api (new immediate-cancel path)
        │
        └─ Phase 2 (purge, Hangfire recurring job): TenantPurgeJob
              → ordered teardown across the fleet DB
              → BillingApiClient.DeleteCustomer (new)  ──────────► billing-api (new delete-customer path)
```

### Unit responsibilities

- **`TenantDeletions` table + repository** (fleet DB, `database` project). The single source of truth for the deletion lifecycle. One row per deletion. Serves triple duty: work queue for the purge job, permanent tombstone that survives the purge, and the admin SPA's data source. Never itself purged.
- **`TenantDeletionHandler`** (`services.core`). Phase-1 logic: validate, insert the deletion row, deactivate the tenant, trigger immediate billing cancel. Also `RestoreTenant`. Extracted `internal static` decision logic (e.g. "is this user orphaned by removing tenant X") for direct unit testing.
- **`TenantPurgeJob`** (`services.core`, Hangfire recurring). Phase-2 logic: find due deletions, run the idempotent ordered teardown, mask orphaned users, trigger billing customer-delete, mark `Purged`.
- **`FleetAdminService` RPCs** (`server`, gRPC): `RequestTenantDeletion`, `RestoreTenant`, `ListTenantDeletions` (plus the existing `GetTenantDetail` backs the new detail page).
- **billing-api additions** (vord-internal): an immediate subscription-cancel path (today's cancel is period-end only) and an operator delete-customer path (`IStripeGateway.DeleteCustomerAsync` exists but is unwired), each with a `BillingManagementService` RPC and `BillingApiClient` method.
- **Admin SPA pages** (vord-internal): `fleet/tenants/[id]` detail with the delete action; `fleet/deletions` list with restore.

## Data model: `TenantDeletions`

Added in-place to `InitialMigration.cs` (pre-GA migration-freeze rule permits it; consistent with the DataProtectionKeys and retention-partitioning tables added the same way). Not partitioned. Columns:

| Column | Type | Notes |
|---|---|---|
| `Id` | int identity PK | |
| `TenantId` | int, FK → Tenants | the row is kept even after purge (Tenants row is kept) |
| `TenantExternalId` | varchar | denormalized so the record is self-describing |
| `TenantName` | varchar | org name (not personal data) — for the operator's record |
| `RequestedByUserId` | int | operator who triggered it |
| `RequestedAt` | timestamptz | |
| `ScheduledPurgeAt` | timestamptz | `RequestedAt + 30 days` |
| `Status` | smallint enum | `Deactivated` / `Purged` / `Restored` |
| `PurgedAt` | timestamptz nullable | set when Phase 2 completes |
| `Reason` | text nullable | free-text reason captured by the operator |

The row holds **no personal PII**, so it persists forever as the "tenant #42 (Acme) deleted on date X by operator Y" record.

## Phase 1 — Deactivation (synchronous, on operator action)

In one fleet-DB transaction:
1. Reject if the tenant already has a non-`Restored` deletion row (idempotent — no double-deletion).
2. Insert the `TenantDeletions` row: `Status=Deactivated`, `ScheduledPurgeAt = now + 30d`.
3. Set `Tenants.IsActive=false`, `DisabledAt=now`, `DisabledByUserId=operator` (the long-unused columns finally used).
4. Write an audit entry (`AuditLog`, tenant-scoped) recording the deletion request.

After commit (never inside the transaction — following the reclassify-dispatcher pattern from the retention batch): call `BillingApiClient.CancelSubscription` with **immediate** semantics. The Stripe customer + `StripeCustomers` row are **kept** so a restore is clean.

**Effects of `Tenants.IsActive=false`** (both must be enforced — these gates do not all exist today and are part of this work):
- **Ingest** rejected — `IsIngestEligibleAsync` must additionally require the tenant to be active.
- **Web login / session** for that tenant blocked.

## Phase 2 — Purge (scheduled, `TenantPurgeJob`)

A Hangfire recurring job (runs on a modest interval, e.g. hourly) selects `TenantDeletions` where `Status=Deactivated AND ScheduledPurgeAt <= now`. For each, an **idempotent, resumable** teardown:

**Purge (DELETE WHERE TenantId=), children→parents** (no cascade exists from `Tenants`; follow the migration's own `Down()` drop order):
- Child/leaf tables first: `MachineStateDetail`, `AlertConditionStates`*, `AlertRuleMachines`*, `IntegrationDeliveryAttempts`*, `AlertEmailDeliveryAttempts`, `MachineAuthorizedKeys`, `MachineStateSummary` (*already `ON DELETE CASCADE` from their parent — deleting the parent handles them, but the teardown is explicit for clarity/idempotence).
- `MachineTelemetry` (partitioned — `DELETE WHERE TenantId=` scatters across daily partitions; not a partition drop), `AlertEvents` (partitioned), `RemoteCommands` (partitioned), `AlertRules`, `IntegrationEndpoints`, `RegistrationTokens`.
- `Machines`.
- Tenant-direct: `TenantOidcConfigurations` (drops the encrypted OIDC client secret), `TenantInvitations`, `TenantSubscriptionOverrides`, `TenantSubscriptions`, `DataExportJobs`, `UserSigningKeys`.

**User handling:**
- Delete this tenant's `UserTenantRoles` rows.
- For each user who had a role in this tenant: if they now have **zero active roles in any tenant**, mask their `UserAccounts` PII — null/tombstone `Email` (→ `deleted-user-{id}@deleted.invalid` or null), `DisplayName`/name fields, and the OIDC subject/`ExternalId` (so a future signup with the same identity is a fresh account, not a re-link). Keep the row and its `Id`. Users with other active memberships are left completely untouched.

**Keep (never purged):** `Tenants` (stays disabled), `AuditLog` (the record-keeping trail — its FKs to the kept `Tenants` and masked `UserAccounts` rows remain valid), `UserAccounts` (masked-or-untouched per above).

**Billing:** after the fleet-DB teardown, call `BillingApiClient.DeleteCustomer` → billing-api deletes the Stripe customer object and removes the `StripeCustomers` + `PendingActions` rows (billing-api's own DB, and its own `BillingAuditLog`).

**Completion:** set `TenantDeletions.Status=Purged`, `PurgedAt=now`; write a `TenantId=NULL` audit entry (survives any tenant-scoped query) recording completion.

## Restore (operator action during the 30-day grace)

For a `Deactivated` deletion: set `Status=Restored`, clear `Tenants.IsActive`/`DisabledAt`/`DisabledByUserId`, write an audit entry. Nothing was masked or purged yet, so restore is clean. The subscription was canceled at day 0, so the tenant returns on **Free tier** and can re-subscribe. Restore is blocked once `Status=Purged`.

## Error handling

- **Idempotent / resumable purge.** Each teardown step is a `DELETE WHERE TenantId=` (already-empty is a no-op) and the user-mask checks-and-skips already-masked rows, so a job that dies mid-purge re-runs from the top and finishes cleanly — no stranded half-purged tenant.
- **Billing failure isolation.** If `DeleteCustomer` fails (Stripe/billing-api down), the fleet-side teardown still completes, but the deletion stays `Deactivated` (not `Purged`) so the next job tick retries the billing step. Nothing is marked complete until fleet **and** billing both succeed.
- **Partial-failure visibility.** Every phase writes an audit entry; a deletion stuck in `Deactivated` past its purge time (because billing keeps failing) is visible in the `fleet/deletions` list and in logs.
- **Double-deletion guard.** Phase 1 rejects a new request when a non-`Restored` deletion row already exists for the tenant.

## Known residual (documented, accepted for v1)

Audit-log free-text descriptions may contain PII captured at write time (e.g. an "invited jane@example.com" entry). The structured audit columns carry no PII, but the details text is retained as part of the immutable record. Scrubbing PII from historical audit text is out of scope for v1; the audit log is retained under a legitimate-interest/record-keeping basis. Revisit only if a stricter erasure standard is required.

## Testing

- **Unit** (`services.core`): the orphaned-user decision (multi-tenant user keeps other memberships and PII; single-tenant user is flagged for masking); the deletion state machine (deactivate → purge; deactivate → restore; reject double-delete; reject restore-after-purge); `ScheduledPurgeAt` computation.
- **Integration (real Postgres, mandatory gate):** seed a tenant with data across every tenant-scoped table (including the partitioned ones) plus a second tenant sharing a user; run the full purge; assert every purge-list table is empty for the deleted tenant, the `Tenants`/`AuditLog` rows survive, the orphaned user's PII is masked while the shared user (and their PII, and the second tenant's data) is fully intact.
- **Functional (gRPC):** `RequestTenantDeletion`, `RestoreTenant`, `ListTenantDeletions` — including the ingest-blocked-after-deactivation gate and the double-delete rejection.
- **billing-api tests:** immediate-cancel and delete-customer paths (mock `IStripeGateway`), plus `StripeCustomers`/`PendingActions` row removal.
- **Vitest (admin SPA):** the tenant-detail delete action and the deletions list/restore.

## Scope boundaries (YAGNI)

- **No auto-export in the tool.** The self-service data export already exists and the customer has the 30-day window to use it. The operator can point them to it.
- **No per-deletion grace override.** Fixed 30 days.
- **No customer-facing self-serve deletion.** Operator-driven only, matching the "contact support" privacy-policy wording.
- **No audit-text PII scrubbing** (see residual above).

## Execution note

This feature touches `InitialMigration.cs` (the `TenantDeletions` table), which the in-flight `production-readiness` branch also modified. Build it **on top of `production-readiness`** (or after that branch merges to `main`) to avoid a migration conflict; the plan will branch accordingly.
