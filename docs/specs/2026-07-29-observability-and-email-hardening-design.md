# Observability and email-delivery hardening

Date: 2026-07-29
Beads: `vord-xoo` (P0 bug), `vord-8gq` (P1), `vord-2eb` (P3, folded in)
Repos touched: `vord`, `vord-internal`, `stack`

## Problem

Two failures share one root cause: the platform cannot see itself.

`vord-xoo` — tenant invitation emails were rejected by Resend for months because the
sender was on an unverified domain. Nothing detected it. The only signal was a log
line nobody was reading.

`vord-8gq` — across two repos there are three metric instruments total and no
OpenTelemetry packages in any project. The cluster observability platform
(`vord-kn3`) is fully built — OTel collector agent and gateway, Prometheus,
Alertmanager, Tempo, Loki, Grafana — and receives nothing from the applications.

The first is an instance of the second. Fixing the sender address without adding
detection leaves the same class of silent failure in place.

## Part 1 — Email hardening (`vord-xoo`)

### Already done

| Change | Where | State |
| --- | --- | --- |
| Sender to `invitations@outreach.framlux.io`; billing-config `From` added (was unset) | stack `1ec5c3f` | pushed |
| Resend rejections log at Error, not Warning, with regression tests | vord `57a92a3` | committed, unpushed |
| Hardcoded `invitations@vordfleet.dev` fallback removed; `ResendOptionsValidator` fails startup on API-key-without-sender | vord `d012de0` | committed, unpushed |

The removed fallback mattered more than it looks: a blank `FromEmail` fell back to an
unverified domain, silently reproducing the outage the config fix was meant to end.
`billing-api` was in exactly that state.

### Remaining work

**1. Port the validator to `vord-internal` billing-api.**
`src/billing-api/Program.cs:103` binds `ResendOptions` with no validation, while
`InternalApiOptions` and `StripeCanaryOptions` on adjacent lines both use
`ValidateOnStart()`. `FromEmail` still defaults to empty. Current config supplies a
value, so it works today, but the silent-failure mode is untouched.

- Add `ResendOptionsValidator : IValidateOptions<ResendOptions>`, mirroring vord's:
  no API key passes (email is genuinely optional); an API key with blank `FromEmail`
  fails startup.
- Register beside `StripeCanaryOptionsValidator`, add `.ValidateOnStart()`.
- Audit `ResendEmailSender` for any hardcoded sender fallback.
- Unit tests: valid, missing, whitespace-only, no-key.

**2. Align stale test fixtures** still using `@vordfleet.dev`:
`vord-internal/test/billing/Services/ResendEmailSenderTests.cs`,
`vord-internal/test/billing/Configuration/StripeCanaryOptionsValidatorTests.cs`,
`vord/test/unit/services.core/Services/Notifications/ResendEmailServiceTests.cs:26`.

**3. Push.** Ten commits sit unpushed on vord `main`, including both fixes above.
Until pushed they are in no image. This is `vord-yp2` and gates everything.

**4. Prove delivery.** Send one real invitation to an external mailbox, confirm
receipt, record the Resend message ID in the bead. Separately confirm ArgoCD has
actually synced the configmap from `1ec5c3f`.

**5. File a follow-up bead** for migrating senders to `vordfleet.dev` once paying
customers justify the Resend paid tier — domain verification plus SPF/DKIM/DMARC.
Post-launch, linked to `vord-3ta` as related, not blocking. Must also cover
Alertmanager's `smtp_from`, which is on the same single verified domain.

### Exit criteria

Invitations demonstrably delivered, and `vord.email.send.failures` with its critical
alert (Part 2) in place. The alert is the durable guard — it catches a domain
becoming unverified in production, which no build-time test can.

## Part 2 — Instrumentation (`vord-8gq`, `vord-2eb`)

### Transport: OTLP push

Services push OTLP to `otel-gateway.framlux-observability.svc.cluster.local:4317`;
the gateway remote-writes metrics to Prometheus and forwards traces to Tempo. The
gateway's `prometheus` receiver is a narrow allowlist for infra components and is
not a path for application metrics.

Network path is already open — verified, not assumed: `gateway-otlp-cluster-only`
permits ingress on 4317 from `namespaceSelector: {}`, and vord-fleet's policies
declare `policyTypes: Ingress` only, so egress is unrestricted. **No NetworkPolicy
changes required.**

When `OTEL_EXPORTER_OTLP_ENDPOINT` is unset the SDK no-ops. This must never become
required configuration — self-hosters do not run a collector.

### Scope: four deployed .NET workloads

`api-server`, `services-worker`, `migration-runner` (vord), `billing-api`
(vord-internal). `web` and `marketing` are SvelteKit and out of scope. The agent is
out of scope — `vord-9kw` covers opt-in agent-side export, which is a privacy
decision, not a hardening task.

Registration is duplicated: ~40 lines in `vord/src/services.core/Extensions` and
again in `vord-internal/src/billing-api`. No shared NuGet package — cross-repo
publishing already has friction (`vord-kdu` exists because of it), and duplicating
40 lines is cheaper than maintaining a third published package.

### Packages

`OpenTelemetry.Extensions.Hosting`, `.Exporter.OpenTelemetryProtocol`,
`.Instrumentation.AspNetCore`, `.Instrumentation.Http`, `.Instrumentation.Runtime`.
Npgsql emits its own `Npgsql` meter and `ActivitySource` natively — `.AddMeter("Npgsql")`,
no extra package, which supplies DB pool saturation and DB spans. gRPC server
metrics and spans arrive via the AspNetCore instrumentation.

### Metrics

No per-tenant or per-machine dimensions. With unbounded tenants that multiplies
series without bound; `vord-tpy` already exists for trimming what does ship.

| Instrument | Type | Service |
| --- | --- | --- |
| `vord.telemetry.ingest.lag` | Histogram (s) | api-server |
| `vord.projection.hwm.lag` | Observable gauge | services-worker |
| `vord.hangfire.queue.depth` | Observable gauge (by queue) | services-worker |
| `vord.hangfire.jobs.failed` | Observable gauge | services-worker |
| `vord.alert.evaluation.duration` | Histogram | services-worker |
| `vord.redis.available` | Observable gauge 0/1 | api-server |
| `vord.machines.active` | Observable gauge | api-server |
| `vord.email.send.failures` | Counter (by status) | api-server, billing-api |
| `vord.stripe.sync.failures` | Counter | billing-api |
| `vord.service.heartbeat` | Counter | all four |

The existing `ClockSkewHistogram` and the two Redis fail-open counters are added to
the exported meter list rather than rewritten.

`migration-runner`, despite the name, does not run and exit. It is a long-running
`Deployment` serving `/healthz` and `/readyz`, executing migrations through a hosted
service and gating readiness on completion. The default 60s export interval is
therefore fine and no explicit flush is needed. Scope it to migration outcome and
duration, which are the only interesting signals it has.

### Traces

**Sampling: `AlwaysOnSampler` in every service.** The gateway already runs
`tail_sampling` with `keep-errors` (status `ERROR`), `keep-slow` (latency > 1000ms),
and `sample-the-rest` at 10%, with `decision_wait: 10s`. App-side ratio sampling
would discard spans before the gateway sees them, defeating `keep-errors` and
`keep-slow` — tail sampling requires receiving everything.

Instrumentation: AspNetCore (server spans including gRPC), HttpClient,
Grpc.Net.Client (outbound), Npgsql (DB spans). api-server to billing-api gRPC calls
propagate W3C `traceparent` automatically, so cross-repo traces stitch without extra
work. Agent to server is untraced, so traces begin at server ingress.

**Hangfire needs manual propagation and is the largest piece of this work.** Jobs
are enqueued by api-server and executed later in services-worker via Postgres, with
no auto-instrumentation. A coherent trace requires an `ActivitySource` plus
capturing `traceparent` at enqueue and restoring it at execution through a
`JobFilter`. Without it, background jobs appear as disconnected root spans and
"this webhook caused this job to fail" is unanswerable.

**Span attributes.** Unlike metrics, `tenant.id` and `machine.id` are valuable and
safe on spans — traces are sampled and stored per-trace, not as cardinality-multiplied
series. Never on spans: machine API keys, Resend or Stripe keys, session cookies,
user email. Requires a scrub pass on the ingest path, where the API key is present
in the request.

**Log correlation activates for free.** Both repos already log through
`RenderedCompactJsonFormatter`, and the Loki datasource is already provisioned with
a `TraceID` derived field matching Serilog's `"@tr"` and linking to Tempo. The
moment spans exist, log-to-trace navigation works with no further configuration.

**Capacity.** Tempo has a 10Gi PVC and 168h block retention; the gateway is tuned
for `expected_new_traces_per_sec: 100`. Four instrumented services on the ingest hot
path will likely exceed that. It is a sampling-quality sizing hint rather than a
hard cap — measure and re-tune after a day of real traffic. Disk is already covered
by the existing `PvcAlmostFull` warning.

### Alert rules (`stack`, `prometheus/alerts.yaml`)

Metric names in rules must be plain OTel names with dots to underscores and **no
unit suffix** — `vord_telemetry_ingest_lag`, not `..._seconds` — because the
exporter sets `add_metric_suffixes: false`. `resource_to_telemetry_conversion` is
enabled, so resource attributes arrive as labels and per-service rules key off
`service_name`.

Existing convention: `severity: critical` routes to `page-all` (Discord, email,
ntfy urgent, 1h repeat); anything else goes to Discord only.

**Critical — money and data loss:**
`VordStripeSyncFailing`, `VordEmailSendFailing`, `VordHangfireJobsFailing`, and
`VordServiceSilent` per service (`absent()` or no heartbeat increase over 5m,
mirroring the existing `PostgresUnreachable ... or absent(...)` pattern). The
silence alert exists because push-based export means a dead service goes quiet
rather than failing a scrape.

**Warning — lag and pressure:**
`VordTelemetryIngestLagHigh`, `VordProjectionLagHigh`, `VordHangfireQueueBacklog`,
`VordDbPoolSaturated`, `VordRedisUnavailable`.

Count-based criticals fire on `> 0` and need no baseline. The lag warnings are
guesses until production data exists — ship conservative thresholds and tune after a
week rather than treating the first numbers as correct.

Every rule gets a promtool test in `tests/rules/alerts_test.yaml`, already run by
CI's `validate.yaml`. This also reduces `vord-sdz`.

### RED dashboard (`vord-2eb`)

Rate, errors, and duration per service, sourced from the AspNetCore auto-instrumentation
metrics. Added as `dashboards/vord-red.json` and registered in the grafana
`configMapGenerator` file list beside the three existing dashboards, following their
house style. Datasource UIDs are `prometheus`, `tempo`, `loki`.

## Testing

- Unit: `MetricCollector<T>` from `Microsoft.Extensions.Diagnostics.Testing` asserting
  instruments record intended values, covering happy path, error cases, and boundaries.
- Functional: instrumentation registers without disturbing the existing pipeline.
- promtool: every new alert rule.
- Coverage target ~75-80% on new code, per repo convention.

## Sequence

Part 1 lands first as its own branch — it is hours of work and holds a P0, and
should not wait on multi-day work. Part 2 follows and closes both beads.

1. Email hardening: billing-api validator, fixtures, push, delivery proof, follow-up bead.
2. OTel scaffolding: metrics and traces, heartbeat, auto-instrumentation, all four
   workloads. Prove one metric reaches Prometheus and one trace reaches Tempo before
   writing any custom instrument.
3. Custom metric instruments per service.
4. Hangfire trace context propagation and span attribute scrubbing.
5. Alert rules and promtool tests; RED dashboard.
6. Production verification, tail-sampling re-tune, close `vord-xoo`, `vord-8gq`,
   `vord-2eb`.

Estimate: Part 1 under a day. Part 2 roughly 4-5 days, up from the bead's 2-3,
driven by Hangfire propagation and the dashboard.

## Out of scope

Agent-side OTLP (`vord-9kw`), metric cardinality trimming (`vord-tpy`), external
heartbeat (`vord-zdn`), `vordfleet.dev` sender migration (new bead).

## Noted separately

`stack/clusters/prod/apps/observability/base/prometheus/alerts.yaml` defines
`TelemetryPipelineFailing` twice, at lines 86 and 109. Legal across groups, but
Alertmanager's `group_by: [alertname, k8s_namespace_name]` and the inhibit rules key
on alertname, so the two collapse into one notification group. Worth its own look.
