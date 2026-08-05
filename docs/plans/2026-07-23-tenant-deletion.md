# Tenant Deletion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> **Never `git commit` or push in either repo** — leave all changes in the working tree; Jonathan reviews and commits. (Commit steps are omitted from the tasks per this repo's conservative profile.)
> **Run all code/task/final reviews under the Fable model.**

**Goal:** Give an operator an internal-admin tool that honors an account-deletion request within the 30-day privacy-policy window — deactivating the tenant immediately (ingest stops, login blocked, billing canceled), then hard-purging the tenant's operational + personal data 30 days later while keeping a masked identity skeleton so the audit trail stays referentially valid.

**Architecture:** Two phases driven from the internal admin SPA. **Phase 1 (synchronous)**: the operator's action inserts a `TenantDeletions` row, sets `Tenants.IsActive=false`, writes an audit entry (one fleet-DB transaction), then — after commit — cancels the Stripe subscription immediately. **Phase 2 (Hangfire recurring `TenantPurgeJob`)**: 30 days later, an idempotent ordered teardown deletes all tenant-scoped operational data, masks users orphaned by the deletion, deletes the Stripe customer, and marks the deletion `Purged`. `Tenants`, `UserAccounts` (masked-or-untouched), and `AuditLog` survive. Restore is available any time before purge. The whole feature spans two repos coupled by the `Framlux.Vord.BillingGrpc` NuGet contract.

**Tech Stack:** .NET 10 / C#, LinqToDB, FluentMigrator, FastEndpoints, Hangfire.PostgreSQL, gRPC (protobuf), TUnit tests, SvelteKit + Vitest, Stripe SDK (billing-api).

**Repos:**
- `~/Repositories/framlux/vord` — consumes the `Framlux.Vord.BillingGrpc` package (`src/services.core/services.core.csproj`, currently pinned `1.15.0`); implements the fleet server (`FleetAdminService`), the fleet DB (`database` project), the handler + purge job (`services.core`).
- `~/Repositories/framlux/vord-internal` — **owns** `src/billingGrpc/protos/BillingService.proto` and publishes the `Framlux.Vord.BillingGrpc` package; contains `billing-api` (gRPC + REST proxy) and the admin SvelteKit SPA (`src/admin`).

## Global Constraints

- **Package coordination (contract-first).** The proto is owned by vord-internal and consumed by vord as a NuGet package. Every wire change lands in `BillingService.proto` first (Task 1), is packed locally as **`1.16.0`**, and vord's pin is bumped `1.15.0 → 1.16.0`. Package-cache gotcha: if the proto is edited again after the first pack, purge the cache before re-packing: `rm -rf ~/.nuget/packages/framlux.vord.billinggrpc/1.16.0 && dotnet pack src/billingGrpc -c Release -o /tmp/billinggrpc-local`. The local-source entry must NOT remain in vord's committed `nuget.config` — flag it in the completion summary.
- **Branch / sequencing.** vord work builds **on top of `production-readiness`** (the branch that also touched `InitialMigration.cs`). Confirm `git -C ~/Repositories/framlux/vord branch --show-current` is `production-readiness` (or a branch off it) before Task 2. vord-internal work has no such dependency.
- **Migration freeze rule.** Pre-GA only: the `TenantDeletions` table is added in place to `InitialMigration.cs` (the DB is recreated). The moment the first production release ships, `InitialMigration.cs` freezes forever. This plan adds one table there; it must ship before GA or become a new incremental migration.
- **No AI labels.** No review IDs, "Fix N", phase/task numbers, or plan references in code or comments. Comments describe intent in natural language.
- **Time is injected.** Any "now", `ScheduledPurgeAt`, or "is-due" computation goes through an injected `TimeProvider`; tests use `Microsoft.Extensions.Time.Testing.FakeTimeProvider`. Never `DateTimeOffset.UtcNow` in code whose timing a test must control.
- **Code standards (`.editorconfig`, error-level):** no `var` (explicit types); file-scoped namespaces; `_camelCase` private fields; Allman braces; no `this.` (except LinqToDB `DatabaseContext` properties); `using` directives alphabetized; XML docs on public members; blank line before `return` (unless the prior line is a comment); one type per file (endpoint req/res may co-locate); no `!bool` (use `== false`); variable-before-constant comparisons; blank line at EOF; license header on every new file. Everything must build with **0 warnings**.
- **Testing policy (per repo CLAUDE.md):** every new unit of logic gets unit + functional tests; test intent, happy path, error cases, boundaries, and null inputs; target >80% line/branch coverage for new code. Integration tests need Docker/Podman (Testcontainers) — see the CLAUDE.md `DOCKER_HOST` setup for Podman on macOS.

**Build / test commands (copy exact):**

vord (run from `~/Repositories/framlux/vord`):
```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/database/unit.database.csproj
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/integration/integration.csproj
# Target a subset of any TUnit project with: --treenode-filter "*SomeTestName*"
```

vord-internal (run from `~/Repositories/framlux/vord-internal`):
```bash
dotnet build vord-internal.slnx -c Release
dotnet run --project test/billing/billing.csproj
pnpm -C src/admin test        # Vitest
pnpm -C src/admin check
```

---

## File Structure

**vord-internal (contract + billing + SPA)**
- `src/billingGrpc/protos/BillingService.proto` — MODIFY: 3 new FleetAdmin RPCs + messages; 2 new BillingManagement RPCs + messages; version bump.
- `src/billing-api/Services/IStripeGateway.cs` / `StripeGateway.cs` — MODIFY: `CancelSubscriptionImmediatelyAsync`.
- `src/billing-api/Services/BillingManagementService.cs` — MODIFY: `CancelSubscriptionImmediate`, `DeleteCustomer` RPCs.
- `src/billing-api/Services/IFleetAdminClient.cs` / `FleetAdminClient.cs` — MODIFY: 3 tenant-deletion passthroughs.
- `src/billing-api/Endpoints/Admin/FleetTenantDeletionEndpoints.cs` — CREATE: REST proxy (request/restore/list).
- `src/admin/src/lib/api.ts`, `src/lib/types.ts` — MODIFY: client methods + types.
- `src/admin/src/routes/fleet/tenants/[externalId]/*` — CREATE: tenant detail + delete action.
- `src/admin/src/routes/fleet/deletions/*` — CREATE: deletions list + restore.

**vord (fleet DB + logic + server)**
- `src/database/Enums/TenantDeletionStatus.cs` — CREATE.
- `src/database/Enums/AuditAction.cs` — MODIFY: 3 new actions.
- `src/database/Models/TenantDeletion.cs` — CREATE.
- `src/database/TableNames.cs`, `src/database/DatabaseContext.cs` — MODIFY: register table.
- `src/database/Migrations/InitialMigration.cs` — MODIFY: create `TenantDeletions` + `Down()` drop.
- `src/database/Repositories/ITenantDeletionRepository.cs` + `DatabaseRepository.TenantDeletions.cs` — CREATE: lifecycle rows + purge/teardown/mask ops.
- `src/services.core/Services/Handlers/TenantDeletionHandler.cs` — CREATE: Phase-1 request + restore + decision helpers.
- `src/services.core/Services/Billing/SubscriptionService.cs` — MODIFY: ingest gate requires active tenant.
- `src/server/Auth/AllowedRolesHandler.cs` (+ role-claim source) — MODIFY: deactivated-tenant session block.
- `src/server/Endpoints/Web/Tenants/TenantSwitchEndpoint.cs` — MODIFY: block switch to inactive tenant.
- `src/services.core/Services/Jobs/TenantPurgeJob.cs` — CREATE: Phase-2 teardown.
- `src/services.core/Hangfire/RecurringJobIds.cs` + `RecurringJobRegistry.cs` — MODIFY: register job.
- `src/services.core/Services/Billing/IBillingApiClient.cs` / `BillingApiClient.cs` / `NoOpBillingApiClient.cs` — MODIFY: immediate-cancel + delete-customer.
- `src/server/Endpoints/grpc/FleetAdminService.cs` — MODIFY: 3 new RPCs.
- `src/services.core/Extensions/ServiceCollectionExtensions.cs` — MODIFY: DI registrations.

---

## Task 1: Proto contract 1.16.0 + local pack + vord pin bump (vord-internal → vord)

**Files:**
- Modify: `vord-internal/src/billingGrpc/protos/BillingService.proto`
- Modify: `vord-internal/src/billingGrpc/billingGrpc.csproj` (version)
- Modify: `vord/src/services.core/services.core.csproj` (pin)
- Modify (if needed): `vord/nuget.config` (local source — must be reverted before commit)

**Interfaces produced (both repos compile against these generated types):**
- `FleetAdmin` service gains: `RequestTenantDeletion(RequestTenantDeletionRequest) → RequestTenantDeletionResponse`, `RestoreTenant(RestoreTenantRequest) → RestoreTenantResponse`, `ListTenantDeletions(ListTenantDeletionsRequest) → ListTenantDeletionsResponse`.
- `BillingManagement` service gains: `CancelSubscriptionImmediate(CancelSubscriptionImmediateRequest) → CancelSubscriptionImmediateResponse`, `DeleteCustomer(DeleteCustomerRequest) → DeleteCustomerResponse`.

- [ ] **Step 1: Add the FleetAdmin RPCs** to the `service FleetAdmin { … }` block (after the `RemoveTenantOverride` line):

```proto
  rpc RequestTenantDeletion(RequestTenantDeletionRequest) returns (RequestTenantDeletionResponse);
  rpc RestoreTenant(RestoreTenantRequest) returns (RestoreTenantResponse);
  rpc ListTenantDeletions(ListTenantDeletionsRequest) returns (ListTenantDeletionsResponse);
```

- [ ] **Step 2: Add the BillingManagement RPCs** to the `service BillingManagement { … }` block (after the `GetPublicCatalog` line):

```proto
  rpc CancelSubscriptionImmediate(CancelSubscriptionImmediateRequest) returns (CancelSubscriptionImmediateResponse);
  rpc DeleteCustomer(DeleteCustomerRequest) returns (DeleteCustomerResponse);
```

- [ ] **Step 3: Add the FleetAdmin messages** at the end of the FleetAdmin request/response section (after `RemoveTenantOverrideResponse`). `status` is the smallint enum value (1=Deactivated, 2=Purged, 3=Restored):

```proto
message RequestTenantDeletionRequest {
  string tenant_external_id = 1;
  int32 requested_by_user_id = 2;
  string reason = 3; // optional free-text
}
message RequestTenantDeletionResponse {
  bool success = 1;
  string message = 2;
  google.protobuf.Timestamp scheduled_purge_at = 3;
}

message RestoreTenantRequest {
  string tenant_external_id = 1;
  int32 requested_by_user_id = 2;
}
message RestoreTenantResponse {
  bool success = 1;
  string message = 2;
}

message ListTenantDeletionsRequest {
  bool include_completed = 1; // false = only pending (Deactivated); true = all
  int32 page = 2;
  int32 page_size = 3;
}
message ListTenantDeletionsResponse {
  repeated TenantDeletionRecord deletions = 1;
  int32 total_count = 2;
}
message TenantDeletionRecord {
  int32 id = 1;
  int32 tenant_id = 2;
  string tenant_external_id = 3;
  string tenant_name = 4;
  int32 requested_by_user_id = 5;
  google.protobuf.Timestamp requested_at = 6;
  google.protobuf.Timestamp scheduled_purge_at = 7;
  int32 status = 8;
  google.protobuf.Timestamp purged_at = 9;
  string reason = 10;
}
```

- [ ] **Step 4: Add the BillingManagement messages** after `GetPublicCatalogResponse`/`CatalogPriceItem`:

```proto
message CancelSubscriptionImmediateRequest {
  string tenant_external_id = 1;
}
message CancelSubscriptionImmediateResponse {
  bool success = 1;
  string message = 2;
}

message DeleteCustomerRequest {
  string tenant_external_id = 1;
}
message DeleteCustomerResponse {
  bool success = 1;
  string message = 2;
}
```

- [ ] **Step 5: Bump the package version.** In `vord-internal/src/billingGrpc/billingGrpc.csproj`, change `<Version>1.15.0</Version>` → `<Version>1.16.0</Version>`.

- [ ] **Step 6: Verify codegen.** From `vord-internal`: `dotnet build src/billingGrpc` — expect Build succeeded and generated `Framlux.Vord.BillingGrpc.FleetAdmin.FleetAdminBase` with the three new abstract methods and `BillingManagement.BillingManagementBase` with the two new ones (proto prefix stripped to PascalCase by protoc's C# codegen).

- [ ] **Step 7: Pack locally.** From `vord-internal`: `dotnet pack src/billingGrpc -c Release -o /tmp/billinggrpc-local` — expect `Successfully created package '/tmp/billinggrpc-local/Framlux.Vord.BillingGrpc.1.16.0.nupkg'`.

- [ ] **Step 8: Point vord at the local package.** Ensure `vord/nuget.config` has a local source for `/tmp/billinggrpc-local` (the working tree already carries a local-source modification per the prior package handoff; reuse it). Then in `vord/src/services.core/services.core.csproj` bump the pin: `<PackageReference Include="Framlux.Vord.BillingGrpc" Version="1.16.0" />`.

- [ ] **Step 9: Verify both repos restore the new contract.** `dotnet build vord-internal.slnx -c Release` (0 warnings). From `vord`: `dotnet build machine-info.slnx` — this will now FAIL with "does not implement inherited abstract member" for the five new RPCs on `FleetAdminService` (vord) — that failure is expected and is resolved by Tasks 2–12. Confirm the failure names exactly the five new RPCs (proves the new contract is being consumed) before proceeding.

---

## Task 2: `TenantDeletions` table, model, enums, and DatabaseContext wiring (vord)

**Files:**
- Create: `vord/src/database/Enums/TenantDeletionStatus.cs`
- Create: `vord/src/database/Models/TenantDeletion.cs`
- Modify: `vord/src/database/Enums/AuditAction.cs`
- Modify: `vord/src/database/TableNames.cs`
- Modify: `vord/src/database/DatabaseContext.cs`
- Modify: `vord/src/database/Migrations/InitialMigration.cs`
- Test: `vord/test/unit/database/Migrations/TenantDeletionsSchemaTests.cs` (or the nearest existing migration-schema test file)

**Interfaces produced:**
- `enum TenantDeletionStatus : short { Deactivated = 1, Purged = 2, Restored = 3 }`
- `TenantDeletion` model with `Id, TenantId, TenantExternalId, TenantName, RequestedByUserId, RequestedAt, ScheduledPurgeAt, Status, PurgedAt, Reason`.
- `TableNames.TenantDeletions = "TenantDeletions"`; `DatabaseContext.TenantDeletions` (`ITable<TenantDeletion>`).
- `AuditAction.TenantDeletionRequested = 144`, `TenantPurged = 145`, `TenantRestored = 146`.

- [ ] **Step 1: Create the status enum.** `TenantDeletionStatus.cs` (license header + XML docs; `: short` so it maps to the `smallint` column):

```csharp
namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Lifecycle state of a tenant deletion. One row per deletion in <c>TenantDeletions</c>.
/// </summary>
public enum TenantDeletionStatus : short
{
    /// <summary>Phase 1 complete: tenant deactivated, awaiting the scheduled purge.</summary>
    Deactivated = 1,

    /// <summary>Phase 2 complete: operational and personal data purged, identity skeleton masked.</summary>
    Purged = 2,

    /// <summary>Operator canceled the deletion during the grace window; tenant reactivated.</summary>
    Restored = 3,
}
```

- [ ] **Step 2: Add the audit actions.** In `AuditAction.cs`, after the last existing member (`TenantCreatedByAdmin = 143`) — first confirm no value ≥ 144 already exists, then add:

```csharp
    /// <summary>An operator requested tenant deletion (Phase 1 deactivation).</summary>
    TenantDeletionRequested = 144,

    /// <summary>A tenant's operational and personal data was purged (Phase 2).</summary>
    TenantPurged = 145,

    /// <summary>An operator restored a tenant during the deletion grace window.</summary>
    TenantRestored = 146,
```

- [ ] **Step 3: Create the model.** `TenantDeletion.cs` — mirror the LinqToDB attribute style of `TenantSubscriptionOverride`/`Tenant`:

```csharp
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// One row per tenant deletion. Source of truth for the deletion lifecycle: work queue for the
/// purge job, permanent tombstone that survives the purge, and the admin panel's data source.
/// Holds no personal PII, so it persists forever as the "tenant N deleted on X by Y" record.
/// </summary>
[Table("TenantDeletions")]
public sealed class TenantDeletion
{
    /// <summary>Identity primary key.</summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public int Id { get; set; }

    /// <summary>The deleted tenant. The Tenants row is kept (disabled) even after purge.</summary>
    [Column("TenantId"), NotNull]
    public int TenantId { get; set; }

    /// <summary>Denormalized external id so the record is self-describing after purge.</summary>
    [Column("TenantExternalId"), NotNull]
    public required string TenantExternalId { get; set; }

    /// <summary>Org name (not personal data) — for the operator's record.</summary>
    [Column("TenantName"), NotNull]
    public required string TenantName { get; set; }

    /// <summary>Operator who triggered the deletion.</summary>
    [Column("RequestedByUserId"), NotNull]
    public int RequestedByUserId { get; set; }

    /// <summary>When Phase 1 ran.</summary>
    [Column("RequestedAt"), NotNull]
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>RequestedAt + 30 days. Phase 2 fires at/after this instant.</summary>
    [Column("ScheduledPurgeAt"), NotNull]
    public DateTimeOffset ScheduledPurgeAt { get; set; }

    /// <summary>Lifecycle state.</summary>
    [Column("Status"), NotNull]
    public TenantDeletionStatus Status { get; set; }

    /// <summary>Set when Phase 2 completes.</summary>
    [Column("PurgedAt"), Nullable]
    public DateTimeOffset? PurgedAt { get; set; }

    /// <summary>Free-text reason captured by the operator.</summary>
    [Column("Reason"), Nullable]
    public string? Reason { get; set; }
}
```

- [ ] **Step 4: Register the table name.** In `TableNames.cs`, add: `public const string TenantDeletions = "TenantDeletions";`

- [ ] **Step 5: Expose the table** in `DatabaseContext.cs` (after the `TenantSubscriptionOverrides` property):

```csharp
    /// <summary>Gets the tenant deletion lifecycle records.</summary>
    public ITable<TenantDeletion> TenantDeletions => this.GetTable<TenantDeletion>();
```

- [ ] **Step 6: Create the table in the migration.** In `InitialMigration.cs` `Up()`, add after the `TenantSubscriptionOverrides` `Create.Table(...)` block (before `DataProtectionKeys`). Not partitioned:

```csharp
        // Tenant deletion lifecycle. Never purged — persists as the permanent deletion tombstone.
        Create.Table(TableNames.TenantDeletions)
            .WithColumn("Id").AsInt32().PrimaryKey().Identity().NotNullable()
            .WithColumn("TenantId").AsInt32().NotNullable().ForeignKey(TableNames.Tenants, "Id")
            .WithColumn("TenantExternalId").AsString().NotNullable()
            .WithColumn("TenantName").AsString().NotNullable()
            .WithColumn("RequestedByUserId").AsInt32().NotNullable()
            .WithColumn("RequestedAt").AsDateTimeOffset().NotNullable()
            .WithColumn("ScheduledPurgeAt").AsDateTimeOffset().NotNullable()
            .WithColumn("Status").AsInt16().NotNullable()
            .WithColumn("PurgedAt").AsDateTimeOffset().Nullable()
            .WithColumn("Reason").AsString().Nullable();

        // The double-deletion guard is a partial unique index: at most one non-Restored (Deactivated
        // or Purged) row per tenant. Status 3 = Restored is excluded so a restored tenant can be
        // deleted again later.
        IfDatabase("PostgreSQL").Execute.Sql(
            @"CREATE UNIQUE INDEX ""IX_TenantDeletions_ActiveTenant""
              ON ""TenantDeletions"" (""TenantId"") WHERE ""Status"" <> 3");
        IfDatabase("SQLite").Execute.Sql(
            @"CREATE UNIQUE INDEX ""IX_TenantDeletions_ActiveTenant""
              ON ""TenantDeletions"" (""TenantId"") WHERE ""Status"" <> 3");
```

- [ ] **Step 7: Drop it in `Down()`.** Add `Delete.Table(TableNames.TenantDeletions);` before `Delete.Table("TenantSubscriptionOverrides");` (dropped before `Tenants`, which it FKs).

- [ ] **Step 8: Write a schema test.** Create `TenantDeletionsSchemaTests.cs` under `test/unit/database` (follow the existing migration/schema test pattern in that project — apply the migration to in-memory SQLite via the project's migration test harness, then assert the table and columns exist and a row round-trips). If the project has no migration-apply harness, instead write a model-round-trip test against the in-memory `DatabaseContext` the other `unit.database` tests use. Assert: insert a `TenantDeletion`, read it back, all fields equal; a second insert with the same `TenantId` and `Status=Deactivated` throws (unique-index guard); a second insert with `Status=Restored` on the first row updated to `Restored` is allowed.

- [ ] **Step 9: Run it.** `dotnet run --project test/unit/database/unit.database.csproj --treenode-filter "*TenantDeletions*"` — expect all pass.

---

## Task 3: `ITenantDeletionRepository` — lifecycle rows (vord)

**Files:**
- Create: `vord/src/database/Repositories/ITenantDeletionRepository.cs`
- Create: `vord/src/database/Repositories/DatabaseRepository.TenantDeletions.cs`
- Modify: `vord/src/services.core/Extensions/ServiceCollectionExtensions.cs`
- Test: `vord/test/unit/database/Repositories/TenantDeletionRepositoryTests.cs`

**Interfaces produced (consumed by the handler and job):**
```csharp
Task<TenantDeletion?> GetActiveDeletionForTenantAsync(int tenantId, CancellationToken ct);      // non-Restored row, if any
Task<TenantDeletion> InsertDeletionAsync(TenantDeletion deletion, CancellationToken ct);         // returns row with Id
Task<int> UpdateDeletionStatusAsync(int id, TenantDeletionStatus status, DateTimeOffset? purgedAt, CancellationToken ct);
Task<List<TenantDeletion>> GetDueDeletionsAsync(DateTimeOffset now, CancellationToken ct);        // Status=Deactivated AND ScheduledPurgeAt<=now
Task<(List<TenantDeletion> Deletions, int TotalCount)> ListDeletionsAsync(bool includeCompleted, int skip, int take, CancellationToken ct);
```

- [ ] **Step 1: Write failing repository tests.** Create `TenantDeletionRepositoryTests.cs` mirroring the construction pattern of the sibling `unit.database` repository tests (they build a `DatabaseRepository` over an in-memory SQLite `DatabaseContext` — copy the setup from `TenantSubscriptionOverrideRepositoryTests` or the nearest existing one). Cover:

```csharp
// Happy path: insert returns a row with a non-zero Id and persists all fields.
// GetActiveDeletionForTenant: returns a Deactivated row; returns null when the only row is Restored.
// UpdateDeletionStatus: flips Deactivated -> Purged and stamps PurgedAt; returns 1; returns 0 for a missing id.
// GetDueDeletions: returns Deactivated rows with ScheduledPurgeAt <= now; excludes future, Purged, Restored.
// ListDeletions: includeCompleted=false returns only Deactivated; =true returns all, ordered RequestedAt desc; TotalCount correct; paging via skip/take.
```

- [ ] **Step 2: Run to confirm they fail** (compile error — repo doesn't exist yet): `dotnet run --project test/unit/database/unit.database.csproj --treenode-filter "*TenantDeletionRepository*"` — expect build failure / not found.

- [ ] **Step 3: Create the interface.** `ITenantDeletionRepository.cs` with the five methods above, each with XML docs.

- [ ] **Step 4: Implement the partial repository.** `DatabaseRepository.TenantDeletions.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Tenant-deletion lifecycle persistence. See <see cref="ITenantDeletionRepository"/>.
/// </summary>
public partial class DatabaseRepository : ITenantDeletionRepository
{
    /// <inheritdoc/>
    public async Task<TenantDeletion?> GetActiveDeletionForTenantAsync(int tenantId, CancellationToken ct)
    {
        return await _db.TenantDeletions
            .Where(d => (d.TenantId == tenantId) && (d.Status != TenantDeletionStatus.Restored))
            .OrderByDescending(d => d.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<TenantDeletion> InsertDeletionAsync(TenantDeletion deletion, CancellationToken ct)
    {
        int id = await _db.InsertWithInt32IdentityAsync(deletion, ct);
        deletion.Id = id;

        return deletion;
    }

    /// <inheritdoc/>
    public async Task<int> UpdateDeletionStatusAsync(int id, TenantDeletionStatus status, DateTimeOffset? purgedAt, CancellationToken ct)
    {
        return await _db.TenantDeletions
            .Where(d => d.Id == id)
            .Set(d => d.Status, status)
            .Set(d => d.PurgedAt, purgedAt)
            .UpdateAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<List<TenantDeletion>> GetDueDeletionsAsync(DateTimeOffset now, CancellationToken ct)
    {
        return await _db.TenantDeletions
            .Where(d => (d.Status == TenantDeletionStatus.Deactivated) && (d.ScheduledPurgeAt <= now))
            .OrderBy(d => d.ScheduledPurgeAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<(List<TenantDeletion> Deletions, int TotalCount)> ListDeletionsAsync(
        bool includeCompleted, int skip, int take, CancellationToken ct)
    {
        IQueryable<TenantDeletion> query = _db.TenantDeletions;
        if (includeCompleted == false)
        {
            query = query.Where(d => d.Status == TenantDeletionStatus.Deactivated);
        }

        int total = await query.CountAsync(ct);
        List<TenantDeletion> rows = await query
            .OrderByDescending(d => d.RequestedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return (rows, total);
    }
}
```

Note: confirm `_db.InsertWithInt32IdentityAsync` is the identity-insert helper the sibling repos use (grep `InsertWithInt32IdentityAsync` in `src/database/Repositories`); if the codebase uses a different helper (e.g. `InsertWithIdentityAsync` cast), match that.

- [ ] **Step 5: Register DI.** In `ServiceCollectionExtensions.cs`, alongside the other `AddScoped<I…Repository>(sp => sp.GetRequiredService<DatabaseRepository>())` lines:

```csharp
        services.AddScoped<ITenantDeletionRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
```

- [ ] **Step 6: Run the tests.** `dotnet run --project test/unit/database/unit.database.csproj --treenode-filter "*TenantDeletionRepository*"` — expect all pass.

---

## Task 4: Purge, mask, and deactivation repository operations (vord)

**Files:**
- Modify: `vord/src/database/Repositories/ITenantDeletionRepository.cs`
- Modify: `vord/src/database/Repositories/DatabaseRepository.TenantDeletions.cs`
- Test: `vord/test/unit/database/Repositories/TenantDeletionRepositoryTests.cs` (extend)

**Interfaces produced:**
```csharp
Task SetTenantActiveAsync(int tenantId, bool isActive, int? disabledByUserId, DateTimeOffset? disabledAt, CancellationToken ct);
Task PurgeTenantOperationalDataAsync(int tenantId, CancellationToken ct);   // idempotent, ordered teardown
Task<List<int>> GetUserIdsWithAnyRoleInTenantAsync(int tenantId, CancellationToken ct);  // active OR inactive roles
Task DeleteUserTenantRolesForTenantAsync(int tenantId, CancellationToken ct);
Task<bool> UserHasAnyActiveRoleAsync(int userId, CancellationToken ct);      // across ALL tenants
Task<int> MaskUserAsync(int userId, CancellationToken ct);                    // returns rows updated; skips already-masked
```

- [ ] **Step 1: Extend the interface** with the six methods above (XML docs each). Document on `PurgeTenantOperationalDataAsync` that it is idempotent (each delete is `WHERE TenantId=`/subquery, so a re-run on an already-purged tenant is a no-op) and deletes children-before-parents following the migration's `Down()` order.

- [ ] **Step 2: Write failing integration-style unit tests** in `TenantDeletionRepositoryTests.cs`. Because the teardown spans many tables including child tables scoped by machine/rule/endpoint, seed a small graph against the in-memory `DatabaseContext`: one tenant with a machine, a telemetry row, an alert rule, an alert-rule-machine link, an alert condition state, an integration endpoint, a registration token, an OIDC config, a subscription, a signing key; plus a **second** tenant with its own machine + telemetry. Assert after `PurgeTenantOperationalDataAsync(tenant1)`:

```csharp
// Every tenant-scoped table is empty for tenant1.
// Tenant2's rows are all intact (no cross-tenant deletion).
// Re-running PurgeTenantOperationalDataAsync(tenant1) does not throw and changes nothing (idempotent).
// SetTenantActiveAsync(tenant1, false, operatorId, now): Tenants row IsActive=false, DisabledAt/DisabledByUserId set.
// GetUserIdsWithAnyRoleInTenantAsync returns members (active and disabled roles).
// DeleteUserTenantRolesForTenantAsync removes only tenant1's UserTenantRoles.
// UserHasAnyActiveRoleAsync: true for a user with a role in tenant2, false for a user only in tenant1 after its roles are deleted.
// MaskUserAsync: nulls/tombstones Email/DisplayName/ExternalId; returns 1; a second call returns 0 (already masked, detected by the tombstone sentinel).
```

> Note: the deep partition behavior (telemetry scattering across daily partitions) is exercised by the mandatory Postgres integration test in Task 15 — the SQLite unit test proves the LinqToDB delete predicates and ordering are correct; the integration test proves they work against the real partitioned schema.

- [ ] **Step 3: Implement `SetTenantActiveAsync`:**

```csharp
    /// <inheritdoc/>
    public async Task SetTenantActiveAsync(
        int tenantId, bool isActive, int? disabledByUserId, DateTimeOffset? disabledAt, CancellationToken ct)
    {
        await _db.Tenants
            .Where(t => t.Id == tenantId)
            .Set(t => t.IsActive, isActive)
            .Set(t => t.DisabledAt, disabledAt)
            .Set(t => t.DisabledByUserId, disabledByUserId)
            .UpdateAsync(ct);
    }
```

- [ ] **Step 4: Implement `PurgeTenantOperationalDataAsync`.** Order follows `InitialMigration.Down()`. Tables with a `TenantId` column delete directly; child tables scoped by machine/rule/endpoint/event delete via a subquery on their parent's `TenantId` (idempotent, cascade-independent). Keep the exact ordering — children before parents:

```csharp
    /// <inheritdoc/>
    public async Task PurgeTenantOperationalDataAsync(int tenantId, CancellationToken ct)
    {
        // Child/leaf tables scoped through their parent's TenantId (they carry no TenantId column).
        await _db.MachineStateDetails
            .Where(x => _db.Machines.Any(m => (m.Id == x.MachineId) && (m.TenantId == tenantId)))
            .DeleteAsync(ct);
        await _db.AlertConditionStates
            .Where(x => _db.AlertRules.Any(r => (r.Id == x.AlertRuleId) && (r.TenantId == tenantId)))
            .DeleteAsync(ct);
        await _db.AlertRuleMachines
            .Where(x => _db.AlertRules.Any(r => (r.Id == x.AlertRuleId) && (r.TenantId == tenantId)))
            .DeleteAsync(ct);
        await _db.IntegrationDeliveryAttempts
            .Where(x => _db.IntegrationEndpoints.Any(e => (e.Id == x.IntegrationEndpointId) && (e.TenantId == tenantId)))
            .DeleteAsync(ct);
        await _db.AlertEmailDeliveryAttempts
            .Where(x => _db.AlertEvents.Any(e => (e.Id == x.AlertEventId) && (e.TenantId == tenantId)))
            .DeleteAsync(ct);

        // Tables carrying TenantId directly.
        await _db.MachineAuthorizedKeys.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.MachineStateSummaries.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.MachineTelemetry.Where(x => x.TenantId == tenantId).DeleteAsync(ct);        // partitioned
        await _db.AlertEvents.Where(x => x.TenantId == tenantId).DeleteAsync(ct);             // partitioned
        await _db.RemoteCommands.Where(x => x.TenantId == tenantId).DeleteAsync(ct);          // partitioned
        await _db.AlertRules.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.IntegrationEndpoints.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.RegistrationTokens.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.UserSigningKeys.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.DataExportJobs.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.Machines.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.TenantOidcConfigurations.Where(x => x.TenantId == tenantId).DeleteAsync(ct); // drops encrypted OIDC secret
        await _db.TenantInvitations.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.TenantSubscriptionOverrides.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.TenantSubscriptions.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        // UserTenantRoles for this tenant are removed by DeleteUserTenantRolesForTenantAsync in the job,
        // after the orphan computation reads them — not here.
    }
```

> Before implementing, verify the exact `DatabaseContext` property names (they are pluralized irregularly — e.g. `MachineStateSummaries`, `MachineStateDetails`, `AlertConditionStates`, `AlertRuleMachines`). Use the csharp-lsp `workspaceSymbol` / the `DatabaseContext.cs` property list, not guesswork. `MachineStateProjectionCursor` is deliberately **not** purged — it is internal projection high-water-mark state keyed by shard, carries no tenant/machine linkage, and holds no tenant data. `MachineCertificates` has no model/table (unused) — skip.

- [ ] **Step 5: Implement the user-handling methods:**

```csharp
    /// <inheritdoc/>
    public async Task<List<int>> GetUserIdsWithAnyRoleInTenantAsync(int tenantId, CancellationToken ct)
    {
        return await _db.UserTenantRoles
            .Where(r => r.AssignedTenantId == tenantId)
            .Select(r => r.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteUserTenantRolesForTenantAsync(int tenantId, CancellationToken ct)
    {
        await _db.UserTenantRoles.Where(r => r.AssignedTenantId == tenantId).DeleteAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<bool> UserHasAnyActiveRoleAsync(int userId, CancellationToken ct)
    {
        return await _db.UserTenantRoles
            .AnyAsync(r => (r.UserId == userId) && (r.IsActive == true), ct);
    }

    /// <inheritdoc/>
    public async Task<int> MaskUserAsync(int userId, CancellationToken ct)
    {
        // Masking is idempotent: an already-masked account carries the tombstone marker in ExternalId,
        // so re-running skips it (returns 0). Email/Username become a per-id tombstone; the OIDC subject
        // (ExternalId) is replaced so a future signup with the same identity is a fresh account, never a
        // re-link. The row and its Id are kept so every AuditLog FK stays valid.
        string tombstone = $"deleted-user-{userId}";

        return await _db.UserAccounts
            .Where(u => (u.Id == userId) && (u.ExternalId.StartsWith("deleted-user-") == false))
            .Set(u => u.Email, $"{tombstone}@deleted.invalid")
            .Set(u => u.Username, tombstone)
            .Set(u => u.ExternalId, $"{tombstone}:{(short)u.AuthProvider}")
            .UpdateAsync(ct);
    }
```

> Verify the `UserAccount` PII column names before implementing (the model exposes `Username` and `ExternalId`; confirm whether an `Email`/`DisplayName` column exists — grep `UserAccount.cs`. Mask exactly the personal columns that exist; do not reference a column that isn't there). The tombstone-in-`ExternalId` sentinel is what makes `MaskUserAsync` idempotent — keep it consistent with the `StartsWith` guard.

- [ ] **Step 6: Run the tests.** `dotnet run --project test/unit/database/unit.database.csproj --treenode-filter "*TenantDeletionRepository*"` — expect all pass.

---

## Task 5: `TenantDeletionHandler` — Phase 1 request + restore (vord)

**Files:**
- Create: `vord/src/services.core/Services/Handlers/TenantDeletionHandler.cs`
- Modify: `vord/src/services.core/Extensions/ServiceCollectionExtensions.cs`
- Test: `vord/test/unit/services.core/Services/Handlers/TenantDeletionHandlerTests.cs`

**Interfaces produced (consumed by `FleetAdminService` and the purge job):**
```csharp
public sealed record TenantDeletionResult(bool Success, string Message, DateTimeOffset? ScheduledPurgeAt);

public sealed class TenantDeletionHandler
{
    Task<TenantDeletionResult> RequestDeletionAsync(int tenantId, int requestedByUserId, string? reason, CancellationToken ct);
    Task<TenantDeletionResult> RestoreAsync(int tenantId, int requestedByUserId, CancellationToken ct);

    internal static DateTimeOffset ComputeScheduledPurge(DateTimeOffset requestedAt);   // requestedAt + 30d
    internal static bool IsUserOrphanedByTenantRemoval(bool hasActiveRoleElsewhere);    // decision seam
}
```

- [ ] **Step 1: Write failing unit tests.** `TenantDeletionHandlerTests.cs`. Mock `ITenantRepository`, `ITenantDeletionRepository`, `IAuditLogRepository`, `IDatabaseTransactionProvider` (+ `IDatabaseTransaction`), `IBillingApiClient`; inject a `FakeTimeProvider`. Cover the state machine and the two static helpers:

```csharp
// ComputeScheduledPurge: requestedAt + exactly 30 days.
// IsUserOrphanedByTenantRemoval: true when no active role elsewhere; false otherwise.
// RequestDeletion happy path: inserts a Deactivated row with ScheduledPurgeAt = now+30d; deactivates the
//   tenant (SetTenantActiveAsync false + operator + now); writes a TenantDeletionRequested audit entry;
//   commits; THEN calls BillingApiClient.CancelSubscriptionImmediateAsync (never before commit); returns
//   Success=true with the ScheduledPurgeAt.
// RequestDeletion double-delete guard: GetActiveDeletionForTenant returns an existing Deactivated row ->
//   returns Success=false, no insert, no deactivation, no billing call, transaction not committed.
// RequestDeletion tenant-not-found: returns Success=false.
// RequestDeletion billing failure after commit: CancelSubscriptionImmediateAsync returns false -> handler
//   still returns Success=true (deactivation already durable) and logs a warning (the tenant is deactivated;
//   billing teardown is retried/handled at purge). Assert deactivation persisted.
// Restore happy path (Deactivated row): sets Status=Restored, reactivates the tenant (IsActive true,
//   DisabledAt/DisabledByUserId null), writes a TenantRestored audit entry, commits, returns Success=true.
// Restore blocked after purge: the active row is Purged -> returns Success=false, no state change.
// Restore with no deletion row: returns Success=false.
// FakeTimeProvider drives "now" — no wall-clock dependency.
```

- [ ] **Step 2: Run to confirm they fail:** `dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*TenantDeletionHandler*"` — expect build failure (handler absent).

- [ ] **Step 3: Implement the handler.** Mirror the transaction + after-commit-side-effect discipline of `FleetAdminService.SetTenantOverride` (write in a transaction, do the external call after commit) and `AuditHelper.Create`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Phase-1 tenant-deletion logic: deactivate now (insert the deletion row, disable the tenant, cancel
/// billing immediately) and the operator-error restore escape hatch. The irreversible purge runs later
/// in <c>TenantPurgeJob</c>.
/// </summary>
public sealed class TenantDeletionHandler
{
    /// <summary>Fixed grace window between deactivation and the irreversible purge.</summary>
    internal static readonly TimeSpan GraceWindow = TimeSpan.FromDays(30);

    private readonly ITenantRepository _tenantRepo;
    private readonly ITenantDeletionRepository _deletionRepo;
    private readonly IAuditLogRepository _auditLog;
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IBillingApiClient _billingApiClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantDeletionHandler> _logger;

    /// <summary>Creates a new instance of the <see cref="TenantDeletionHandler"/> class.</summary>
    public TenantDeletionHandler(
        ITenantRepository tenantRepo,
        ITenantDeletionRepository deletionRepo,
        IAuditLogRepository auditLog,
        IDatabaseTransactionProvider transactionProvider,
        IBillingApiClient billingApiClient,
        TimeProvider timeProvider,
        ILogger<TenantDeletionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(tenantRepo);
        ArgumentNullException.ThrowIfNull(deletionRepo);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(billingApiClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _tenantRepo = tenantRepo;
        _deletionRepo = deletionRepo;
        _auditLog = auditLog;
        _transactionProvider = transactionProvider;
        _billingApiClient = billingApiClient;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Grace-window arithmetic, isolated for direct unit testing.</summary>
    internal static DateTimeOffset ComputeScheduledPurge(DateTimeOffset requestedAt)
    {
        return requestedAt.Add(GraceWindow);
    }

    /// <summary>
    /// Decides whether removing this tenant's membership orphans the user. A user is orphaned when
    /// they retain no active role in any tenant after this tenant's roles are removed.
    /// </summary>
    internal static bool IsUserOrphanedByTenantRemoval(bool hasActiveRoleElsewhere)
    {
        return hasActiveRoleElsewhere == false;
    }

    /// <summary>Deactivates the tenant and schedules the purge. Idempotent via the double-deletion guard.</summary>
    public async Task<TenantDeletionResult> RequestDeletionAsync(
        int tenantId, int requestedByUserId, string? reason, CancellationToken ct)
    {
        Tenant? tenant = await _tenantRepo.GetTenantByIdAsync(tenantId, ct);
        if (tenant is null)
        {
            return new TenantDeletionResult(false, "Tenant not found", null);
        }

        TenantDeletion? existing = await _deletionRepo.GetActiveDeletionForTenantAsync(tenantId, ct);
        if (existing is not null)
        {
            return new TenantDeletionResult(false, "Tenant already has a pending or completed deletion", null);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset scheduledPurgeAt = ComputeScheduledPurge(now);

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _deletionRepo.InsertDeletionAsync(new TenantDeletion
        {
            TenantId = tenantId,
            TenantExternalId = tenant.ExternalId,
            TenantName = tenant.Name,
            RequestedByUserId = requestedByUserId,
            RequestedAt = now,
            ScheduledPurgeAt = scheduledPurgeAt,
            Status = TenantDeletionStatus.Deactivated,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        }, ct);

        await _tenantRepo.SetTenantActiveAsync(tenantId, false, requestedByUserId, now, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId: tenantId,
            userId: requestedByUserId,
            machineId: null,
            AuditAction.TenantDeletionRequested,
            AuditResourceType.Tenant,
            tenantId.ToString(),
            new { ScheduledPurgeAt = scheduledPurgeAt, Reason = reason },
            ipAddress: null), ct);

        await transaction.CommitAsync(ct);

        // After commit only — an external call inside the transaction would hold the DB lock across a
        // network round-trip and could not be rolled back anyway (following the reclassify-dispatcher
        // pattern). Billing cancel is best-effort: the tenant is already deactivated, and the purge
        // job tears the Stripe customer down entirely at day 30 regardless.
        bool billingCanceled = await _billingApiClient.CancelSubscriptionImmediateAsync(tenant.ExternalId, ct);
        if (billingCanceled == false)
        {
            _logger.LogWarning(
                "Tenant {TenantId} deactivated but immediate billing cancel failed; purge will reconcile at {ScheduledPurgeAt}",
                tenantId, scheduledPurgeAt);
        }

        _logger.LogInformation("Tenant {TenantId} deactivated; purge scheduled for {ScheduledPurgeAt}", tenantId, scheduledPurgeAt);

        return new TenantDeletionResult(true, "OK", scheduledPurgeAt);
    }

    /// <summary>Cancels a pending deletion during the grace window and reactivates the tenant.</summary>
    public async Task<TenantDeletionResult> RestoreAsync(int tenantId, int requestedByUserId, CancellationToken ct)
    {
        TenantDeletion? deletion = await _deletionRepo.GetActiveDeletionForTenantAsync(tenantId, ct);
        if (deletion is null)
        {
            return new TenantDeletionResult(false, "No pending deletion for this tenant", null);
        }

        if (deletion.Status == TenantDeletionStatus.Purged)
        {
            return new TenantDeletionResult(false, "Tenant has already been purged and cannot be restored", null);
        }

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _deletionRepo.UpdateDeletionStatusAsync(deletion.Id, TenantDeletionStatus.Restored, null, ct);
        await _tenantRepo.SetTenantActiveAsync(tenantId, true, null, null, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId: tenantId,
            userId: requestedByUserId,
            machineId: null,
            AuditAction.TenantRestored,
            AuditResourceType.Tenant,
            tenantId.ToString(),
            new { RestoredDeletionId = deletion.Id },
            ipAddress: null), ct);

        await transaction.CommitAsync(ct);

        _logger.LogInformation("Tenant {TenantId} deletion {DeletionId} restored", tenantId, deletion.Id);

        return new TenantDeletionResult(true, "OK", null);
    }
}
```

Create `TenantDeletionResult.cs` as its own file (one type per file):

```csharp
namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>Outcome of a tenant-deletion Phase-1 operation.</summary>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="Message">Human-readable result or rejection reason.</param>
/// <param name="ScheduledPurgeAt">The scheduled purge instant, when a deletion was created.</param>
public sealed record TenantDeletionResult(bool Success, string Message, DateTimeOffset? ScheduledPurgeAt);
```

- [ ] **Step 4: Register DI** in `ServiceCollectionExtensions.cs` (near the other handler registrations, e.g. `services.AddScoped<MemberHandler>();`):

```csharp
        services.AddScoped<TenantDeletionHandler>();
```

Confirm `TimeProvider` is registered (grep `AddSingleton(TimeProvider.System)` / `TimeProvider` in `ServiceCollectionExtensions.cs`; the codebase already injects it elsewhere — if a root registration exists, nothing to add).

- [ ] **Step 5: Run the tests:** `dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*TenantDeletionHandler*"` — expect all pass.

---

## Task 6: Ingest gate requires an active tenant (vord)

**Files:**
- Modify: `vord/src/services.core/Services/Billing/SubscriptionService.cs`
- Modify: any `ISubscriptionService` test setup that constructs `SubscriptionService` (add the new dependency)
- Test: `vord/test/unit/services.core/Services/Billing/SubscriptionServiceTests.cs`

**Interface change:** `IsIngestEligibleAsync` now additionally requires `Tenant.IsActive`. Extract the decision:
```csharp
internal static bool IsIngestEligible(TenantSubscription? subscription, bool tenantIsActive);
```

- [ ] **Step 1: Write failing tests.** Add to `SubscriptionServiceTests.cs`:

```csharp
// IsIngestEligible(active-sub, tenantActive:true)  -> true
// IsIngestEligible(active-sub, tenantActive:false) -> false   (deactivated tenant blocks ingest even on an active/Free sub)
// IsIngestEligible(pastdue-sub, tenantActive:true) -> true    (dunning grace preserved)
// IsIngestEligible(canceled-sub, tenantActive:true)-> false
// IsIngestEligible(null-sub, tenantActive:true)    -> false
// IsIngestEligibleAsync loads the tenant and returns false when the tenant is inactive even though the
//   subscription is Active (the Free-tenant deactivation case).
```

- [ ] **Step 2: Run to confirm failure.**

- [ ] **Step 3: Implement.** Add `ITenantRepository` to `SubscriptionService`'s constructor (update the field, null-check, and assignment). Extract the decision and consult the tenant:

```csharp
    /// <summary>
    /// Ingest eligibility: an active-or-past-due subscription AND an active tenant. A deactivated
    /// tenant (a pending deletion) never ingests, even on a Free/Active subscription.
    /// </summary>
    internal static bool IsIngestEligible(TenantSubscription? subscription, bool tenantIsActive)
    {
        if (tenantIsActive == false)
        {
            return false;
        }

        return (subscription is not null) &&
               ((subscription.Status == SubscriptionStatus.Active) || (subscription.Status == SubscriptionStatus.PastDue));
    }

    /// <inheritdoc/>
    public async Task<bool> IsIngestEligibleAsync(int tenantId, CancellationToken ct)
    {
        Tenant? tenant = await _tenantRepo.GetTenantByIdAsync(tenantId, ct);
        if ((tenant is null) || (tenant.IsActive == false))
        {
            return false;
        }

        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        return IsIngestEligible(subscription, tenant.IsActive);
    }
```

- [ ] **Step 4: Fix every constructor site.** Per the repo rule, update ALL `SubscriptionService` constructions/mocks (grep `new SubscriptionService(` across `test/` and `src/`) to pass the new `ITenantRepository`. Build to find them: `dotnet build machine-info.slnx` — resolve each CS7036.

- [ ] **Step 5: Run the tests + the full services.core unit project** (ingest gate feeds telemetry acceptance): `dotnet run --project test/unit/services.core/unit.services.core.csproj` — expect all pass.

---

## Task 7: Block web login/session for a deactivated tenant (vord)

**Files:**
- Modify: the role-claim source used by `CookiePrincipalValidator.RefreshRoleClaimsIfChangedAsync` (the `ITenantRepository.GetTenantsForUserAsync` path) OR add an active-only variant
- Modify: `vord/src/server/Endpoints/Web/Tenants/TenantSwitchEndpoint.cs`
- Test: `vord/test/functional/web/…` (tenant-switch + authorization), `vord/test/unit/server/Auth/…`

**Approach:** A deactivated tenant's role claims must stop granting access, and an operator must not be switchable into it. Roles are surfaced as `tenantId:roleValue` claims built from the DB; `AllowedRolesHandler` grants only when the active-tenant role matches. The least-invasive, correct enforcement is to **exclude inactive tenants at the point role claims are built** (so a deactivated tenant's roles vanish from the principal within one refresh) **and** to reject switching into an inactive tenant.

- [ ] **Step 1: Confirm the enforcement points with the LSP.** Use csharp-lsp `findReferences` on `ITenantRepository.GetTenantsForUserAsync` and `GetTenantsForUserByIdAsync` to see every consumer (login claim issuance, `CookiePrincipalValidator`, `AuthMeEndpoint`). Confirm these are the only paths that materialize per-tenant role claims / the tenant list. Record the callers in the task notes so the change's blast radius is known.

- [ ] **Step 2: Write failing functional tests.** In `functional/web`:

```csharp
// A user with a role in tenant A (active) and tenant B (deactivated): AuthMe returns only tenant A.
// Switching to tenant B (deactivated) returns 403/400 and does not set the active-tenant cookie.
// A request scoped to tenant B (active-tenant cookie forced to B) is denied on a tenant-scoped endpoint
//   after B is deactivated (role claim no longer grants access).
// Global admin is unaffected (still bypasses).
// Regression: a user in two ACTIVE tenants still sees and can switch between both.
```

- [ ] **Step 3: Add an active-only tenant/role query** to `ITenantRepository` if `GetTenantsForUserAsync` is used in contexts that must still see inactive tenants (e.g. an admin view). If every caller should hide inactive tenants, filter in place. Prefer a dedicated method to avoid changing unrelated semantics:

```csharp
Task<IEnumerable<UserTenantRole>> GetActiveTenantRolesForUserAsync(string userUniqueId, CancellationToken ct);
// implementation: join UserTenantRoles -> Tenants, WHERE role.IsActive AND tenant.IsActive
```

Point `CookiePrincipalValidator.RefreshRoleClaimsIfChangedAsync`, the login claim issuance, and `AuthMeEndpoint`'s tenant list at the active-only method.

- [ ] **Step 4: Block tenant switch.** In `TenantSwitchEndpoint`, after resolving the target tenant, reject when `tenant.IsActive == false` (return the same 403/validation error the endpoint uses for a tenant the user isn't a member of). Add an `internal static bool CanSwitchToTenant(bool isMember, bool tenantIsActive)` decision method and unit-test it in `unit.server`.

- [ ] **Step 5: Immediacy — bust the role cache at deactivation (optional but recommended).** The role/user-state caches carry a ≤5-minute TTL, so a deactivated tenant's access lapses within 5 minutes on its own. For immediate effect, in `TenantDeletionHandler.RequestDeletionAsync` after commit, invalidate the role cache for the tenant's members via the existing `IRoleCacheInvalidator` (grep for its interface + how `MemberHandler` uses it). If wiring this cleanly expands the handler's dependencies materially, leave the TTL as the backstop and note it — do not block the task on it.

- [ ] **Step 6: Run:** `dotnet run --project test/functional/web/functional.web.csproj` and `dotnet run --project test/unit/server/unit.server.csproj` — expect all pass.

---

## Task 8: `IBillingApiClient` immediate-cancel + delete-customer (vord)

**Files:**
- Modify: `vord/src/services.core/Services/Billing/IBillingApiClient.cs`
- Modify: `vord/src/services.core/Services/Billing/BillingApiClient.cs`
- Modify: `vord/src/services.core/Services/Billing/NoOpBillingApiClient.cs`
- Test: `vord/test/unit/services.core/Services/Billing/BillingApiClientTests.cs`, `NoOpBillingApiClientTests.cs`

**Interfaces produced (consumed by the handler + purge job):**
```csharp
Task<bool> CancelSubscriptionImmediateAsync(string tenantExternalId, CancellationToken ct);
Task<bool> DeleteCustomerAsync(string tenantExternalId, CancellationToken ct);
```

- [ ] **Step 1: Write failing tests** in `BillingApiClientTests.cs`, mirroring the existing `CancelSubscriptionAsync` test's mocked-gRPC-client pattern:

```csharp
// CancelSubscriptionImmediateAsync: success response -> true; success=false response -> false;
//   RpcException -> false (never throws; matches the class's catch-log-return-false contract).
// DeleteCustomerAsync: same three cases.
```

And in `NoOpBillingApiClientTests.cs`: both new methods return `true` (the no-op client is the billing-disabled stand-in — grep the existing no-op returns to match convention).

- [ ] **Step 2: Run to confirm failure.**

- [ ] **Step 3: Add the interface methods** (XML docs) to `IBillingApiClient.cs`.

- [ ] **Step 4: Implement in `BillingApiClient.cs`** (append after `CancelSubscriptionAsync`, same deadline + try/catch shape):

```csharp
    /// <inheritdoc/>
    public async Task<bool> CancelSubscriptionImmediateAsync(string tenantExternalId, CancellationToken ct)
    {
        try
        {
            CancelSubscriptionImmediateResponse response = await _grpcClient.CancelSubscriptionImmediateAsync(
                new CancelSubscriptionImmediateRequest { TenantExternalId = tenantExternalId },
                deadline: DateTime.UtcNow.Add(GrpcDeadline),
                cancellationToken: ct);

            if (response.Success == false)
            {
                _logger.LogWarning(
                    "Failed to immediately cancel subscription for tenant {TenantExternalId}: {Message}",
                    tenantExternalId, response.Message);
            }

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error immediately canceling subscription for tenant {TenantExternalId}", tenantExternalId);

            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteCustomerAsync(string tenantExternalId, CancellationToken ct)
    {
        try
        {
            DeleteCustomerResponse response = await _grpcClient.DeleteCustomerAsync(
                new DeleteCustomerRequest { TenantExternalId = tenantExternalId },
                deadline: DateTime.UtcNow.Add(GrpcDeadline),
                cancellationToken: ct);

            if (response.Success == false)
            {
                _logger.LogWarning(
                    "Failed to delete billing customer for tenant {TenantExternalId}: {Message}",
                    tenantExternalId, response.Message);
            }

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting billing customer for tenant {TenantExternalId}", tenantExternalId);

            return false;
        }
    }
```

- [ ] **Step 5: Implement in `NoOpBillingApiClient.cs`** — both return `Task.FromResult(true)` with a debug log, matching the file's existing no-op methods.

- [ ] **Step 6: Run:** `dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*BillingApiClient*"` — expect all pass. (This depends only on the 1.16.0 contract from Task 1, not on the billing-api implementation.)

---

## Task 9: `TenantPurgeJob` — Phase 2 teardown (vord)

**Files:**
- Create: `vord/src/services.core/Services/Jobs/TenantPurgeJob.cs`
- Modify: `vord/src/services.core/Hangfire/RecurringJobIds.cs`
- Modify: `vord/src/services.core/Hangfire/RecurringJobRegistry.cs`
- Modify: `vord/src/services.core/Extensions/ServiceCollectionExtensions.cs` (register the job type)
- Test: `vord/test/unit/services.core/Services/Jobs/TenantPurgeJobTests.cs`; `vord/test/functional/hangfire/…` (registration surface)

**Interface produced:** `TenantPurgeJob.RunAsync(CancellationToken ct)` — Hangfire entry point.

- [ ] **Step 1: Write failing unit tests.** Mock `ITenantDeletionRepository`, `IAuditLogRepository`, `IDatabaseTransactionProvider`, `IBillingApiClient`; `FakeTimeProvider`. Because the per-tenant teardown mixes DB ops and a billing call with a specific ordering and failure policy, assert the orchestration:

```csharp
// No due deletions -> no teardown, no billing call, no status update.
// One due deletion, all succeed: for the tenant, GetUserIdsWithAnyRoleInTenant is read, PurgeTenantOperationalData
//   runs, UserTenantRoles deleted, each user's active-role check runs, orphaned users masked (non-orphans NOT masked),
//   DeleteCustomer called, status set to Purged with PurgedAt=now, and a TenantId=null TenantPurged audit entry written.
// Orphan logic: user with a role only in the deleted tenant -> masked; user also active in another tenant -> not masked.
// Billing-failure isolation: DeleteCustomer returns false -> fleet teardown still ran, but status stays Deactivated
//   (NOT Purged) and no completion audit entry; the next tick retries.
// Idempotency/resume: a second RunAsync over the same still-Deactivated tenant re-runs the teardown without throwing.
// Multiple due deletions: each processed independently; one tenant's billing failure does not block another's purge.
// FakeTimeProvider supplies "now" for GetDueDeletions and PurgedAt.
```

- [ ] **Step 2: Run to confirm failure.**

- [ ] **Step 3: Implement the job.** Follow the `AlertConditionStateCleanupJob` shape (sealed class, `[DisableConcurrentExecution]`, `[AutomaticRetry(Attempts = 0)]` — the loop is its own retry via the recurring tick). The billing call is after the fleet-DB teardown; status flips to `Purged` only when billing also succeeds:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Services.Core.Jobs;

/// <summary>
/// Hangfire recurring job that hard-purges tenants whose 30-day grace has elapsed. Each teardown is
/// idempotent and resumable: every delete is scoped by tenant (already-empty is a no-op) and user
/// masking skips already-masked rows, so a job that dies mid-purge re-runs cleanly from the top.
/// A deletion is marked Purged only when the fleet teardown AND the billing customer-delete both
/// succeed; a billing failure leaves it Deactivated so the next tick retries the billing step.
/// </summary>
public sealed class TenantPurgeJob
{
    private readonly ITenantDeletionRepository _deletionRepo;
    private readonly IAuditLogRepository _auditLog;
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IBillingApiClient _billingApiClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantPurgeJob> _logger;

    /// <summary>Creates a new instance of the <see cref="TenantPurgeJob"/> class.</summary>
    public TenantPurgeJob(
        ITenantDeletionRepository deletionRepo,
        IAuditLogRepository auditLog,
        IDatabaseTransactionProvider transactionProvider,
        IBillingApiClient billingApiClient,
        TimeProvider timeProvider,
        ILogger<TenantPurgeJob> logger)
    {
        ArgumentNullException.ThrowIfNull(deletionRepo);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(billingApiClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _deletionRepo = deletionRepo;
        _auditLog = auditLog;
        _transactionProvider = transactionProvider;
        _billingApiClient = billingApiClient;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Purges every deletion whose scheduled purge time has passed.</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken ct)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<TenantDeletion> due = await _deletionRepo.GetDueDeletionsAsync(now, ct);
        if (due.Count == 0)
        {
            _logger.LogDebug("No tenant deletions due for purge");

            return;
        }

        foreach (TenantDeletion deletion in due)
        {
            try
            {
                await PurgeOneAsync(deletion, ct);
            }
            catch (Exception ex)
            {
                // One tenant's failure must not stop the others; the still-Deactivated row is retried next tick.
                _logger.LogError(ex, "Purge failed for tenant {TenantId} (deletion {DeletionId}); will retry",
                    deletion.TenantId, deletion.Id);
            }
        }
    }

    private async Task PurgeOneAsync(TenantDeletion deletion, CancellationToken ct)
    {
        int tenantId = deletion.TenantId;

        // Snapshot the membership BEFORE deleting roles so the orphan check has the full member set.
        List<int> memberIds = await _deletionRepo.GetUserIdsWithAnyRoleInTenantAsync(tenantId, ct);

        // Fleet-DB teardown in one transaction: operational data, then this tenant's roles, then mask orphans.
        using (IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct))
        {
            await _deletionRepo.PurgeTenantOperationalDataAsync(tenantId, ct);
            await _deletionRepo.DeleteUserTenantRolesForTenantAsync(tenantId, ct);

            foreach (int userId in memberIds)
            {
                bool activeElsewhere = await _deletionRepo.UserHasAnyActiveRoleAsync(userId, ct);
                if (TenantDeletionHandler.IsUserOrphanedByTenantRemoval(activeElsewhere))
                {
                    await _deletionRepo.MaskUserAsync(userId, ct);
                }
            }

            await transaction.CommitAsync(ct);
        }

        // Billing teardown AFTER the fleet commit. If it fails, leave the deletion Deactivated so the next
        // tick retries — nothing is marked complete until fleet AND billing both succeed.
        bool customerDeleted = await _billingApiClient.DeleteCustomerAsync(deletion.TenantExternalId, ct);
        if (customerDeleted == false)
        {
            _logger.LogWarning(
                "Fleet data purged for tenant {TenantId} but billing customer-delete failed; leaving Deactivated for retry",
                tenantId);

            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        await _deletionRepo.UpdateDeletionStatusAsync(deletion.Id, TenantDeletionStatus.Purged, now, ct);

        // Completion audit entry with TenantId = null so it survives any tenant-scoped query after the purge.
        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId: null,
            userId: null,
            machineId: null,
            AuditAction.TenantPurged,
            AuditResourceType.Tenant,
            tenantId.ToString(),
            new { deletion.TenantExternalId, deletion.TenantName, PurgedAt = now },
            ipAddress: null), ct);

        _logger.LogInformation("Tenant {TenantId} ({TenantName}) purged", tenantId, deletion.TenantName);
    }
}
```

> The teardown deliberately deletes operational data and roles inside the transaction, then masks orphans, then commits, then calls billing. Masking is inside the same transaction as the role deletion so `UserHasAnyActiveRoleAsync` sees the post-deletion role state for THIS tenant (its rows are already deleted in-transaction) while still counting other tenants' rows. Confirm `IDatabaseTransaction`/`BeginTransactionAsync` make in-transaction reads see the transaction's own writes (LinqToDB on the same connection does); the Task 15 integration test is the ground truth.

- [ ] **Step 4: Register the recurring job.** In `RecurringJobIds.cs` add `public const string TenantPurge = "tenant-purge";` and append it to the `All` list. In `RecurringJobRegistry.RegisterAll`, add an unconditional registration (it is not billing- or storage-gated; when billing is disabled the `NoOpBillingApiClient` returns success so purges still complete). Hourly per the spec:

```csharp
        recurringJobs.AddOrUpdate<TenantPurgeJob>(
            RecurringJobIds.TenantPurge,
            job => job.RunAsync(CancellationToken.None),
            "23 * * * *");
```

- [ ] **Step 5: Register the job type for DI** in `ServiceCollectionExtensions.cs` (Hangfire resolves job instances from the container — grep how `AlertConditionStateCleanupJob` is registered and mirror it, e.g. `services.AddScoped<TenantPurgeJob>();`).

- [ ] **Step 6: Update the Hangfire registration surface test.** The `hangfire` functional project has an audit test that locks `RecurringJobIds.All` / registered jobs (grep `RecurringJobIds.All` in `test/functional/hangfire`). Update its expected set to include `tenant-purge`.

- [ ] **Step 7: Run:** `dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*TenantPurgeJob*"` and `dotnet run --project test/functional/hangfire/functional.hangfire.csproj` — expect all pass.

---

## Task 10: `FleetAdminService` RPCs (vord)

**Files:**
- Modify: `vord/src/server/Endpoints/grpc/FleetAdminService.cs`
- Test: `vord/test/functional/grpc/Endpoints/Grpc/FleetAdminServiceTests.cs`, `vord/test/unit/server/Endpoints/Grpc/FleetAdminServiceTests.cs`

**Interface produced:** implementations of the three FleetAdmin RPCs from Task 1, delegating to `TenantDeletionHandler` and `ITenantDeletionRepository`.

- [ ] **Step 1: Write failing functional tests** in `functional/grpc/…FleetAdminServiceTests.cs` (this suite already exercises the internal-key auth + full gRPC pipeline; mirror an existing RPC test's setup):

```csharp
// RequestTenantDeletion: valid tenant -> Success=true, ScheduledPurgeAt ~ now+30d; deletion row created;
//   Tenants.IsActive=false; audit entry written. Second call for the same tenant -> Success=false (double-delete guard).
// RequestTenantDeletion unknown tenant -> Success=false / NotFound per handler mapping.
// RestoreTenant on a Deactivated tenant -> Success=true; Tenants.IsActive=true; status Restored.
// RestoreTenant with no pending deletion -> Success=false.
// ListTenantDeletions: includeCompleted=false returns only Deactivated; =true returns all; fields mapped
//   (tenant_external_id, tenant_name, status int, scheduled_purge_at, reason); pagination + total_count.
// Ingest-after-deactivation gate: after RequestTenantDeletion, IsIngestEligibleAsync(tenant) is false
//   (assert via the telemetry ingest path or the subscription service seam the suite already uses).
// Missing/invalid internal key -> the same auth rejection the other RPCs enforce.
```

- [ ] **Step 2: Run to confirm failure** (the methods are still the unimplemented abstract overrides from Task 1): `dotnet build machine-info.slnx` then the grpc functional project.

- [ ] **Step 3: Implement the RPCs** in `FleetAdminService.cs`, following the class's scope-per-call + `ValidateInternalKey` + `ResolveTenantByExternalIdAsync` conventions. Resolve the tenant to an internal id, then delegate:

```csharp
    /// <summary>Deactivates a tenant and schedules its purge (Phase 1 of deletion).</summary>
    public override async Task<RequestTenantDeletionResponse> RequestTenantDeletion(
        RequestTenantDeletionRequest request, ServerCallContext context)
    {
        ValidateInternalKey(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantRepository tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        TenantDeletionHandler handler = scope.ServiceProvider.GetRequiredService<TenantDeletionHandler>();

        Tenant tenant = await ResolveTenantByExternalIdAsync(tenantRepo, request.TenantExternalId, context.CancellationToken);

        TenantDeletionResult result = await handler.RequestDeletionAsync(
            tenant.Id, request.RequestedByUserId, request.Reason, context.CancellationToken);

        RequestTenantDeletionResponse response = new RequestTenantDeletionResponse
        {
            Success = result.Success,
            Message = result.Message,
        };
        if (result.ScheduledPurgeAt.HasValue)
        {
            response.ScheduledPurgeAt = Timestamp.FromDateTimeOffset(result.ScheduledPurgeAt.Value);
        }

        return response;
    }

    /// <summary>Restores a tenant during its deletion grace window.</summary>
    public override async Task<RestoreTenantResponse> RestoreTenant(
        RestoreTenantRequest request, ServerCallContext context)
    {
        ValidateInternalKey(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantRepository tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        TenantDeletionHandler handler = scope.ServiceProvider.GetRequiredService<TenantDeletionHandler>();

        Tenant tenant = await ResolveTenantByExternalIdAsync(tenantRepo, request.TenantExternalId, context.CancellationToken);

        TenantDeletionResult result = await handler.RestoreAsync(
            tenant.Id, request.RequestedByUserId, context.CancellationToken);

        return new RestoreTenantResponse { Success = result.Success, Message = result.Message };
    }

    /// <summary>Lists tenant deletions for the admin panel.</summary>
    public override async Task<ListTenantDeletionsResponse> ListTenantDeletions(
        ListTenantDeletionsRequest request, ServerCallContext context)
    {
        ValidateInternalKey(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantDeletionRepository deletionRepo = scope.ServiceProvider.GetRequiredService<ITenantDeletionRepository>();

        (int page, int pageSize) = SanitizePagination(request.Page, request.PageSize);
        (List<TenantDeletion> deletions, int totalCount) = await deletionRepo.ListDeletionsAsync(
            request.IncludeCompleted, (page - 1) * pageSize, pageSize, context.CancellationToken);

        ListTenantDeletionsResponse response = new ListTenantDeletionsResponse { TotalCount = totalCount };
        foreach (TenantDeletion d in deletions)
        {
            TenantDeletionRecord record = new TenantDeletionRecord
            {
                Id = d.Id,
                TenantId = d.TenantId,
                TenantExternalId = d.TenantExternalId,
                TenantName = d.TenantName,
                RequestedByUserId = d.RequestedByUserId,
                RequestedAt = Timestamp.FromDateTimeOffset(d.RequestedAt),
                ScheduledPurgeAt = Timestamp.FromDateTimeOffset(d.ScheduledPurgeAt),
                Status = (int)d.Status,
                Reason = d.Reason ?? string.Empty,
            };
            if (d.PurgedAt.HasValue)
            {
                record.PurgedAt = Timestamp.FromDateTimeOffset(d.PurgedAt.Value);
            }

            response.Deletions.Add(record);
        }

        return response;
    }
```

Add the `using Framlux.FleetManagement.Services.Core.Handlers;` directive (alphabetized).

- [ ] **Step 4: Build the whole solution** — the Task 1 "unimplemented abstract member" failure must now be gone: `dotnet build machine-info.slnx` (0 warnings).

- [ ] **Step 5: Run:** `dotnet run --project test/functional/grpc/functional.grpc.csproj` and `dotnet run --project test/unit/server/unit.server.csproj` — expect all pass.

---

## Task 11: billing-api immediate-cancel + delete-customer (vord-internal)

**Files:**
- Modify: `vord-internal/src/billing-api/Services/IStripeGateway.cs`
- Modify: `vord-internal/src/billing-api/Services/StripeGateway.cs`
- Modify: `vord-internal/src/billing-api/Services/BillingManagementService.cs`
- Test: `vord-internal/test/billing/Services/BillingManagementServiceTests.cs`

**Interfaces produced:** `IStripeGateway.CancelSubscriptionImmediatelyAsync`; `BillingManagement.CancelSubscriptionImmediate` + `DeleteCustomer` RPC implementations.

- [ ] **Step 1: Add the gateway method to `IStripeGateway`:**

```csharp
    /// <summary>
    /// Cancels a subscription immediately (not at period end), stopping billing right away.
    /// </summary>
    Task<Subscription> CancelSubscriptionImmediatelyAsync(string subscriptionId, CancellationToken ct);
```

- [ ] **Step 2: Implement it in `StripeGateway.cs`** using the Stripe SDK's subscription-cancel (mirror how the class constructs its `SubscriptionService` and passes `RequestOptions`/`ct` in the existing `UpdateSubscriptionAsync`):

```csharp
    /// <inheritdoc/>
    public async Task<Subscription> CancelSubscriptionImmediatelyAsync(string subscriptionId, CancellationToken ct)
    {
        return await _subscriptionService.CancelAsync(
            subscriptionId, new SubscriptionCancelOptions(), _requestOptions, ct);
    }
```

Match the field names actually used in `StripeGateway.cs` (`_subscriptionService`, `_requestOptions` — confirm by reading the file; adapt if the class news up services inline).

- [ ] **Step 3: Write failing service tests** in `BillingManagementServiceTests.cs`, reusing the file's `SeedCustomer` / `CreateService` / `CreateCallContext` helpers and mocked `IStripeGateway`:

```csharp
// CancelSubscriptionImmediate: known customer WITH a subscription -> gateway.CancelSubscriptionImmediatelyAsync
//   called with the stored SubscriptionId; Success=true.
// CancelSubscriptionImmediate: customer with NO subscription (Free) -> Success=true, gateway not called (nothing to cancel).
// CancelSubscriptionImmediate: no StripeCustomers row (Free/never-checked-out) -> Success=true, no-op.
// CancelSubscriptionImmediate: gateway throws StripeException -> Success=false, message set (does not surface a 500).
// DeleteCustomer: known customer -> gateway.DeleteCustomerAsync(CustomerId) called; StripeCustomers row removed;
//   PendingActions rows for the tenant removed; Success=true.
// DeleteCustomer: no StripeCustomers row -> Success=true, no-op (idempotent; supports Free tenants and retries).
// DeleteCustomer: gateway throws -> Success=false and the StripeCustomers row is NOT removed (so vord keeps the
//   deletion Deactivated and retries).
```

- [ ] **Step 4: Run to confirm failure.**

- [ ] **Step 5: Implement the two RPCs in `BillingManagementService.cs`** (mirror the existing `CancelSubscription` lookup-by-`TenantExternalId` pattern):

```csharp
    /// <inheritdoc/>
    public override async Task<CancelSubscriptionImmediateResponse> CancelSubscriptionImmediate(
        CancelSubscriptionImmediateRequest request, ServerCallContext context)
    {
        StripeCustomer? customer = await _db.StripeCustomers
            .FirstOrDefaultAsync(c => c.TenantExternalId == request.TenantExternalId, context.CancellationToken);

        // No customer or no subscription (e.g. a Free tenant) — nothing to cancel; report success so the
        // deletion flow proceeds.
        if ((customer is null) || string.IsNullOrEmpty(customer.SubscriptionId))
        {
            return new CancelSubscriptionImmediateResponse { Success = true, Message = "No active subscription to cancel" };
        }

        try
        {
            await _stripeGateway.CancelSubscriptionImmediatelyAsync(customer.SubscriptionId, context.CancellationToken);
            _logger.LogInformation("Immediately canceled subscription for tenant {TenantExternalId}", request.TenantExternalId);

            return new CancelSubscriptionImmediateResponse { Success = true, Message = "OK" };
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe immediate-cancel failed for tenant {TenantExternalId}", request.TenantExternalId);

            return new CancelSubscriptionImmediateResponse { Success = false, Message = ex.Message };
        }
    }

    /// <inheritdoc/>
    public override async Task<DeleteCustomerResponse> DeleteCustomer(
        DeleteCustomerRequest request, ServerCallContext context)
    {
        StripeCustomer? customer = await _db.StripeCustomers
            .FirstOrDefaultAsync(c => c.TenantExternalId == request.TenantExternalId, context.CancellationToken);

        // Idempotent / Free-tenant path: no customer to delete.
        if (customer is null)
        {
            return new DeleteCustomerResponse { Success = true, Message = "No billing customer to delete" };
        }

        try
        {
            await _stripeGateway.DeleteCustomerAsync(customer.CustomerId, context.CancellationToken);
        }
        catch (StripeException ex)
        {
            // Leave the local rows in place so vord keeps the deletion Deactivated and retries next tick.
            _logger.LogError(ex, "Stripe customer-delete failed for tenant {TenantExternalId}", request.TenantExternalId);

            return new DeleteCustomerResponse { Success = false, Message = ex.Message };
        }

        await _db.PendingActions
            .Where(p => p.TenantExternalId == request.TenantExternalId)
            .DeleteAsync(context.CancellationToken);
        await _db.StripeCustomers
            .Where(c => c.TenantExternalId == request.TenantExternalId)
            .DeleteAsync(context.CancellationToken);

        _logger.LogInformation("Deleted billing customer and local rows for tenant {TenantExternalId}", request.TenantExternalId);

        return new DeleteCustomerResponse { Success = true, Message = "OK" };
    }
```

> Confirm the `PendingActions` model's tenant key column name (`TenantExternalId`) and the `_db` / `_stripeGateway` / `_logger` field names by reading the top of `BillingManagementService.cs`; adapt the queries to match. If billing-api has an existing `BillingAuditLog` write pattern used by other admin mutations, add a mirroring audit entry for the customer deletion; if there is no such helper in this service, the authoritative deletion record is vord's `TenantPurged` audit entry (Task 9) — do not invent a new audit table here.

- [ ] **Step 6: Run:** `dotnet build vord-internal.slnx -c Release` (0 warnings) and `dotnet run --project test/billing/billing.csproj` — expect all pass.

---

## Task 12: billing-api FleetAdmin client + REST proxy endpoints (vord-internal)

**Files:**
- Modify: `vord-internal/src/billing-api/Services/IFleetAdminClient.cs`
- Modify: `vord-internal/src/billing-api/Services/FleetAdminClient.cs`
- Create: `vord-internal/src/billing-api/Endpoints/Admin/FleetRequestTenantDeletionEndpoint.cs`
- Create: `vord-internal/src/billing-api/Endpoints/Admin/FleetRestoreTenantEndpoint.cs`
- Create: `vord-internal/src/billing-api/Endpoints/Admin/FleetTenantDeletionsEndpoint.cs`
- Test: `vord-internal/test/billing/…` (endpoint/proxy tests, mirroring existing Fleet* endpoint tests)

**Interfaces produced:** three `IFleetAdminClient` passthroughs; three REST routes under `/admin/fleet/*` (policy `AdminPanel`, version 1).

- [ ] **Step 1: Add the client methods to `IFleetAdminClient`:**

```csharp
Task<RequestTenantDeletionResponse> RequestTenantDeletionAsync(string tenantExternalId, int requestedByUserId, string? reason, CancellationToken ct);
Task<RestoreTenantResponse> RestoreTenantAsync(string tenantExternalId, int requestedByUserId, CancellationToken ct);
Task<ListTenantDeletionsResponse> ListTenantDeletionsAsync(bool includeCompleted, int page, int pageSize, CancellationToken ct);
```

- [ ] **Step 2: Implement them in `FleetAdminClient.cs`** (mirror the existing `ListTenantsAsync` / `UpdateTenantSubscriptionAsync` gRPC-call pattern in that file — same deadline/error conventions).

- [ ] **Step 3: Write failing endpoint tests** mirroring the existing `FleetTenantsEndpoint` test (mock `IFleetAdminClient`; assert route, `AdminPanel` policy, response mapping, and the `RpcException` → 503/404 translation the Fleet endpoints share).

- [ ] **Step 4: Implement the three endpoints.** Follow `FleetTenantsEndpoint` (GET list) and `FleetUpdateSettingEndpoint` (POST mutation) exactly — DTOs co-located, `try/catch (RpcException …)` ladder for Unavailable/DeadlineExceeded → 503, NotFound → 404, else `ThrowError(ex.Status.Detail)`.
  - `POST /admin/fleet/tenants/{externalId}/delete` → `RequestTenantDeletionAsync` (body: `reason`; `requestedByUserId` from the authenticated admin — pull it from the same claim the other admin endpoints use, grep how `RequestedByUserId`/admin id is sourced in `SubscriptionCreateEndpoint`/`OverrideEndpoint`).
  - `POST /admin/fleet/tenants/{externalId}/restore` → `RestoreTenantAsync`.
  - `GET /admin/fleet/deletions?includeCompleted=&page=&pageSize=` → `ListTenantDeletionsAsync`, mapping `TenantDeletionRecord` → a `FleetTenantDeletionDto` (ISO-8601 timestamps via `?.ToDateTime().ToString("o")`, `status` mapped to a string `deactivated|purged|restored`).

- [ ] **Step 5: Add the AdminApi client methods** in `vord-internal/src/admin/src/lib/api.ts` (mirror `updateFleetSetting` for POST and `getFleetTenants` for GET):

```typescript
async requestTenantDeletion(externalId: string, reason: string): Promise<{ success: boolean; message: string; scheduledPurgeAt: string }> {
    return this.post(`/api/v1/admin/fleet/tenants/${encodeURIComponent(externalId)}/delete`, { reason });
}
async restoreTenant(externalId: string): Promise<{ success: boolean; message: string }> {
    return this.post(`/api/v1/admin/fleet/tenants/${encodeURIComponent(externalId)}/restore`, {});
}
async getTenantDetail(externalId: string): Promise<FleetTenantDetail> {
    return this.get(`/api/v1/admin/fleet/tenants/${encodeURIComponent(externalId)}`);
}
async getTenantDeletions(params?: { includeCompleted?: boolean; page?: number; pageSize?: number }): Promise<FleetTenantDeletionsResponse> {
    const qs = new URLSearchParams();
    if (params?.includeCompleted) qs.set('includeCompleted', 'true');
    if (params?.page) qs.set('page', String(params.page));
    if (params?.pageSize) qs.set('pageSize', String(params.pageSize));
    return this.get(`/api/v1/admin/fleet/deletions?${qs}`);
}
```

Add the `FleetTenantDeletion` / `FleetTenantDeletionsResponse` / `FleetTenantDetail` types to `src/admin/src/lib/types.ts`. (`getTenantDetail` reuses the existing `GetTenantDetail` FleetAdmin RPC — if billing-api has no `/admin/fleet/tenants/{id}` GET yet, add a thin proxy endpoint for it here too, mirroring `FleetTenantsEndpoint`.)

- [ ] **Step 6: Run:** `dotnet build vord-internal.slnx -c Release` (0 warnings), `dotnet run --project test/billing/billing.csproj` — expect all pass.

---

## Task 13: Admin SPA — tenant detail delete action + deletions list/restore (vord-internal)

**Files:**
- Create: `vord-internal/src/admin/src/routes/fleet/tenants/[externalId]/+page.server.ts`
- Create: `vord-internal/src/admin/src/routes/fleet/tenants/[externalId]/+page.svelte`
- Create: `vord-internal/src/admin/src/routes/fleet/deletions/+page.server.ts`
- Create: `vord-internal/src/admin/src/routes/fleet/deletions/+page.svelte`
- Modify: `vord-internal/src/admin/src/routes/fleet/tenants/+page.svelte` (link each row to its detail page)
- Modify: `vord-internal/src/admin/src/routes/fleet/+layout.svelte` (add a "Deletions" nav item)
- Test: `vord-internal/src/admin/…` Vitest specs for both new pages

**Follow the established SPA patterns:** `+page.server.ts` `load` builds `new AdminApi(env.BILLING_API_URL ?? '', locals.admin!.accessToken, fetch)`; mutations use SvelteKit form `actions` returning `fail(...)` on error (mirror `fleet/settings/+page.server.ts`); SvelteKit `fetch` only; Svelte 5 runes (`$props`, `$state`, `$derived`); Skeleton v3 dark-mode selector.

- [ ] **Step 1: Write failing Vitest specs** for the two pages (mirror the existing fleet-page specs — grep `*.test.ts`/`*.spec.ts` under `src/admin/src/routes/fleet`). Assert:

```
// Tenant detail: renders tenant name/tier/counts; shows a "Delete tenant" control gated behind a
//   confirmation (typed org-name or explicit confirm) with a reason field; when the tenant already has a
//   pending deletion, the delete control is replaced by a "Deletion scheduled for <date>" notice.
// Deletions list: renders rows (tenant, requested-by, requested-at, scheduled-purge-at, status);
//   a "Restore" button appears only for Deactivated rows and is absent for Purged/Restored;
//   an includeCompleted toggle re-queries.
```

- [ ] **Step 2: Run to confirm failure:** `pnpm -C src/admin test`.

- [ ] **Step 3: Implement `fleet/tenants/[externalId]/+page.server.ts`** — `load` calls `api.getTenantDetail(params.externalId)` and (to show a pending-deletion banner) `api.getTenantDeletions({ includeCompleted: true })` filtered to this tenant, returning `{ tenant, pendingDeletion }`. Add two form `actions`: `delete` (reads `reason`, calls `api.requestTenantDeletion`, `fail(422,…)` on `success===false`, else `redirect` to `/fleet/deletions`) and nothing else. Guard destructive intent with a required confirmation field validated server-side.

- [ ] **Step 4: Implement `fleet/tenants/[externalId]/+page.svelte`** — tenant summary + a "Danger zone" card with the delete form (`method="POST" action="?/delete"`, a `reason` textarea, and a confirm input that must equal the org name before the submit button enables, using `$derived`). When `data.pendingDeletion` is set, render the scheduled-purge notice instead of the form.

- [ ] **Step 5: Implement `fleet/deletions/+page.server.ts`** — `load` reads the `includeCompleted` query param and calls `api.getTenantDeletions({ includeCompleted, page, pageSize })`; a `restore` form action calls `api.restoreTenant(externalId)` and returns `fail`/success.

- [ ] **Step 6: Implement `fleet/deletions/+page.svelte`** — a table of deletions; a `Restore` form button (`action="?/restore"`, hidden `externalId`) shown only when `status === 'deactivated'`; an `includeCompleted` toggle that `goto`s with the query param. Match the visual language of `fleet/tenants/+page.svelte` (same table classes, `ink-*`/`accent-*` tokens).

- [ ] **Step 7: Wire navigation** — in `fleet/tenants/+page.svelte`, make each tenant row link to `/fleet/tenants/{externalId}`. In `fleet/+layout.svelte`, add a "Deletions" nav entry pointing at `/fleet/deletions`.

- [ ] **Step 8: Run:** `pnpm -C src/admin test`, `pnpm -C src/admin check`, `pnpm -C src/admin build` — expect all pass/clean.

---

## Task 14: End-to-end purge integration test (vord, real Postgres — mandatory gate)

**Files:**
- Create: `vord/test/integration/TenantPurgeIntegrationTests.cs`

This is the correctness gate for the whole teardown against the real partitioned schema. It needs Docker/Podman (Testcontainers) — apply the Podman `DOCKER_HOST` env from CLAUDE.md on macOS.

- [ ] **Step 1: Write the test.** Using the integration project's Postgres fixture + real `DatabaseRepository`/migrations:

```csharp
// Seed tenant A with at least one row in EVERY tenant-scoped table (including the partitioned ones:
//   MachineTelemetry, AlertEvents, RemoteCommands) and the child tables (MachineStateDetail/Summary,
//   AlertRuleMachines, AlertConditionStates, IntegrationDeliveryAttempts, AlertEmailDeliveryAttempts,
//   MachineAuthorizedKeys, TenantOidcConfigurations, TenantInvitations, TenantSubscriptions,
//   TenantSubscriptionOverrides, DataExportJobs, UserSigningKeys, RegistrationTokens, AlertRules,
//   IntegrationEndpoints, Machines, UserTenantRoles). Seed an AuditLog entry for tenant A.
// Seed tenant B sharing user U (U has an active role in BOTH A and B). Seed a user V active only in A.
// Run TenantPurgeJob.RunAsync with a FakeTimeProvider set past A's ScheduledPurgeAt (insert A's deletion
//   row Deactivated via the handler/repo first). Use a stub IBillingApiClient returning true.
// Assert: every tenant-scoped table has ZERO rows for tenant A (query each table by TenantId or via the
//   child->parent join). Tenants(A) row still exists with IsActive=false. AuditLog(A) entry still exists.
//   A new TenantPurged audit entry with TenantId=null exists. The deletion row is Purged with PurgedAt set.
// Assert user V (orphaned) is masked: Email/Username tombstoned, ExternalId namespaced tombstone, Id unchanged.
// Assert user U is UNTOUCHED (still active in B; PII intact) and ALL of tenant B's data is intact.
// Assert idempotency: a second RunAsync (deletion now Purged, so not due) is a no-op and nothing changes;
//   AND a direct second PurgeTenantOperationalDataAsync(A) does not throw and leaves counts at zero.
```

- [ ] **Step 2: Run:** `dotnet run --project test/integration/integration.csproj --treenode-filter "*TenantPurge*"` — expect all pass. If any tenant-scoped table still has tenant-A rows, the purge list in Task 4 is incomplete — add the missing table there and re-run (this test is the completeness backstop for the teardown set).

---

## Task 15: Full-suite verification + completion summary

- [ ] **Step 1: vord full build + all suites:**
```bash
dotnet build machine-info.slnx            # 0 warnings
dotnet run --project test/unit/database/unit.database.csproj
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/hangfire/functional.hangfire.csproj
dotnet run --project test/integration/integration.csproj
```
- [ ] **Step 2: vord-internal full build + suites:**
```bash
dotnet build vord-internal.slnx -c Release   # 0 warnings
dotnet run --project test/billing/billing.csproj
pnpm -C src/admin test && pnpm -C src/admin check && pnpm -C src/admin build
```
- [ ] **Step 3: Coverage check** on the new vord code (handler, job, repository, subscription-service change) via coverlet per CLAUDE.md — confirm ≥80% line/branch for the new units.
- [ ] **Step 4: Completion summary** listing: changed files per repo, validation output, the package handoff (billing-api reviewed tree publishes the real `Framlux.Vord.BillingGrpc` 1.16.0; vord's `nuget.config` local-source entry must be stripped before commit), the Task 7 login-block design choice (flag for review), and any residual noted in the spec (audit-text PII retained by design). Do not commit or push — leave everything in the working tree for Jonathan.

---

## Self-Review

**Spec coverage:**
- Two-phase deactivate-then-purge, fixed 30-day grace → Tasks 5 (`ComputeScheduledPurge` = +30d, no override) + 9. ✓
- Restore available full 30 days, blocked after purge → Task 5 (`RestoreAsync`, Purged guard). ✓
- Keep identity rows, erase everything else; mask orphaned users; keep Tenants/AuditLog → Tasks 4 (`PurgeTenantOperationalDataAsync`, `MaskUserAsync`) + 9 + 14. ✓
- Users global, only this tenant's membership removed; zero-active-role → mask → Tasks 4/5 (`UserHasAnyActiveRoleAsync`, `IsUserOrphanedByTenantRemoval`) + 9. ✓
- Billing: immediate cancel at deactivation, customer delete at purge → Tasks 8/11 (client + RPCs), 5 (after-commit cancel), 9 (delete at purge). ✓
- Home = internal admin SPA via FleetAdminService gRPC + billing-api proxy → Tasks 10/12/13. ✓
- `TenantDeletions` table (all columns, never purged, unique guard) → Task 2. ✓
- Phase-1 transaction (guard, insert, deactivate, audit) + after-commit cancel → Task 5. ✓
- Ingest gate requires active; web login/session blocked → Tasks 6 + 7. ✓
- Phase-2 idempotent ordered teardown following Down() order; partitioned deletes; TenantId=null completion audit → Tasks 4/9/14. ✓
- Error handling: idempotent/resumable, billing-failure isolation, partial-failure visibility, double-delete guard → Tasks 2 (unique index), 4 (idempotent deletes), 5 (guard), 9 (billing-failure keeps Deactivated). ✓
- Testing matrix (unit/integration/functional-grpc/billing-api/vitest) → Tasks 3–14. ✓
- YAGNI boundaries (no auto-export, no grace override, no self-serve, no audit scrub) — nothing in the plan adds these. ✓
- Execution note (build on production-readiness) → Global Constraints. ✓

**Type consistency:** `TenantDeletionStatus{Deactivated=1,Purged=2,Restored=3}` used identically in the model, migration unique index (`Status <> 3`), repository queries, handler, and job. `TenantDeletionResult` record shape matches every caller. `IBillingApiClient.CancelSubscriptionImmediateAsync`/`DeleteCustomerAsync` names match across interface, impl, no-op, handler, and job. Proto message/field names in Task 1 match their C# usages in Tasks 8/10/12. Repository method names (`GetActiveDeletionForTenantAsync`, `GetDueDeletionsAsync`, `PurgeTenantOperationalDataAsync`, `MaskUserAsync`, `UserHasAnyActiveRoleAsync`, `GetUserIdsWithAnyRoleInTenantAsync`, `SetTenantActiveAsync`) are consistent between the interface (Tasks 3/4) and consumers (Tasks 5/9).

**Verification-required flags baked into the tasks** (not placeholders — explicit "confirm X before implementing" steps): exact `DatabaseContext` plural property names (Task 4), `InsertWithInt32IdentityAsync` helper name (Task 3), `UserAccount` PII column set (Task 4), `StripeGateway`/`BillingManagementService` field + `PendingActions` key names (Task 11), admin-id claim source (Task 12), and the `IRoleCacheInvalidator` wiring (Task 7). These are grep/LSP confirmations against real symbols, done at implementation time.
