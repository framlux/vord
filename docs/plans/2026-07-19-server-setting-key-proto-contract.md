# Server Setting Key Proto Contract Implementation Plan

> **For agentic workers:** Execute task-by-task with build + test verification after each task.
> **Never `git commit` or push in either repo** — leave all changes in the working tree; Jonathan reviews and commits.
> Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the gRPC contract (`BillingService.proto`) the single authoritative definition of
server-setting keys. Today the wire carries a bare `int32 key` and every consumer re-declares the
key list: the C# enum in vord (`ServerConfigurationSettingKeys`), a hardcoded `const 8` in
vord-internal's `PublicSettingsCache`, and a hand-mirrored `KEYS` map in the admin Svelte UI
(which has already drifted once — it carried dead keys 4/5 until 2026-07-19).

**Architecture:** Add a proto enum `ServerSettingKey` to the contract; both repos compile against
the generated type from the shared `Framlux.Vord.BillingGrpc` NuGet package. The vord **database
enum stays** as the storage/domain type — LinqToDB models must not depend on a wire-contract
package — with an explicit cast at the gRPC boundary and a **parity test** in vord asserting the
two enums match member-for-member. That parity test is the cross-repo drift guard: vord compiles
against the published package, so either side drifting fails vord's build or tests.

**Decisions already made (Jonathan, 2026-07-19):**
- Ids 4, 5, 13 are **NOT reserved** — they are free for future reuse (breaking changes are
  acceptable pre-production; the DB will be recreated). Do not add `reserved` statements.
- Existing keys keep their current ids (1,2,3,6,7,8,9,10,11,12,14). Do NOT renumber to close the
  gaps — the gaps are simply available for the next new settings.
- Migrations are frozen explicit snapshots; nothing in this plan changes `InitialMigration`.

**Repos:**
- `~/Repositories/framlux/vord-internal` — owns `src/billingGrpc/protos/BillingService.proto`,
  publishes the `Framlux.Vord.BillingGrpc` NuGet package (currently 1.12.1), and contains the
  gRPC client (`billing-api`) + admin Svelte UI.
- `~/Repositories/framlux/vord` — consumes the package (`src/services.core/services.core.csproj`,
  `PackageReference Framlux.Vord.BillingGrpc`), implements the service
  (`src/server/Endpoints/grpc/FleetAdminService.cs`), owns the domain enum
  (`src/database/Enums/ServerConfigurationSettingKeys.cs`).

**Package coordination:** vord's changes need the regenerated enum before the real package is
published. Use a local pack: `dotnet pack` billingGrpc at version `1.13.0-local`, add a local
folder source for the vord build, and set vord's reference to the final version `1.13.0`.
Jonathan publishes the real 1.13.0 from the reviewed vord-internal tree; the local-source entry
must NOT remain in vord's committed nuget.config (flag it in the completion summary).

---

## Task 1: Proto enum (vord-internal)

**File:** `src/billingGrpc/protos/BillingService.proto`

- [ ] **Step 1:** Add the enum next to the server-setting messages (~line 236). Values mirror
  `vord/src/database/Enums/ServerConfigurationSettingKeys.cs` exactly. proto3 enum members share
  the enclosing scope, hence the prefix; C# codegen strips it back to PascalCase:

```proto
// Server configuration setting identifiers. Values match the fleet server's storage enum;
// vord's ServerSettingKeyParityTests asserts the two stay identical. Ids 4, 5, and 13 belonged
// to retired settings and are available for reuse.
enum ServerSettingKey {
  SERVER_SETTING_KEY_NONE = 0;
  SERVER_SETTING_KEY_AGENT_HEARTBEAT_SECONDS = 1;
  SERVER_SETTING_KEY_AGENT_CONFIG_REFRESH_SECONDS = 2;
  SERVER_SETTING_KEY_ONLINE_THRESHOLD_SECONDS = 3;
  SERVER_SETTING_KEY_DEDUPLICATION_TTL_SECONDS = 6;
  SERVER_SETTING_KEY_AGENT_COMMAND_POLL_SECONDS = 7;
  SERVER_SETTING_KEY_ALLOW_USER_SIGNUP = 8;
  SERVER_SETTING_KEY_TELEMETRY_COLLECT_FAST_SECONDS = 9;
  SERVER_SETTING_KEY_TELEMETRY_COLLECT_SLOW_SECONDS = 10;
  SERVER_SETTING_KEY_TELEMETRY_SEND_FAST_SECONDS = 11;
  SERVER_SETTING_KEY_TELEMETRY_SEND_SLOW_SECONDS = 12;
  SERVER_SETTING_KEY_SERVICE_STATUS_SECONDS = 14;
}
```

- [ ] **Step 2:** Change `ServerSetting.key` (field 1, ~line 237) and
  `UpdateServerSettingRequest.key` (field 1, ~line 303) from `int32` to `ServerSettingKey`.
  (Enums are varint on the wire like int32, so this is binary-compatible; it is a source-level
  breaking change for generated code, which is the point.)
- [ ] **Step 3:** Bump the billingGrpc package version to `1.13.0` (find the `<Version>` /
  `<PackageVersion>` property in `src/billingGrpc/*.csproj` or a shared props file).
- [ ] **Step 4:** `dotnet build` the vord-internal solution (`vord-internal.slnx`). Expect
  compile errors in `billing-api` where `int key` meets the new enum — fixed in Task 2. Build
  billingGrpc project alone first to confirm codegen: `dotnet build src/billingGrpc`.

## Task 2: Migrate the vord-internal client code

**Files:** `src/billing-api/Services/FleetAdminClient.cs`, `Services/IFleetAdminClient.cs`,
`Services/PublicSettingsCache.cs`, `Endpoints/Admin/FleetUpdateSettingEndpoint.cs`,
`Endpoints/Admin/FleetSettingsEndpoint.cs`, plus their tests under `test/billing/`.

- [ ] **Step 1:** `PublicSettingsCache`: delete `private const int AllowUserSignupKey = 8;` and
  use `(int)ServerSettingKey.AllowUserSignup` (or compare enums directly where the fetched
  `ServerSetting.Key` is now typed). Keep the fail-closed `allowUserSignup=false` default —
  it is deliberate.
- [ ] **Step 2:** `IFleetAdminClient`/`FleetAdminClient.UpdateServerSettingAsync(int key, ...)`:
  keep the `int` at the REST-facing seam (the admin UI posts JSON ints) and cast once inside the
  client: `Key = (ServerSettingKey)key`. Unknown ints cast fine; the fleet server's validation
  rejects them with InvalidArgument, same as today.
- [ ] **Step 3:** `FleetSettingsEndpoint` (list path): `ServerSetting.Key` is now the enum —
  the REST DTO keeps exposing the int (`(int)setting.Key`), `key_name` string continues to flow
  through unchanged.
- [ ] **Step 4:** Fix test mocks in `test/billing/` (`FleetSettingsEndpointTests`,
  `FleetAdminClientTests`) — mechanical: ints become enum members in `UpdateServerSettingRequest`
  / `ServerSetting` constructions.
- [ ] **Step 5:** Verify: full vord-internal build 0 warnings + `dotnet run` the test project(s)
  under `test/` (check csproj layout; TUnit-style executables like vord). Also
  `pnpm -C src/admin check` (the Svelte UI needs no change — it keeps its local `KEYS` map for
  presentation grouping; ints on the wire are unchanged).

## Task 3: Local package handoff

- [ ] **Step 1:** `dotnet pack src/billingGrpc -c Release -o /tmp/billinggrpc-local` (version
  1.13.0 from Task 1 Step 3).
- [ ] **Step 2:** In vord, add the local folder as a package source for the build. Prefer a CLI
  source add or an **uncommitted** `nuget.config` edit; either way, record it in the completion
  summary so Jonathan strips it when publishing the real package:
  `dotnet nuget add source /tmp/billinggrpc-local --name billinggrpc-local --configfile nuget.config`
  (create/locate vord's nuget.config first; if vord has none, create it minimally).
- [ ] **Step 3:** Bump `src/services.core/services.core.csproj`:
  `Framlux.Vord.BillingGrpc` `1.12.1` → `1.13.0`. `dotnet restore` must resolve from the local
  source.

## Task 4: vord service boundary + parity test (TDD)

**Files:** `src/server/Endpoints/grpc/FleetAdminService.cs`,
`test/unit/server/Endpoints/Grpc/ServerSettingKeyParityTests.cs` (create),
`test/unit/server/Endpoints/Grpc/FleetAdminServiceTests.cs`,
`test/functional/grpc/Endpoints/Grpc/FleetAdminServiceTests.cs`.

- [ ] **Step 1 (failing test first):** Create `ServerSettingKeyParityTests` asserting the domain
  and proto enums match 1:1. Normalization: proto C# codegen yields PascalCase members with the
  `SERVER_SETTING_KEY_` prefix stripped (verify actual generated names first —
  `ServerSettingKey.None`, `.AgentHeartbeatSeconds`, …; `TTL` may generate as `Ttl`, so compare
  case-insensitively):

```csharp
[Test]
public async Task DomainAndContractSettingKeys_MatchMemberForMember()
{
    Dictionary<string, int> domain = Enum.GetValues<ServerConfigurationSettingKeys>()
        .ToDictionary(k => k.ToString().ToLowerInvariant(), k => (int)k);
    Dictionary<string, int> contract = Enum.GetValues<ServerSettingKey>()
        .ToDictionary(k => k.ToString().ToLowerInvariant(), k => (int)k);

    await Assert.That(contract).IsEquivalentTo(domain);
}
```

  (Adjust the equivalence assertion to whatever TUnit supports for dictionaries — asserting
  sorted key lists and per-name values separately is fine. `None` maps to `None`.)
- [ ] **Step 2:** `FleetAdminService.UpdateServerSetting`: `request.Key` is now
  `ServerSettingKey`; convert with `(ServerConfigurationSettingKeys)(int)request.Key` — keep the
  existing `ServerSettingValidation.Validate` call as the single rejection point (unknown enum
  values still arrive as unknown ints and must still produce InvalidArgument; add/keep a unit
  test casting an undefined int to the proto enum to prove it).
- [ ] **Step 3:** `GetServerSettings` response population: set the enum field
  (`Key = (ServerSettingKey)(int)entry.Key` or from the SettingEntry int).
- [ ] **Step 4:** Fix compile fallout in both FleetAdminServiceTests files (unit + functional):
  request construction `Key = (int)X` becomes `Key = (ServerSettingKey)(int)X` or the direct
  generated member. The functional tests exercise the real wire — they are the proof the contract
  change round-trips.
- [ ] **Step 5:** Run:
```bash
dotnet build machine-info.slnx                       # 0 warnings
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
dotnet run --project test/functional/web/functional.web.csproj
```

## Task 5: Full verification + summary

- [ ] **Step 1:** vord: remaining suites (`unit.services.core`, `unit.database`,
  `functional.hangfire`) — all green, 0 warnings.
- [ ] **Step 2:** vord-internal: full build + tests + `pnpm -C src/admin check` green.
- [ ] **Step 3:** Completion summary must list: (a) the local package source that Jonathan must
  remove/replace when he publishes the real `Framlux.Vord.BillingGrpc 1.13.0`; (b) confirmation
  that no `reserved` statements were added (ids 4/5/13 free); (c) any enum-name normalization
  quirks found in codegen (e.g. `Ttl` vs `TTL`).

## Exit criteria

1. `BillingService.proto` defines `ServerSettingKey`; `ServerSetting.key` and
   `UpdateServerSettingRequest.key` are typed with it. No `reserved` statements.
2. `ServerSettingKeyParityTests` passes and fails if either enum gains/loses/renumbers a member.
3. No consumer re-declares key ints: `PublicSettingsCache` const 8 gone; vord maps via casts at
   the boundary only. (The Svelte `KEYS` map stays — presentation grouping, documented as such.)
4. Both repos: builds 0 errors / 0 warnings, all test suites green.
5. Working trees left uncommitted in both repos; local NuGet source handoff documented.
