# Product Steelman Review — 2026-07-22

Four parallel independent reviews (over-engineering, solo-operability, scalability, product gaps) judged the shipped code — not the plans — against the product tenets: **simple/transparent, easy to run solo, scalable to thousands of tenants / hundreds of thousands of machines**. Every finding below carries file:line evidence verified by the reviewer that reported it.

## Overall verdict

**The load-bearing architecture is right-sized, not over-engineered.** The async projection with batch collapse, daily partition-drop retention, the ingest resilience stack (dedup + circuit breaker + unique-index fallback + Lua stream-slot caps), Redis caching in front of every per-request auth/subscription check, and the two-repo split (forced by the FSL license — Stripe code cannot live in the source-available repo) all map to real constraints and survive a steelman.

**What fails the steelman is not the architecture — it is (a) a handful of job-shape implementations that cap practical scale at ~5–10k machines and a few hundred paid tenants, (b) two operability gaps that will genuinely burn a solo operator, and (c) customer-facing docs/copy written aspirationally and never reconciled with what shipped.**

Two factual corrections to internal lore, verified: the ingest path has **no Redis stream** (gRPC → Redis dedup → direct batched Postgres insert → polling projection), and there is **no audit hash chain** (plain table insert, `DatabaseRepository.AuditLog.cs:15-26`).

---

## Critical — GA blockers

### 1. The migration scheme cannot evolve a live database (solo-op review)
Two frozen migrations (`InitialMigration` = version 2026-04-05.1, `InitialMigration2` = .2) with the rule "add schema in place, never new files." FluentMigrator records applied versions in `VersionInfo` and never re-executes them: any database that has ever migrated treats `MigrateUp()` as a **silent no-op** — migration reports success, the server starts, and every in-place schema change since consolidation (projection cursor table, `AlertEmailDeliveryAttempts`, `MemberLimit`, trigram indexes, …13 commits) never reaches it. The deployed `prod` branch's DB already contains those version numbers. CI structurally cannot catch this (integration tests use a fresh Testcontainers Postgres). Self-host compose pulls `IMAGE_TAG=latest`, so existing self-hosters get the same silent corruption. billing-api already uses normal incremental migrations — the sane model exists in the other repo. **First post-GA schema change bricks production undetectably.** Fix: reinstate incremental migrations + an upgrade-path test (old schema → head) + written upgrade procedure. ~2–4 days.

### 2. Data-protection key ring lives only in unpersisted Redis (solo-op review)
`RedisXmlRepository` holds the ASP.NET key ring (`ServiceCollectionExtensions.cs:217-236`); compose Redis has **no volume and no AOF** (`docker-compose.yml:31-49`). Redis loss invalidates every session/CSRF/OIDC nonce — and because `OidcSecretProtector` encrypts Team-tier tenant OIDC client secrets with this ring, those secrets in Postgres become **permanently undecryptable**. Durable customer data keyed to a cache. Fix: persist the ring to Postgres (or file+volume) and add Redis AOF+volume. ~1 day.

### 3. The documented install path does not exist (product-gap review)
Docs and homepage say `curl https://get.vordfleet.dev | sudo bash -s -- --token …`; nothing serves that URL, the real `install.sh` accepts no flags (env vars only), the KB names service `vordfleet-agent` / config `/etc/vordfleet/agent.conf` / endpoint `api.vordfleet.dev` — actual: `vord-agent`, `/etc/framlux/vord-agent.toml`, `grpc.app.vordfleet.dev`. The dashboard-generated script still uses `apt-key` (removed in Ubuntu 24.04/Debian 12) while the repo script correctly uses keyrings — the two drifted. **Install is minute one of the paid journey; every documented command fails.** Fix: host the script, unify on the keyring flow, sed the KB. ~2 days.

### 4. Retention is physically 365 days for everyone (scalability review)
Partition drop uses `MAX(TierFeatureLimits.RetentionDays)` over the tier config table (`DatabaseRepository.Partitions.cs:15-18`), so Team's 365 days keeps every partition ~367 days regardless of tenant mix; Free/Pro rows are only hidden at query time. ≈600B/row × ~4,600 rows/machine/day → **~10TB at 10k machines, ~100TB at 100k**. Storage kills the product one order of magnitude before any throughput limit. Fix: retention-class sub-partitioning (LIST(class) × RANGE(day)) + history rollups (also fixes the 525k-row 365-day chart fetch). ~1–2 weeks.

---

## High — scale-ceiling items (fix before "hundreds of thousands" is honest)

5. **Alert evaluation** — single serial job/minute doing 2 DB round-trips per (rule, machine) pair even when nothing fires (`AlertEvaluationJob.cs:213-218`): ~400k queries/tick at 2k paid tenants; unmeetable beyond ~300–500 paid tenants; degradation is silent (skipped ticks). Fix: set-based SQL evaluation + per-tenant fan-out. ~1 week.
6. **StripeSyncJob** — every 5 min, serial live Stripe read per paid tenant (`StripeSyncJob.cs:59-110` → billing-api live fetch): 12.5 min per 5-min tick at 5k paid; breaks ~1–2k. `UsageHeartbeatJob` is a strict subset of it (over-engineering review M1 concurs). Fix: webhooks primary, hourly/daily reconciliation, drop the heartbeat job. ~2–3 days.
7. **Health sweep fan-out** — per-tenant Hangfire jobs twice a minute (~14.4M Postgres-backed jobs/day at 5k tenants) each running one UPDATE under two distributed locks (`HealthSweepCoordinatorJob.cs:60-114`; both scale and over-engineering reviews flagged independently). Fix: coordinator runs set-based sweeps over tenant slices directly. ~1–2 days.
8. **Projection ceiling & failure mode** — ShardCount=1 tops out ~4–5k rows/s vs ~5.3k needed at 100k machines; lag > `OnlineThresholdSeconds` marks the whole fleet offline → alert storm. Fix: multi-row `UPDATE…FROM VALUES` batching (~10–50×), pre-provision shards, projection-lag metric. ~1 week (with #9).
9. **MachineStateSummary write amplification** — ~1 UPDATE/machine/min against 11 indexes incl. 3 GIN → non-HOT updates, ~1.6B index writes/day at 100k; vacuum death by ~50k. Fix: hot/cold table split with low fillfactor. ~3–4 days.
10. **No metrics, no operator alerting** — 3 instruments total, no exporter of any kind; webhook "alerting" is `LogCritical` to stdout. Operator cannot see ingest lag, cursor lag, queue depth, or billing failures. Fix: OTel + Prometheus exporter + ~10 key gauges + alert rules. ~2–3 days.
11. **`/readyz` hard-fails on Redis** (`ServiceCollectionExtensions.cs:204-206`), so the carefully built fail-open paths never matter — any Redis blip is a full outage; `ServerConfigurationService` reads Redis with no outage handling. Fix: Redis = Degraded in readiness + DB-fallback catch. ~0.5–1 day.
12. **Production manifests exist only in the cluster** — `deployment/kube/` referenced in docs does not exist in any repo; the real deployment is unversioned and unrecoverable. ~1–2 days to commit.
13. **Redis ping sorted sets** — 7-day per-heartbeat history ≈ 25GB at 100k machines, keys never expire for decommissioned machines. Fix: last-ping key + short window + TTL. ~1 day.
14. **In-app billing page states wrong plan terms** — "Unlimited machines, 30-day retention" for Pro (actual 1,000 / 60-day), wrong downgrade copy, stale fallback prices (`settings/billing/+page.svelte:56-60,659`). ~0.5 day.
15. **Homepage sells "custom thresholds on Pro"; code gates custom rules to Team** (`+page.svelte:104` vs `AlertRuleCreateEndpoint.cs:68-71`); Free-tier alerting story contradicts itself across doc/table/code. Decide the truth, then minutes to fix.
16. **Privacy policy promises deletion that isn't built** — no tenant/account deletion endpoint exists. Interim: documented request process (~1 h); real purge job ~3–5 days.
17. **No agent version reporting** — protos carry no version field; support cannot triage old agents; no update visibility. ~1–2 days.

## Medium — simplicity/ops debt worth a focused week

18. **Settings cache layer 1 has zero production readers** — `ServerSettingsCache.GetSettingAsync` is called only by its own tests; production reads go Redis→DB, deliberately bypassing it; the pub/sub invalidation channel keeps coherent a cache nobody reads (`ServerSettingsCache.cs:80-116`, `ServerConfigurationService.cs:132-178`). Verify with LSP, then delete layer + pub/sub + the `database`→Redis ctor dependency. ~4–6 h.
19. **Config knobs that bind to nothing, advertised to self-hosters** — compose + `.env.example` tell operators to tune `Subscription__FreeTierMachineLimit`/`FreeTierRetentionDays`; no code reads a `Subscription` section (real limits are seeded `TierFeatureLimits`); `ServerDefaults` appsettings blocks read by nothing. A shipped trap. Delete or wire. 1–2 h / 0.5 day.
20. **Dual admin surfaces** — REST admin + 835-line gRPC `FleetAdminService` + second SPA implement users/settings/audit twice. Freeze the duplication (zero cost) or consolidate (~1–2 days).
21. **Backup/DR** — no tooling or docs for either Postgres; two DBs + Stripe must stay consistent; restore thinking exists only as DEFERRABLE FKs. ~1 day to document + test one restore.
22. **vord-internal has zero operator docs**; admin UI env vars silently default empty. ~0.5–1 day.
23. **README config drift** — `InternalApi`, `Streaming__ShardCount`, `Hangfire__*`, `ForwardedHeaders` undocumented. ~0.5 day.
24. **Webhook signing exists but is undocumented** (`X-Vord-Signature` HMAC, `CustomPayloadFormatter.cs:74-82`); customers can't verify. ~0.5 day docs.
25. **No machine API-key rotation** (decommission+reinstall is the only path). ~2–3 days.
26. **Command-poll stream** — 3,333 qps of empty SELECTs at 100k machines; raise default poll to 120s + push hint. Hours.
27. **Connection budget** — pools × replicas ≈ 500–800 conns vs Postgres default 100; needs pgbouncer (xact-scoped advisory locks are compatible) or tuning. Config.
28. **Per-agent second stream is refused by default cap** (1 stream/machine vs fast+slow streams) → permanent unary fallback + 100k warn logs/day. One-line default.
29. **SubscriptionEndpoint makes a live Stripe call per paid page view** (uncached in billing-api). Hours.
30. **Package metadata says `license: Proprietary`** for the MIT-badged agent; wrong homepage URL (`nfpm.yaml`). Minutes.
31. Marketing footer lacks Terms/Privacy links; no status page; no automation/read API tokens (document absence); docs nav naming drift; `AntiforgeryStartup.CookieAuthenticationScheme` confessed-dead symbol (~15 min).

## Kept deliberately (steelman survived)
Async projection + dormant ShardCount seam; daily partitioning + partition-drop mechanism; ingest dedup/breaker/fallback stack; `CachingSubscriptionRepository`; antiforgery enrollment + allowlist parity pattern; two-repo FSL split (automate the proto publish); Hangfire/BackgroundService split (minus the fan-outs above); `ISqlDialect` (real SQLite second impl); `SubscriptionService` consolidation (split only if it grows again — mock-churn outweighs today).

---

## Recommended phasing

- **Phase A — before any customer money (~2 weeks):** items 1, 2, 3, 14, 15, 16-interim, 19, 30 + backup doc (21). Nothing here is optional; three are silent-failure shaped.
- **Phase B — before the scale story is honest (~3–4 weeks):** items 4, 5, 6, 7, 8, 9, 10, 11, 13, 26–28. Highest ROI first: retention-class partitioning + rollups; set-based alerts + sweep de-fan-out; projection batching + hot/cold split.
- **Phase C — simplicity sweep (~1 week):** items 18, 20, 22, 23, 24, 31 + 12 (commit manifests).
- **Phase D — product fast-follows:** 17, 25, 16-full, status page, API tokens.

Verification note: item 18 (unused cache layer) should be re-confirmed with `csharp-lsp` findReferences before deletion; all other Criticals were mechanically verified (version-table behavior, compose contents, URL/flag greps, seeded limits).
