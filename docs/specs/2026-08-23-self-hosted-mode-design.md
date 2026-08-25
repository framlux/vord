# Self-Hosted Mode — Design

**Date:** 2026-08-23
**Status:** Approved, ready for implementation plan

## Problem

Vord is meant to run two ways: as a SaaS operated by Framlux, and self-hosted by third parties.
Today nothing in the system says which one it is. The mode is *inferred* from three unrelated
configuration values that are set independently and can disagree with each other:

| Inferred signal | Location | What it silently decides |
| --- | --- | --- |
| `Billing:Enabled` | `services.core/Extensions/ServiceCollectionExtensions.cs:329` | Real `BillingApiClient` vs `NoOpBillingApiClient`; whether `StripeSyncJob` is registered; whether `BillingGatewayService` / `FleetAdminService` are mapped (`server/Program.cs:476`); whether `PUT /admin/settings` returns 404 |
| `PUBLIC_BILLING_URL` (web container env) | `web/src/routes/(app)/settings/billing/+page.server.ts:43`, `web/src/routes/(admin)/admin/+page.server.ts:13` | Whether the billing page and the admin "settings" tab render |
| `Resend:ApiKey` being empty | `ResendOptionsValidator`, `services.core/Services/Notifications/ResendEmailService.cs:39` | Whether email sends or returns `EmailDeliveryOutcome.Skipped` |

Three consequences:

1. **The web and the API can disagree.** `PUBLIC_BILLING_URL` is set on the `web` container;
   `Billing:Enabled` is set on `api-server`. Nothing reconciles them. A deployment where one is set
   and the other is not produces a UI offering billing against a server that has no billing client,
   or a server with a live Stripe integration whose UI hides it.
2. **"No email configured" is overloaded.** An empty `Resend:ApiKey` currently means *both* "this is
   a self-hosted install" and "email is off". A SaaS deployment that loses its API key through a
   config mistake looks exactly like a supported self-hosted deployment, and silently stops sending
   invitations.
3. **Self-hosted is functionally unusable.** `OnboardingHandler.cs:117` seeds every tenant at
   `SubscriptionTier.Free`, and only billing can move a tenant off Free. Free is 3 machines, 1-day
   retention, **0 alert rules, 0 webhooks, 1 member**. A self-hoster therefore cannot create an
   alert rule, add a webhook, or invite a second person — ever. This is the largest gap between the
   product as shipped and the product as intended.

## Goals

- One explicit flag that states which mode the deployment is in.
- Every SaaS-only subsystem keys off that flag alone, with no second switch that can drift.
- Self-hosted deployments get the full feature set (alerts, email, webhooks, SSO, members) with no
  tier gating.
- Self-hosted deployments can never reach billing or the internal control plane, structurally
  rather than by configuration discipline.
- Email becomes a provider chosen by mode, with the transport separated from the message content.

## Non-goals

- Changing what the SaaS deployment does. SaaS behaviour is preserved exactly.
- Introducing a general-purpose feature-flag or entitlement framework.
- Changing the `TenantSubscription` schema. `InitialMigration` is frozen (production shipped
  2026-08-04) and this design requires no new migration.
- Any change to the Go agent.

## Configuration categorization

| Section | Category | Disposition |
| --- | --- | --- |
| `Database`, `Redis`, `Hangfire`, `Streaming`, `Telemetry`, `KestrelLimits`, `ForwardedHeaders`, `Cors`, `Auth`, `App` | Both | Unchanged. |
| `Authentication` (GitHub / Google / Microsoft OAuth) | Both | Unchanged. A self-hoster registers their own OAuth applications. |
| `ObjectStorage` | Both, optional | Unchanged. Empty `BucketName` still selects `NoOpObjectStorageService` and hides data export. Documented as self-hostable (any S3-compatible endpoint). |
| `TierDefaults` | SaaS-only | Still bound so SaaS keeps its knobs. Ignored in self-hosted, where limits are unlimited. |
| `Billing` | SaaS-only | `Billing:Enabled` **removed**. `GrpcUrl` and the client-certificate paths are required in SaaS, ignored with a warning in self-hosted. |
| `InternalGrpc` | SaaS-only | The mutual-TLS control-plane listener. Never bound in self-hosted. |
| `Resend` | Replaced | Becomes `Email` — see "Email". |
| `Deployment` | New | `Deployment:SelfHosted`, boolean, defaults `true`. |
| `PUBLIC_BILLING_URL` (web env) | SaaS-only | Demoted from a mode signal to what its name says: the billing-web base URL. It no longer decides whether anything renders. |

## Design

### 1. The flag

```csharp
// src/services.core/Options/DeploymentOptions.cs
public sealed class DeploymentOptions
{
    public bool SelfHosted { get; set; } = true;
}
```

Bound from the `Deployment` section in `AddCoreOptions`, so both `api-server` and
`services-worker` pick it up from the one place.

**The default is `true` deliberately.** CLAUDE.md states this repository must build and run for
someone who clones it with nothing configured. Defaulting to self-hosted means a fresh clone works;
the SaaS deployment is the one that opts in explicitly, and it is the deployment with an operator
watching it.

A `DeploymentMode` singleton is the read surface, replacing `BillingStatus` (which is deleted):

```csharp
// src/services.core/Services/Deployment/DeploymentMode.cs
public sealed class DeploymentMode
{
    public bool IsSelfHosted { get; }
    public bool IsSaas => IsSelfHosted == false;
}
```

`BillingStatus.IsEnabled` is renamed rather than reinterpreted: every current consumer
(`UpdateAdminSettingsEndpoint`, `BillingEndpointGuards`, `CancelSubscriptionEndpoint`,
`DowngradeSubscriptionEndpoint`, `ReactivateSubscriptionEndpoint`, `ResumeSubscriptionEndpoint`)
was already asking "is this the SaaS deployment?" through a billing-shaped proxy. The rename makes
the question it was actually asking visible.

### 2. Startup validation

`DeploymentOptionsValidator` (an `IValidateOptions<DeploymentOptions>`, `ValidateOnStart`) fails
startup when:

- `SelfHosted == false` and `Billing:GrpcUrl` is empty — SaaS without a reachable billing API is
  not a runnable configuration, and the failure would otherwise surface as a gRPC dial error on the
  first checkout.
- `SelfHosted == true` and `InternalGrpc:Enabled == true` — the mutual-TLS control-plane port
  exists only to serve `BillingGateway` and `FleetAdmin`, neither of which is mapped in
  self-hosted. An enabled-but-unreachable listener is a misconfiguration worth surfacing loudly.

A populated `Billing` section under `SelfHosted == true` logs a warning and is ignored. It is
deliberately **not** a hard failure: flipping modes to test should not require gutting the config
file, and the values are inert because nothing reads them in that mode.

`BillingOptionsValidator` loses its `Enabled` branch and validates only the certificate/key pairing,
and only in SaaS mode.

### 3. Removing `Billing:Enabled`

Every current read of `billingOpts.Enabled` becomes a read of `DeploymentMode.IsSaas`:

- `services.core/Extensions/ServiceCollectionExtensions.cs:329` — real billing gRPC client and
  `BillingWebhookHandler` in SaaS; `NoOpBillingApiClient` in self-hosted.
- `ServiceCollectionExtensions.AddHangfireJobTypes(billingEnabled:)` and
  `AddBackgroundWorkers(billingOpts, …)` — parameter becomes `bool isSaas`. `StripeSyncJob` is not
  registered in self-hosted.
- `RecurringJobRegistry.RegisterAll(recurringJobs, billingEnabled, objectStorageEnabled)` — same
  rename; the Stripe sync recurring job is not scheduled in self-hosted.
- `server/Program.cs:105` — `internalGrpcListenerEnabled` becomes `IsSaas && internalGrpcOpts.Enabled`.
- `server/Program.cs:476` — `BillingGatewayService` and `FleetAdminService` are mapped only in SaaS.

Self-hosted therefore always gets the no-op billing client, and the internal control-plane services
are not merely unauthorized — they are not routed at all.

**Accepted trade-off:** with `Billing:Enabled` gone, `api-server` cannot run in SaaS mode without
`billing-api` reachable. Local development against the SaaS path uses `Deployment:SelfHosted=true`.

### 4. Entitlements in self-hosted

"Everything unlocked" is not a single branch, and it is not only about tier. Entitlement reaches
callers through three different shapes on `ISubscriptionService`: the subscription object itself,
the `Can*Async` predicates, and the retention-days accessors. All three must be handled or the
self-hoster is still blocked.

**Tier-keyed gates** (all read `GetSubscriptionForTenantAsync`):

| Site | Check |
| --- | --- |
| `server/Endpoints/Web/Alerts/AlertRuleCreateEndpoint.cs:69` | `Tier != Team` |
| `server/Endpoints/Web/Alerts/AlertRuleUpdateEndpoint.cs:124` | `Tier != Team` (custom rules) |
| `server/Endpoints/Web/Machines/MachineAuthorizedKeyAddEndpoint.cs:62` | `Tier != Team` |
| `server/Endpoints/Web/Commands/CommandSendEndpoint.cs:49` | `Tier != Team` |
| `server/Endpoints/Web/AuditLog/AuditLogListEndpoint.cs:75` | `Tier != Team` |
| `server/Services/Handlers/TenantOidcHandler.cs:65,107` | `Tier != Team` |
| `server/Endpoints/Web/Auth/AuthProviderChallengeEndpoint.cs:98` | `Tier == Team` |
| `services.core/Services/Alerts/EventAlertService.cs:47` | Free/non-Active suppresses event alerts |
| `services.core/Services/Alerts/AlertEvaluationJob.cs:105` | Free/non-Active tenants' rules are never evaluated |
| `services.core/Services/Handlers/InvitationHandler.cs:90` | 402 upsell on invite |
| `services.core/Services/Handlers/InvitationHandler.cs:126` | non-Team forces every invitee to `TenantAdmin` |
| `services.core/Services/Handlers/MemberHandler.cs:158` | role management is Team-gated |
| `services.core/Services/Handlers/DataExportHandler.cs:163` | audit-log inclusion in exports is Team-gated |
| `server/Services/Billing/ProSubscriptionPreProcessor.cs` | `RequiresProGate` |
| `server/Services/Billing/SubscriptionStatusPreProcessor.cs:71` | `Status == Canceled` |

`AlertEvaluationJob.cs:105` is the one that matters most: without it passing, alert rules created in
self-hosted would be accepted and then never fire. Email alerts are the headline self-hosted
feature, so this gate is load-bearing for the whole goal.

**Limit-keyed predicates** — these do *not* read the subscription object, so a synthetic tier alone
does not satisfy them:

| Site | Predicate |
| --- | --- |
| `server/Endpoints/Web/Alerts/AlertRuleCreateEndpoint.cs:77` | `CanCreateAlertRuleAsync` (0 on Free) |
| `server/Endpoints/Web/Integrations/IntegrationCreateEndpoint.cs:89` | `CanCreateWebhookAsync` (0 on Free) |
| `services.core/Services/Handlers/InvitationHandler.cs:112` | `CanAddMemberAsync` (1 on Free) |
| `services.core/Services/Machines/MachineService.cs:314` | machine limit via `GetEffectiveLimitsForTenantAsync` |
| `services.core/Services/Handlers/InvitationHandler.cs:232` | member limit via `GetEffectiveLimitsForTenantAsync` |

**Retention accessors** — a separate path again, and the one most likely to be missed:

| Site | Accessor |
| --- | --- |
| `server/Endpoints/grpc/TelemetryService.cs:465` | `GetEffectiveRetentionDaysForTenantAsync` → `RetentionClassPolicy.Classify` stamps `RetentionClass` on **every ingested row** |
| `server/Endpoints/Web/Machines/History/HistoryRequestValidator.cs:69` | `GetRetentionDaysForTenantAsync` clamps the query window |
| `server/Endpoints/Web/.../SshSessionsFleetEndpoint.cs:133` | `GetRetentionDaysForTenantAsync` clamps the query window |

`SubscriptionService.GetEffectiveRetentionDaysForTenantAsync` (`SubscriptionService.cs:153`) is a
straight pass-through to the repository, which computes from the real row. Left delegated, a
self-hoster would see "Team, unlimited" in the UI while their telemetry was stamped `Short` and
dropped after one day.

**Chosen approach: a `SelfHostedSubscriptionService` decorator** implementing `ISubscriptionService`
and registered in place of `SubscriptionService` when `SelfHosted == true`.

**It must be defined over all thirteen interface members, not the two that are interesting.** A
member left delegating to the inner service silently reintroduces a Free-tier limit. Explicitly:

| Member | Self-hosted behaviour |
| --- | --- |
| `GetSubscriptionForTenantAsync` | synthetic `Tier = Team`, `Status = Active` |
| `GetEffectiveLimitsForTenantAsync` | `int.MaxValue` in every field |
| `CanCreateAlertRuleAsync`, `CanCreateWebhookAsync`, `CanAddMemberAsync` | always `true` |
| `GetRetentionDaysForTenantAsync`, `GetEffectiveRetentionDaysForTenantAsync` | `RetentionClassPolicy.LongWindowDays` (365) — see below |
| `IsIngestEligibleAsync` | **delegate** — see below |
| `ProvisionFreeSubscriptionAsync`, `EnsureSubscriptionExistsAsync` | delegate (row creation must still happen) |
| `GetMachineCountForTenantAsync`, `GetMachineCountAtDateAsync`, `GetBillableMachineCountAsync` | delegate (real counts, no entitlement meaning) |

**`IsIngestEligibleAsync` must delegate, not answer `true`.** An earlier draft made it permissive
along with the other predicates. That is wrong: `SubscriptionService.IsIngestEligibleAsync`
(`SubscriptionService.cs:99`) checks `Tenant.IsActive` *before* it looks at any subscription, and
that check is the enforcement point for tenant deactivation and pending deletion — the behaviour
CLAUDE.md describes as blocking "immediately on the live `Tenants.IsActive` check". Overriding it
to `true` would let a deactivated self-hosted tenant's machines ingest forever. The override is also
unnecessary: `IsIngestEligible` (`SubscriptionService.cs:83`) accepts any `Active` or `PastDue`
subscription regardless of tier, and a self-hosted row is `Free`/`Active`, so delegating already
yields full ingest. Delegate.

**Retention is capped at 365 days, not unlimited, and the spec says so honestly.** Telemetry rows
are physically partitioned by `RetentionClass`, and `RetentionClassPolicy` defines exactly three
windows — `Short` (1), `Medium` (60), `Long` (365). There is no unlimited class. Returning
`int.MaxValue` from the retention accessors would classify as `Long` anyway, so the decorator
returns `LongWindowDays` directly rather than implying a ceiling that does not exist. Self-hosted
retention beyond a year is a separate piece of work.

`TenantSubscription` rows continue to be written as `Free` by `OnboardingHandler`, so no migration
is required and the frozen `InitialMigration` is untouched.

**Known wart, accepted:** in self-hosted the service reports a tier the database does not hold, and
`RetentionReclassifyJob` deliberately injects an *uncached, undecorated* `ISubscriptionRepository`
(`RetentionReclassifyJob.cs:87,118`) that would see the real `Free` row. That job is dormant in
self-hosted — its only dispatch site is `FleetAdminService.cs:635`, which is SaaS-only gRPC — but it
is a live example of the decorator being bypassable, and it must be called out in the decorator's
XML docs.

**The safety argument, corrected.** An earlier draft claimed the synthetic tier is safe because
every tier-keyed write path sits under `/billing/` or the internal gRPC port. That is **false**.
`MachineService.cs:388` fires on **agent machine registration** — very much reachable in
self-hosted — and takes a `Pro | Team` branch to write a billable quantity. (It is an inline copy,
not a call: `MachineService` does not reference `IMachineBillingSync` today. `MachineBillingSync`'s
own copy at `:63` is reached from the machine *deletion* path, `MachineHandler.cs:124`. §7 deletes
the copy and routes registration through the service, after which one site serves both.) Under
the synthetic Team tier, self-hosted registrations newly enter that branch where `Free` previously
skipped it. It is harmless only because `NoOpBillingApiClient.UpdateQuantityAsync` returns `true`
without doing anything (`NoOpBillingApiClient.cs:15`). So the real safety mechanism is the no-op
client, not routing, and that is what the decorator's docs must say. Any future tier-keyed write
path has to be checked against the no-op client's behaviour, not against a routing assumption.

**Rejected alternative:** introducing an `IEntitlementService` and rewriting all ~23 call sites.
Cleaner long-term, but a large diff across the SaaS revenue path for no SaaS behaviour change. The
thirteen-member decorator is the smaller change; it is worth revisiting if the surface grows again.

**Deliberate behaviour change to verify:** `InvitationHandler.cs:126` currently forces every invitee
to `TenantAdmin` when the tenant is not Team. Under the synthetic Team tier, self-hosted invitations
will honour the requested role instead. This is the desired outcome, but it is a behaviour change
rather than an unlocking, so it needs its own test.

### 5. Email

The `Resend` section is replaced by `Email`, with the provider selected by mode rather than
configured independently:

```yaml
Email:
  FromEmail: alerts@example.com
  Resend:
    ApiKey: ""                  # SaaS
  Smtp:                         # self-hosted
    Host: ""
    Port: 587
    Username: ""
    Password: ""
    UseStartTls: true
```

- **`IEmailService` keeps its contract unchanged** — three-state `EmailDeliveryOutcome`
  (`Sent` / `Skipped` / `Failed`), with `Skipped` remaining terminal success that callers must never
  retry. `AlertDeliveryService` and `SendInvitationEmailJob` need no changes.
- **SaaS** resolves `ResendEmailService`. A missing `Email:Resend:ApiKey` becomes a **startup
  failure**. In SaaS a missing key is always a misconfiguration, never a deployment style — this is
  precisely the overloading being removed.
- **Self-hosted** resolves `SmtpEmailService`, built on MailKit. A missing `Email:Smtp:Host`
  resolves `NoOpEmailService` instead, which returns `Skipped`. Email therefore stays genuinely
  optional for self-hosters while becoming mandatory for SaaS.
- **Invitation content moves out of the transport.** The *invitation* HTML is currently inline
  inside `ResendEmailService.cs:46` and would have to be duplicated by a second provider. It moves
  to a shared `EmailTemplates` static. Alert content is **already** transport-independent — built in
  `services.core/Services/Notifications/AlertEmailContent.cs` and passed into
  `SendAlertEmailAsync(toEmail, subject, htmlBody, …)` by `AlertDeliveryService.cs:88,113` — so it
  needs no change. The simplification applies to invitations only.
- `Email:FromEmail` is required whenever any provider is active, for both providers.

**MailKit** is added to `services.core`. `System.Net.Mail.SmtpClient` was evaluated: it is not
`[Obsolete]` in .NET 10 (so it would not trip `TreatWarningsAsErrors`) and would cover SMTP AUTH
over STARTTLS, which is every realistic self-hoster target. It was rejected because Microsoft's
documentation explicitly directs new development to MailKit (DE0005), and MailKit gives clearer
failure surfaces to map onto `EmailDeliveryOutcome.Failed`. A separate `services.email` project was
also rejected: `server` and `services.worker` both consume `IEmailService`, so the package ships in
the same two containers either way, and `services.core` already carries `AWSSDK.S3`,
`Hangfire.PostgreSql`, `Npgsql`, `StackExchange.Redis` and `NSec.Cryptography`.

**This obsoletes the "absent `Resend:ApiKey` means self-hosted" rule** documented in CLAUDE.md and
stored in user memory. Both must be updated as part of this work.

### 6. Web

`event.locals.user` is already hydrated from the session and cached in `hooks.server.ts` (the real
path is `getMeBootstrap()` around line 93; the `getMe()` call at line 74 is the mock-mode branch and
is not cached), so folding the flag into `/auth/me` reaches every server-side load with no extra
request.

`AuthMeEndpoint` adds a `deployment: { selfHosted }` object to `UserDto`
(`services.core/Models/Users/UserDto.cs`), mirrored in `src/web/src/lib/api/types.ts` and
`src/web/src/app.d.ts`. `UserDto` is otherwise built purely from cookie claims via
`FromPrincipal` (`AuthMeEndpoint.cs:58`); this field is set endpoint-side from DI **after** that
call, so it involves no claims round-trip, no cookie change and no role-cache interaction.
`src/web/src/lib/api/mock-client.ts` (`getMe()`, line 39) and any `UserDto` fixtures need the new
field too.

**Mixed-version window.** During phase 3 of the rollout a 3.3.0 `web` can briefly talk to a 3.2.0
`api-server`, which returns no `deployment` object. Every web-side gate is therefore written as an
explicit `selfHosted === true` comparison so `undefined` falls to the SaaS branch, which is the
correct behaviour for the SaaS cluster and the only place the mixed window can occur.

**Noted wart:** deployment mode is not a user property, and `UserDto` is a slightly odd home for it.
It is chosen because it is the payload the web app already fetches and caches. The alternative — a
separate unauthenticated `/api/v1/capabilities` endpoint — was rejected as an extra request per SSR
load for a value only authenticated pages need.

Changes:

- `routes/(admin)/admin/+page.server.ts:13` — `billingEnabled: !!env.PUBLIC_BILLING_URL` becomes
  `selfHosted` read from the session. `+page.svelte:343`'s `activeTab === 'settings' && billingEnabled === false`
  becomes `activeTab === 'settings' && selfHosted === true`. Identical behaviour, but it can no
  longer disagree with the server.
- `routes/(app)/settings/billing/+page.server.ts` — gates on `selfHosted` and returns 404 in
  self-hosted. It keeps reading `PUBLIC_BILLING_URL` inside `createBillingClient` as the billing-web
  base URL, which is its legitimate remaining job.
- Tier badges, upgrade prompts and usage-versus-limit meters hide when `selfHosted` is true.
- vord's own `/admin` global-admin page **stays in both modes**. It is the only place a self-hoster
  edits server settings. "The admin panel is SaaS-only" refers to `vord-internal/src/admin`, the
  separate fleet-admin application that reaches vord over the `FleetAdmin` gRPC service.
  `PUT /admin/settings` remains editable in self-hosted and 404 in SaaS — which is what
  `UpdateAdminSettingsEndpoint.cs:43` already does, now keyed on the honest flag.

## 7. Entitlement enforcement architecture

This section was added after implementation, when a question about `EnsureSubscriptionExistsAsync`
turned into a full audit of how the platform enforces "is this subscription active" and "does this
tier allow this feature". The audit found the enforcement itself sound but the *expression* of it
duplicated, with one dead method whose behaviour contradicts a documented policy elsewhere.

### What exists today

Enforcement happens in six places. Five are correct; the shape is the problem.

| Layer | Mechanism | Reach |
| --- | --- | --- |
| Subscription is active | `SubscriptionStatusPreProcessor` — non-GET requests outside the exempt paths; `Canceled` → 403 read-only | HTTP mutations |
| Pro-or-Team feature gate | `ProSubscriptionPreProcessor` + `EndpointTags.RequiresProSubscription` tag + `RequiresProFeatureMessage` metadata | 7 endpoints, declarative |
| Team-only feature gate | **Nine hand-written copies** of `(subscription is null) \|\| (subscription.Tier != SubscriptionTier.Team)` | scattered |
| Count limits | `CanCreateAlertRuleAsync` / `CanCreateWebhookAsync` / `CanAddMemberAsync` / `GetEffectiveLimitsForTenantAsync` | handlers + endpoints |
| Telemetry ingest | `IsIngestEligibleAsync` — tenant active **and** subscription `Active`/`PastDue` | gRPC |
| Retention | `GetEffectiveRetentionDaysForTenantAsync` → `RetentionClassPolicy.Classify` | ingest + query clamps |

### The duplication

**The Pro gate exists in three forms.** The declarative tag covers 7 endpoints through
`ProSubscriptionPreProcessor.RequiresProGate`. But pre-processors do not run in background jobs, so
`EventAlertService.cs:47` and `AlertEvaluationJob.cs:105` each hand-roll the identical predicate
`(subscription is null) || (Tier == Free) || (Status != Active)`. Three copies of one rule, two of
them invisible to anyone reading the tag.

**The Team gate has no shared form at all.** Ten sites each load the subscription themselves and
inline a tier-versus-Team test, and they do not all point the same way.

**Eight are the block form** — `(subscription is null) || (subscription.Tier != SubscriptionTier.Team)`
— each writing its own 403 message: `AlertRuleCreateEndpoint:69`, `AlertRuleUpdateEndpoint:124`
(additionally conditioned on the loaded `rule.IsCustom`), `MachineAuthorizedKeyAddEndpoint:62`,
`CommandSendEndpoint:49`, `AuditLogListEndpoint:75`, `TenantOidcHandler:65,107`, `MemberHandler:158`.

**Two are the grant form, and that is the trap.** `AuthProviderChallengeEndpoint:98` reads
`(subscription is not null) && (subscription.Tier == SubscriptionTier.Team)` to set a flag. So does
`DataExportHandler:163`, which uses it to *include* the audit log in a data export — no 403, no early
exit, running inside a background export job. Substituting a block-polarity predicate at either site
without inverting it flips the behaviour rather than preserving it; at `DataExportHandler` that means
audit logs exported for non-Team tenants and withheld from Team ones. None of the ten consults
`SubscriptionStatus` — unlike the Pro gate, they are tier-only.

Adding a Team feature means remembering to copy the idiom; forgetting means shipping an ungated
feature with no compiler or test complaint.

**Two further predicate shapes exist that neither name covers.** `InvitationHandler.cs:89` gates on
`(subscription is null) || (Tier == Free)` and returns **402**, not 403 — a "paid tier of any kind"
rule that is neither the Pro predicate (it has no status test, and Pro passes) nor the Team one.
`InvitationHandler.cs:126` re-derives `Tier != Team` to fork the invitee's role rather than to
block. And the billable-tier allowlist `(Tier == Pro) || (Tier == Team)` is itself duplicated across
`MachineBillingSync.cs:63` and `MachineService.cs:388`. A third expression of "which tiers are
billable" lives at `FleetAdminService.cs:818-822`, written as a denylist over a *request-supplied*
tier string rather than a loaded subscription; it is SaaS-only and stays where it is, but it means
`MachineBillingSync` is not the sole home for that rule even after the duplication below is deleted.

So the true count is **four distinct predicate shapes across roughly twenty sites**, not two across
nine.

**One gate fails open, and it is reachable.** `SubscriptionStatusPreProcessor:66` returns early when
the subscription is null, permitting the mutation. Every other gate blocks on null.

The earlier draft called this defence-in-depth on the grounds that row creation is transactional at
every tenant-creation path. That is wrong: there are **three** creation paths, not two.
`OnboardingHandler.cs:96` and `InvitationHandler.cs:254` do provision the subscription inside the
same transaction as the `Tenant` insert (`:122` and `:266`). `TenantHandler.CreateAsync` does not —
it opens a transaction, inserts the tenant, writes an audit row and commits, and the class takes no
subscription dependency at all. It is live: `TenantCreateEndpoint` maps `POST /tenants` under the
Admin policy and calls it, and members can then be attached through `MemberHandler`. So a global
admin can create a tenant that permanently has no subscription row, and every mutation by that
tenant's members currently passes the status gate unchecked.

That makes the fix two-sided: harden the pre-processor to fail closed **and** give `TenantHandler`
the same transactional provisioning the other two paths already have. Hardening alone would convert
a silent fail-open into a permanently read-only tenant with no route to recovery in self-hosted,
where the billing endpoints are absent.

**`EnsureSubscriptionExistsAsync` is dead and one branch is wrong.** Zero callers since the initial
code drop (`git log -S` over `src/` returns only `21095ac`). Its branches duplicate mechanisms that
already exist: provisioning is transactional at both tenant-creation paths
(`OnboardingHandler.cs:122`, `InvitationHandler.cs:266`), and the `Canceled`-paid revert is owned by
`BillingWebhookHandler.cs:240`. Worse, that revert **contradicts a documented policy**:
`StripeSyncJob.cs:160-162` states *"never downgrade a paid subscription to Free via the sync path…
the webhook pipeline owns Pro→Free transitions and is the authoritative source for downgrades"*,
guarding the branch at `:163-170` — and `EnsureSubscriptionExistsAsync` performs exactly that
locally-derived downgrade without consulting Stripe. A paid tier with `None` status also matches no
branch and silently does nothing.

### Target architecture

The aim is one vocabulary and one place per rule. This extends the pattern that already works
rather than introducing a new abstraction.

**One home for the predicates.** A `SubscriptionPolicy` static in `services.core` holding pure,
unit-testable functions over a loaded `TenantSubscription?`. Every predicate is **block-polarity** —
`true` means refuse — so no reader has to track which way each one points:

| Predicate | `true` when |
| --- | --- |
| `RequiresPro(subscription)` | null, `Free`, or `Status != Active` |
| `RequiresTeam(subscription)` | null, or `Tier != Team` |
| `RequiresPaidTier(subscription)` | null, or `Tier == Free` — the `InvitationHandler` 402 rule |
| `BlocksMutations(subscription)` | null, or `Status == Canceled` |

`BlocksMutations` is deliberately *not* called `IsActive`: it returns `true` for `PastDue` and
`None` as well as `Canceled`, so an "active" reading would be wrong, and an allow-polarity name
sitting beside three block-polarity ones is exactly the kind of detail that gets misread once and
then copied. The name states what it decides.

The null case is decided once, in the open, and every predicate fails closed on it. Every
enforcement point calls these; none re-derives them. `ProSubscriptionPreProcessor.RequiresProGate`
moves here verbatim, and the two background-job copies become calls to it.

**Deliberately excluded**, with the reason recorded so the seam does not read as arbitrary: the
count limits (`Can*Async`, `GetEffectiveLimitsForTenantAsync`), ingest eligibility and retention all
need repository access, so they stay on `ISubscriptionService` where the self-hosted decorator
already governs them. `SubscriptionPolicy` is pure functions over an already-loaded subscription.
The billable-tier allowlist in `MachineBillingSync`/`MachineService` also stays out of
`SubscriptionPolicy`: it selects which tiers are *invoiced*, not which features are permitted, and
folding a billing concern into an entitlement type would blur the very boundary this section exists
to sharpen. It gets a different fix — see below.

**The billable-tier duplication is deleted, not extracted.** Looking at the two sites closely, they
do not merely share a predicate: `MachineService.cs:381-400` reimplements, inline, the entire
operation that `IMachineBillingSync.ReportActiveMachineUsageAsync` already is — load the
subscription, test the billable-tier allowlist, load the tenant, compute the billable count, report
the quantity, all wrapped in the same best-effort `try`/`catch`. The tier comment is duplicated
verbatim down to its explanation of `Tier.None`.

So `MachineService` should call the service instead of restating it. That removes the duplicated
allowlist, the duplicated tenant load and the duplicated error handling in one edit — and because
`:396` is the **only** use of `_billingApiClient` in `MachineService`, the class can then drop that
dependency entirely and stop touching billing at all. A machine service that does not know about
billing is the right boundary; extracting a shared `IsBillableTier` helper would have preserved the
wrong one.

**No extra subscription read, provided the dead local goes too.** `ReportActiveMachineUsageAsync`
loads the subscription itself, whereas `MachineService` already holds one at `:313` — but that local
is read *only* inside the block being deleted, so it goes with it. One read before, one read after;
it simply moves to after the machine row is committed.

**The call site's position is load-bearing.** The reported quantity is a live count
(`Math.Max(activeMachineCount, tierFloor)`), so the call must stay where the deleted block was —
after `CreateMachineWithKeyAsync` commits the new row. Resolving the service earlier is harmless;
invoking it earlier under-reports by one, silently, because `ReportActiveMachineUsageAsync` swallows
its own failures and any quantity is accepted.

**Team gating becomes declarative, like Pro already is.** Add `EndpointTags.RequiresTeamSubscription`,
a `RequiresTeamFeatureMessage` metadata type and a `TeamSubscriptionPreProcessor`, modelled exactly
on the Pro trio and living beside it in `src/server/Services/Billing/`. Four endpoint-level Team
gates become one `Tags(...)` line each. The non-endpoint and conditional sites —
`TenantOidcHandler` ×2, `MemberHandler`, `DataExportHandler`, `AuthProviderChallengeEndpoint`, and
`AlertRuleUpdateEndpoint` whose gate depends on the loaded `rule.IsCustom` — call
`SubscriptionPolicy.RequiresTeam` directly, because a tag cannot express a condition on loaded state
and a pre-processor cannot reach a handler.

**The two grant-form sites invert the call.** `AuthProviderChallengeEndpoint` and
`DataExportHandler` ask "is this tenant Team?", not "should this be refused?", so both are written
`SubscriptionPolicy.RequiresTeam(subscription) == false`. Writing the bare predicate at either site
compiles, passes type-checking, and silently reverses the feature.

**Registration order is load-bearing.** The Team pre-processor must be registered *after* the Pro
one (`Program.cs:441-445`, which already runs `TenantContextPreProcessor`, then Status, then Pro).
`AlertRuleCreateEndpoint` carries both tags, and today a Free tenant sees the *Pro* message because
the Pro gate fires before the handler's Team check is ever reached. Registering Team first would
change the message a Free tenant receives, and functional tests assert on those strings.

**Fail closed everywhere.** `SubscriptionStatusPreProcessor` treats a null subscription as blocked,
matching every other gate.

**Delete `EnsureSubscriptionExistsAsync`.** This one is decided rather than proposed, and the
reasoning reversed twice under evidence, so the trail is recorded here in full.

It reads as the natural "is this tenant's subscription coherent?" sanity check, and the instinct to
keep it is a good one. It survives none of the following:

*It has never been called.* `git log -S` over `src/` returns only the initial code drop (`21095ac`)
and the self-hosted decorator's delegation. It was written once and never wired — which is also why
it carries a silent hole: a paid tier with `Status == None` matches none of its branches and does
nothing at all.

*Its safe branches duplicate mechanisms that already exist — or belong at the creation site.*
Provisioning is already transactional at two of the three tenant-creation paths, in the same
transaction as the `Tenant` insert (`OnboardingHandler.cs:122`, `InvitationHandler.cs:266`); the
third, `TenantHandler.CreateAsync`, is given the same treatment above rather than being patched from
a request path. Nothing deletes a subscription row short of tenant purge, where the tenant is already
deactivated and blocked upstream.

*Its dangerous branch contradicts a documented policy.* On a `Canceled` **paid** row it reverts to
`Free`/`Active` with `clearCurrentPeriodEnd: true` (`SubscriptionService.cs:246`). But
`StripeSyncJob.cs:158-166` carries an explicit guard — *"never downgrade a paid subscription to Free
via the sync path… the webhook pipeline owns Pro→Free transitions and is the authoritative source
for downgrades"* — and corrects status drift with `tier: null` only. The Free-revert belongs to
`BillingWebhookHandler.cs:240`, which acts on billing-api's PendingActions and distinguishes
DowngradeToFree from CancelAccount. `EnsureSubscriptionExistsAsync` performs exactly the
locally-derived downgrade the sync path refuses to make, without consulting Stripe and without that
distinction. It is not a redundant duplicate; it is a policy-violating one.

*And the check it appears to offer already exists*, on the read path where it belongs — see the
enforcement table above. Wiring it into a request path would add a **write-path** duplicate of
read-path enforcement that already works, on a hot path, racing across replicas.

*In self-hosted it would be actively harmful.* There is no payment provider, so a `Canceled` paid
row is imported history from a previous hosted life. Reverting it destroys the record irreversibly,
including the ability to migrate back — precisely for the migration story self-hosting invites.

So: delete it from `ISubscriptionService`, `SubscriptionService`, the self-hosted decorator and its
tests. Reconciliation stays where it already lives — `StripeSyncJob` on a five-minute schedule for
status drift, `BillingWebhookHandler` for tier transitions.

**Two consequences that must land in the same commit.** The decorator drops to **twelve** members,
and "all thirteen" is load-bearing prose in §4 above, in vord CLAUDE.md, and in the parent plan's
grep check — every occurrence changes together or the documentation fails its own audit. And
`SubscriptionStatusPreProcessor`'s null fail-open is fixed at the pre-processor, paired with
provisioning at `TenantHandler` (above), **not** by reintroducing an ensure-exists call: hardening
the enforcement point adds no write path, and fixing the creation site removes the state rather than
repairing it later.

**What is deliberately not solved by this.** One state has no reconciler once the method is gone: a
`Free` row drifted non-`Active`, excluded from `StripeSyncJob` by its `Tier != Free` filter. (A
missing row was a second such state; closing the `TenantHandler` gap removes the only path that
produced one.) It does not arise from normal operation; the only live writer that can produce it is
the manual admin path `FleetAdminService.cs:443`, which sets arbitrary tier and status over the
internal control plane. That is operator-error scope, and the same admin surface can correct it
directly. If it ever needs a systematic answer, the answer is a reconciliation job with explicit
semantics — not a helper on the request path.

### Consequences for self-hosted

None of this changes the self-hosted design, because every rule above reads through
`ISubscriptionService` and the decorator already answers permissively. Two knock-on edits:

- `SelfHostedSubscriptionService` drops to **twelve** members. "All thirteen members" is load-bearing
  prose in §4 above, in vord CLAUDE.md and in the decorator's own XML remarks — every occurrence must
  change in the same commit, or the documentation fails its own audit.
- The new `TeamSubscriptionPreProcessor` must be registered in the FastEndpoints configurator
  alongside the existing two, or the Team gates silently stop firing in **SaaS** — the failure mode
  is an unguarded paid feature, so it needs a SaaS regression test per gated endpoint.

### Why this is worth doing

The nine copied Team gates are the kind of duplication that is harmless until the day someone adds
the tenth feature and forgets. There is no test that can catch a gate that was never written. Making
the gate declarative means the omission is visible in the endpoint's `Configure()` method, next to
its route and its policy, where a reviewer already looks.

## Testing

Per repository convention, every item below is required before the work is complete.

**Unit**

- `DeploymentOptionsValidator` — both failure modes, both valid modes, and the default-when-absent
  behaviour asserted explicitly (a regression here silently flips a deployment's mode).
- `DeploymentMode` — `IsSaas` is the exact negation of `IsSelfHosted`.
- `SelfHostedSubscriptionService` — synthetic tier and status; `int.MaxValue` on every
  `EffectiveLimits` field, asserted field by field rather than by count.
- `SmtpEmailService` — `Sent`, `Failed` (connect failure, auth failure, rejected recipient), and
  `Skipped` when host is absent.
- `EmailTemplates` — invitation and alert bodies render, and HTML-encode injected tenant/inviter
  names.
- `BillingOptionsValidator` — certificate/key pairing, with the removed `Enabled` branch gone.

**Functional** — run the full matrix in both modes:

- Self-hosted: the **billing management** endpoints (`CancelSubscriptionEndpoint`,
  `DowngradeSubscriptionEndpoint`, `ReactivateSubscriptionEndpoint`, `ResumeSubscriptionEndpoint`,
  `CatalogEndpoint`, `InvoicesEndpoint`, `UpcomingInvoiceEndpoint`, `UsageHistoryEndpoint`) return
  404. `UsageHistoryEndpoint` is in the set because its invoice-amount series is sourced from
  `IBillingApiClient`: in self-hosted every month would report `invoiceAmountCents: 0`, which reads
  as "you were billed nothing" rather than "this product has no billing". Its machine-count series is
  genuine, so if that view is wanted self-hosted it should be re-exposed as a usage endpoint that
  does not claim to be billing history.
  **`GET /billing/subscription` must stay reachable** — it is unguarded today
  (`SubscriptionEndpoint.cs:90`) and the app layout fetches it on every SSR load
  (`web/src/routes/(app)/+layout.server.ts:20`) for the tier/limits payload. A blanket
  `/billing/*` 404 would break the shell on every page.
- Self-hosted: alert-rule create *and* the rule-limit predicate, webhook/integration create, SSH key
  add, command send, audit log list, custom OIDC, and a second member invitation all succeed for a
  tenant whose row is `Free`; the invitee's requested role is honoured; `PUT /admin/settings`
  succeeds.
- Self-hosted: ingested telemetry is stamped `RetentionClass.Long`, not `Short` — the regression
  test for the retention accessors, which is the failure mode a UI-level test would not catch.
- Self-hosted: a **deactivated** tenant still cannot ingest. Unlocking entitlements must not unlock
  tenant deactivation; `IsIngestEligibleAsync` delegates precisely so pending-deletion enforcement
  survives, and this test is what stops a later edit from making it permissive alongside its
  neighbours.
- SaaS: every tier gate still refuses a `Free` tenant; `PUT /admin/settings` returns 404;
  `BillingGateway` / `FleetAdmin` are routed.

**Test-host changes required.** `test/shared/FunctionalTestFactory.cs:120` currently sets
`Billing__Enabled=true` and `Billing__GrpcUrl` with **no** Resend key. Under the new SaaS
startup validation that host would fail to start. The factory must set `Deployment__SelfHosted`
per the mode under test and inject a fake `Email:Resend:ApiKey` for the SaaS matrix — and because a
present key makes `ResendEmailService` live, the factory must also register a substitute
`IEmailService` container-wide rather than per-test as today (e.g.
`AlertEmailDeliveryTests.cs:42`), or a functional test will attempt a real HTTP call to Resend.

**Architecture**

- A test asserting `BillingManagement.BillingManagementClient` is not resolvable from a container
  built in self-hosted mode. This complements the existing `BillingContractBoundaryTests`, which
  governs *compile-time* reachability of the contract; this new test governs *runtime* registration.

**Web**

- Vitest coverage for the billing and admin loads under both `selfHosted` values.

## Deployment

### Self-hosted reference deployment

`deployment/server/docker/docker-compose.yml` — add `Deployment__SelfHosted: "true"` to
`api-server` and `services-worker`, remove `Billing__Enabled`, replace `RESEND_API_KEY` /
`RESEND_FROM_EMAIL` with `EMAIL_FROM` and the `SMTP_*` set. The compose file is the reference
self-hosted deployment, so it should demonstrate the SMTP path with commented defaults for a local
relay.

### SaaS deployment (`framlux/stack`)

All four fleet workloads (`api-server`, `services-worker`, `web`, `migration-runner`) take their
configuration from a single Kustomize `configMapGenerator` named `fleet-config`, declared in
`clusters/prod/apps/vord-platform/base/kustomization.yaml`, plus the `vord-secret` SealedSecret.
Two properties of that setup shape the rollout:

- `fleet-config` is generated, so Kustomize appends a content hash to its name. Any literal change
  produces a new ConfigMap name and therefore a rolling restart of everything that mounts it.
- `vord-secret` is not hash-versioned, but both `api-server` and `services-worker` carry a
  `secret.reloader.stakater.com/reload: "vord-secret,…"` annotation and Stakater Reloader is
  installed (`clusters/prod/apps/reloader-app.yaml`), so a reseal does trigger a rolling restart.
  (The comment in `kustomization.yaml` claiming a `vord-secret` change "triggers no rollout of its
  own" predates the Reloader annotations and should be corrected while we are in the file.)

**The danger is narrow and specific:** a build that reads `Deployment:SelfHosted` arriving before
the value that sets it to `false`. The rollout below closes that window by putting every new key in
place while it is still inert, rather than relying on commit ordering discipline at release time.

**Phase 1 — `stack`, before the vord tag exists.** Every key added here is unbound in the running
3.2.0 build, and ASP.NET Core configuration silently ignores keys that bind to nothing, so this
phase is a behaviour-free restart:

- add `Deployment__SelfHosted=false` to `fleet-config`
- add `Email__FromEmail=Framlux Vord <invitations@outreach.framlux.io>` alongside the existing
  `Resend__FromEmail` (both present, only the old one read)
- reseal `vord-secret` to carry `Email__Resend__ApiKey` in addition to `Resend__ApiKey`

The resulting rolling restart on 3.2.0 is itself the verification that the additions are inert.

**Phase 2 — `vord`.** Tag `server-v3.3.0`, which builds `api-server`, `services-worker`, `web` and
`migration_runner`.

**Phase 3 — `stack`.** Bump the four image tags from `3.2.0` to `3.3.0` in
`fleet/api-server/deployment.yaml`, `fleet/services-worker/deployment.yaml`,
`fleet/web/deployment.yaml` and `fleet/migration-runner/deployment.yaml`. The mode flag and the
email keys are already present, so the new build comes up in SaaS mode with email configured and
billing intact.

**Phase 4 — `stack` cleanup, once 3.3.0 is healthy.** Remove `Billing__Enabled=true` and
`Resend__FromEmail` from `fleet-config`. Both are inert against 3.3.0 — `Billing:Enabled` no longer
binds to anything, and the fleet email options read the `Email:*` keys.

**`Resend__ApiKey` must NOT be dropped from `vord-secret`.** `vord-secret` is shared: billing-api
mounts it via `secretRef` (`billing/api/deployment.yaml:68`) and binds `Resend:ApiKey` directly
(`vord-internal/src/billing-api/Program.cs:119`), with a
`secret.reloader.stakater.com/reload: "vord-secret,…"` annotation (line 10). Deleting the key would
reseal the secret, Reloader would restart billing-api, and it would come up with its Resend sender
silently swapped out — exactly the silent-email-loss failure this design exists to remove. The
"leave vord-internal's Resend keys alone" instruction is unsatisfiable by scoping alone, because it
is the *same physical key in the same secret*.

So `Resend__ApiKey` stays in `vord-secret` as billing-api's key, and `Email__Resend__ApiKey` is the
fleet's. Two keys, same value, different owners. Collapsing them into one is a follow-up that
requires either splitting `vord-secret` per workload or renaming billing-api's binding in
`vord-internal` first; it is explicitly **not** part of this work.

**Why the email rename cannot lag the image.** In SaaS mode a missing `Email:Resend:ApiKey` is a
hard startup failure by design (§5). If the image were bumped before the secret carried the new
key, every fleet pod would fail its startup probe and the ArgoCD sync would stall. That is the
intended loud failure rather than silently stopping invitations — but phase 1 means it is never
reached.

**Out of scope:** `billing-config` and `billing/api` in the same `kustomization.yaml` belong to
`vord-internal`, which has its own options classes. Its `Resend__FromEmail` literal and its
`Resend__ApiKey` binding are **not** part of this rename. Note the shared-secret constraint in
phase 4 — this is not merely a scoping instruction, it is a hard dependency.

**Verification:** `kustomize build clusters/prod` must render cleanly after each stack phase, and
phase 1 must be confirmed by observing the fleet pods restart and stay healthy on 3.2.0 before the
tag is cut.

## Documentation

- vord CLAUDE.md — rewrite the "Email (Resend) is optional" paragraph, and the `Billing:Enabled`
  references in the internal-gRPC paragraph; add a paragraph describing `Deployment:SelfHosted` as
  the single mode switch.
- stack CLAUDE.md — no structural change, but the `fleet-config` comment block about
  `Resend__FromEmail` / `vord-secret` rollout behaviour needs updating alongside the literals.
- User memory `vord-self-hosted-mode-signal` — replace; the Resend-absence inference it records is
  no longer true.

## Risks

| Risk | Mitigation |
| --- | --- |
| SaaS deploy ships without `Deployment__SelfHosted=false` and silently loses billing | Phase 1 of the stack rollout places the flag while it is still inert, so no build that reads it ever runs without it; `DeploymentOptionsValidator` additionally fails startup on a half-configured SaaS deployment |
| A later stack change reverts or drops the `Deployment__SelfHosted` literal | It sits in `fleet-config` next to `Billing__GrpcUrl`, and a SaaS deployment missing the flag falls back to self-hosted where `Billing__GrpcUrl` is ignored — no startup failure catches this. A comment in `kustomization.yaml` must state that the literal is load-bearing |
| A `SelfHostedSubscriptionService` member is left delegating and silently reintroduces a Free limit | The decorator is specified over all thirteen members with defined semantics per member; the retention and `Can*Async` regression tests are the backstop, since a delegated member fails silently rather than loudly |
| Synthetic Team tier leaks into a tier-keyed write path added later | Reachable tier-keyed writes already exist (`MachineBillingSync.cs:64`) and are safe only because `NoOpBillingApiClient` no-ops them; this is documented on the decorator as the actual invariant to preserve |
| `RetentionReclassifyJob`'s undecorated keyed repository sees the real `Free` row | Dormant in self-hosted (dispatched only from the SaaS-only `FleetAdminService.cs:635`); documented on the decorator so it is not made reachable without revisiting |
| MailKit is a new dependency in a project referenced by seven test projects | Standard package, no native assets; build verification covers it |
| `UserDto` growing a non-user field invites more of the same | Noted as a deliberate wart in this document; revisit with a capabilities endpoint if a second such field appears |
