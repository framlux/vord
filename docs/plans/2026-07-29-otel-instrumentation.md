# OpenTelemetry Instrumentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the platform observable — metrics and traces from all four deployed .NET workloads reaching the existing Prometheus and Tempo, with alert rules that page on money and data loss, closing `vord-8gq`, `vord-2eb`, and the remaining exit criterion of `vord-xoo`.

**Architecture:** Services push OTLP to the existing collector gateway, which remote-writes metrics to Prometheus and forwards traces to Tempo. The cluster platform is already built and receiving nothing from the applications; this plan is entirely app-side plus alert rules. Registration is duplicated rather than shared via a package — the tradeoff is recorded in the spec.

**Tech Stack:** .NET 10, OpenTelemetry .NET SDK, OTLP/gRPC exporter, Npgsql 10.0.2 (native meter and ActivitySource), Hangfire 1.8.20, Prometheus, Tempo, Grafana, promtool.

## Global Constraints

- Every code file starts with the three-line Framlux license header, copied verbatim from a neighbouring file.
- No `var` — explicit types only (error-level in `.editorconfig`).
- File-scoped namespaces; Allman braces; private fields `_camelCase`; no `this.` qualifier.
- XML doc comments on all public members. The build must be warning-free.
- No Yoda conditions. Never `!boolean` — write `(x == false)`. Parenthesise compound conditions.
- Logical operators stay on the same line as their operands.
- `using` directives in alphabetical order.
- Blank line before every `return` except when the preceding line is a comment. Blank line at end of file.
- One type per file.
- Tests run with `dotnet run --project <path>`, never `dotnet test`.
- **TUnit filter syntax:** `--treenode-filter` takes a path form, `/Assembly/Namespace/Class/Method`, so a class filter is `--treenode-filter "/*/*/VordMetricsTests/*"`. The `"*Name*"` form written in the steps below matches **nothing** and still exits zero. Treat a filtered run that reports zero tests as a failure and correct the filter — never as a pass.
- **No per-tenant or per-machine dimensions on any metric.** Tenants are unbounded; tags multiply series. Span attributes are exempt — traces are sampled per-trace, not stored as series.
- **Every service uses `AlwaysOnSampler`.** The gateway already tail-samples (`keep-errors`, `keep-slow` > 1000ms, `sample-the-rest` 10%). App-side sampling would discard the errors `keep-errors` exists to catch.
- **Metric names in alert rules use plain OTel names, dots to underscores, no unit suffix** — `vord_telemetry_ingest_lag`, never `vord_telemetry_ingest_lag_seconds`. The exporter sets `add_metric_suffixes: false`.
- `OTEL_EXPORTER_OTLP_ENDPOINT` unset must be a clean no-op. Self-hosters do not run a collector, so this must never become required configuration.
- Commit messages: no AI attribution, no `Co-Authored-By`, no session footer.
- No review IDs, task numbers, or phase labels in code or comments.

## File Structure

**`vord`:**
- Create `src/services.core/Observability/VordMetrics.cs` — the single `Meter` and every custom instrument shared by api-server and services-worker. One file so instrument names cannot drift.
- Create `src/services.core/Observability/VordActivitySource.cs` — the shared `ActivitySource` for manual spans.
- Create `src/services.core/Observability/HangfireTracingFilter.cs` — trace-context capture at enqueue and restore at execution.
- Modify `src/services.core/Extensions/ServiceCollectionExtensions.cs` — add `AddCoreObservability`, mirroring the existing `AddCoreSerilog`.
- Modify `src/server/Program.cs`, `src/services.worker/Program.cs` — call it.
- Modify `src/migrationRunner/Program.cs` — standalone registration (see Task 4 for why it does not use the shared one).
- Modify instrument-owning services listed per task.

**`vord-internal`:**
- Create `src/billing-api/Observability/BillingMetrics.cs`, `src/billing-api/Observability/ObservabilityExtensions.cs`.
- Modify `src/billing-api/Program.cs`.

**`stack`:**
- Modify `clusters/prod/apps/observability/base/prometheus/alerts.yaml`, `tests/rules/alerts_test.yaml`.
- Create `clusters/prod/apps/observability/base/grafana/dashboards/vord-red.json`; modify that directory's `kustomization.yaml`.
- Modify `clusters/prod/apps/vord-platform/base/kustomization.yaml` — the OTLP endpoint for each service.

---

### Task 1: Shared observability registration in services.core

**Files:**
- Create: `vord/src/services.core/Observability/VordMetrics.cs`
- Create: `vord/src/services.core/Observability/VordActivitySource.cs`
- Modify: `vord/src/services.core/Extensions/ServiceCollectionExtensions.cs` (after `AddCoreSerilog`, line 57)
- Modify: `vord/src/services.core/services.core.csproj`
- Test: `vord/test/unit/services.core/Observability/VordMetricsTests.cs`

**Interfaces:**
- Produces:
  - `public static class VordMetrics` in `Framlux.FleetManagement.Services.Core.Observability` with `public const string MeterName = "Framlux.FleetManagement.Vord"` and the instruments added in later tasks.
  - `public static class VordActivitySource` with `public const string Name = "Framlux.FleetManagement.Vord"` and `public static readonly ActivitySource Instance`.
  - `public static IServiceCollection AddCoreObservability(this IServiceCollection services, IConfiguration configuration, string serviceName)`.

- [ ] **Step 1: Add the packages**

```bash
dotnet add src/services.core/services.core.csproj package OpenTelemetry.Extensions.Hosting
dotnet add src/services.core/services.core.csproj package OpenTelemetry.Exporter.OpenTelemetryProtocol
dotnet add src/services.core/services.core.csproj package OpenTelemetry.Instrumentation.AspNetCore
dotnet add src/services.core/services.core.csproj package OpenTelemetry.Instrumentation.Http
dotnet add src/services.core/services.core.csproj package OpenTelemetry.Instrumentation.GrpcNetClient
dotnet add src/services.core/services.core.csproj package OpenTelemetry.Instrumentation.Runtime
dotnet add src/services.core/services.core.csproj package Npgsql.OpenTelemetry
```

Then re-sort the `PackageReference` block alphabetically to match the existing file. Npgsql 10.0.2 is already referenced and exposes its metrics through a meter named `Npgsql` with no extra package; `Npgsql.OpenTelemetry` is only needed for the tracing side.

- [ ] **Step 2: Write the failing test**

Create `vord/test/unit/services.core/Observability/VordMetricsTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Tests for the shared metric and trace identities. These names are a contract: alert rules in
/// the stack repository query them, so a rename here silently breaks paging.
/// </summary>
public sealed class VordMetricsTests
{
    /// <summary>
    /// The meter name is referenced by the exporter registration and must not drift.
    /// </summary>
    [Test]
    public async Task MeterName_IsStable()
    {
        await Assert.That(VordMetrics.MeterName).IsEqualTo("Framlux.FleetManagement.Vord");
    }

    /// <summary>
    /// The activity source name is referenced by the tracing registration and must not drift.
    /// </summary>
    [Test]
    public async Task ActivitySourceName_IsStable()
    {
        await Assert.That(VordActivitySource.Name).IsEqualTo("Framlux.FleetManagement.Vord");
        await Assert.That(VordActivitySource.Instance.Name).IsEqualTo(VordActivitySource.Name);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*VordMetricsTests*"
```

Expected: compile failure — the types do not exist.

- [ ] **Step 4: Create the metric and trace identities**

`vord/src/services.core/Observability/VordMetrics.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Owns the single meter that every custom Vord instrument is created from. Instruments live here
/// rather than beside the code that records them so that their names, units, and descriptions sit
/// in one reviewable place — the alert rules in the stack repository query these names, so a
/// silent rename breaks paging rather than failing a build.
/// </summary>
public static class VordMetrics
{
    /// <summary>
    /// The meter name registered with the OpenTelemetry exporter.
    /// </summary>
    public const string MeterName = "Framlux.FleetManagement.Vord";

    /// <summary>
    /// The meter that owns every custom Vord instrument.
    /// </summary>
    public static readonly Meter Meter = new(MeterName);
}
```

`vord/src/services.core/Observability/VordActivitySource.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// The activity source used for spans this codebase creates by hand, as opposed to the spans the
/// framework instrumentation produces automatically. Currently used for Hangfire job execution,
/// which has no auto-instrumentation and would otherwise appear as disconnected root spans.
/// </summary>
public static class VordActivitySource
{
    /// <summary>
    /// The activity source name registered with the OpenTelemetry tracer provider.
    /// </summary>
    public const string Name = "Framlux.FleetManagement.Vord";

    /// <summary>
    /// The shared activity source instance.
    /// </summary>
    public static readonly ActivitySource Instance = new(Name);
}
```

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*VordMetricsTests*"
```

Expected: 2 tests pass.

- [ ] **Step 6: Add the registration extension**

In `vord/src/services.core/Extensions/ServiceCollectionExtensions.cs`, insert immediately after `AddCoreSerilog` (which ends at line 57):

```csharp
    /// <summary>
    /// Registers OpenTelemetry metrics and traces, exporting OTLP to the collector gateway.
    /// When no OTLP endpoint is configured this is a no-op, because self-hosted deployments do
    /// not run a collector and must not be forced to.
    /// Sampling is deliberately AlwaysOn: the gateway performs tail sampling, keeping all errors
    /// and all slow traces and 10% of the rest. Sampling here instead would discard those spans
    /// before the gateway ever sees them.
    /// </summary>
    public static IServiceCollection AddCoreObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        string? otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        if (string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            return services;
        }

        string serviceVersion = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "unknown";

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: serviceName, serviceVersion: serviceVersion)
                .AddAttributes([new KeyValuePair<string, object>("service.namespace", "vord")]))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql")
                .AddMeter(VordMetrics.MeterName)
                .AddMeter("Framlux.FleetManagement.Server.Telemetry")
                .AddMeter("Framlux.FleetManagement.Server.RateLimiting")
                .AddMeter("Framlux.FleetManagement.Services.Core.TelemetryDedup")
                .AddOtlpExporter())
            .WithTracing(tracing => tracing
                .SetSampler(new AlwaysOnSampler())
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddGrpcClientInstrumentation()
                .AddNpgsql()
                .AddSource(VordActivitySource.Name)
                .AddOtlpExporter());

        return services;
    }
```

The three string meter names are the pre-existing instruments (`ClockSkewHistogram`, the rate-limiter fail-open counter, and the dedup fail-open counter). They are referenced as literals rather than by their `MeterName` constants because two of them live in the `server` project, which `services.core` does not reference. Task 3 adds a test that fails if any of them is renamed.

Add to the file's using block, preserving alphabetical order: `Framlux.FleetManagement.Services.Core.Observability`, `Npgsql`, `OpenTelemetry.Metrics`, `OpenTelemetry.Resources`, `OpenTelemetry.Trace`, `System.Reflection`.

`AddOtlpExporter()` with no arguments reads `OTEL_EXPORTER_OTLP_ENDPOINT` from the environment itself, which is why the endpoint is only read above to decide whether to register at all.

- [ ] **Step 7: Build**

```bash
dotnet build machine-info.slnx
```

Expected: zero errors, zero warnings.

- [ ] **Step 8: Commit**

```bash
git add src/services.core/Observability src/services.core/Extensions/ServiceCollectionExtensions.cs src/services.core/services.core.csproj test/unit/services.core/Observability/VordMetricsTests.cs
git commit -m "Add OpenTelemetry registration and shared metric identities"
```

---

### Task 2: Heartbeat instrument and service-silence detection

Push-based export means a dead service goes quiet rather than failing a scrape. A heartbeat counter plus an `absent()` alert is what converts silence into a page. This lands before any domain metric so that the whole path — instrument, gateway, Prometheus, alert rule — is proven end to end while there is only one moving part.

**Files:**
- Modify: `vord/src/services.core/Observability/VordMetrics.cs`
- Create: `vord/src/services.core/Observability/HeartbeatService.cs`
- Modify: `vord/src/services.core/Extensions/ServiceCollectionExtensions.cs`
- Test: `vord/test/unit/services.core/Observability/HeartbeatServiceTests.cs`

**Interfaces:**
- Consumes: `VordMetrics.Meter`.
- Produces: `VordMetrics.Heartbeat` of type `Counter<long>`; `public sealed class HeartbeatService : BackgroundService` taking `(TimeProvider timeProvider)`.

- [ ] **Step 1: Write the failing test**

Create `vord/test/unit/services.core/Observability/HeartbeatServiceTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Tests for <see cref="HeartbeatService"/>. The heartbeat exists so that a service which stops
/// exporting can be alerted on: with push-based OTLP there is no scrape to fail, so silence is
/// the only signal, and silence needs something that was previously non-silent.
/// </summary>
public sealed class HeartbeatServiceTests
{
    /// <summary>
    /// The counter must increment once per interval so an absent() rule can detect silence.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_AdvancesTime_IncrementsHeartbeat()
    {
        using MetricCollector<long> collector = new(VordMetrics.Meter, "vord.service.heartbeat");
        FakeTimeProvider timeProvider = new();
        HeartbeatService service = new(timeProvider);

        await service.StartAsync(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None);

        await Assert.That(collector.GetMeasurementSnapshot().Count).IsGreaterThanOrEqualTo(2);
    }
}
```

Never use wall-clock delays here — inject `TimeProvider` and drive it with `FakeTimeProvider`. If `Microsoft.Extensions.TimeProvider.Testing` or `Microsoft.Extensions.Diagnostics.Testing` are not already referenced by `test/unit/services.core/unit.services.core.csproj`, add them:

```bash
dotnet add test/unit/services.core/unit.services.core.csproj package Microsoft.Extensions.Diagnostics.Testing
dotnet add test/unit/services.core/unit.services.core.csproj package Microsoft.Extensions.TimeProvider.Testing
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*HeartbeatServiceTests*"
```

Expected: compile failure — `HeartbeatService` does not exist.

- [ ] **Step 3: Add the instrument**

Append to `VordMetrics`:

```csharp
    /// <summary>
    /// Incremented on a fixed interval by every service. Its absence is the signal that a service
    /// has stopped exporting: with push-based OTLP there is no failing scrape to alert on.
    /// </summary>
    public static readonly Counter<long> Heartbeat = Meter.CreateCounter<long>(
        "vord.service.heartbeat",
        description: "Incremented on a fixed interval to prove the service is still exporting.");
```

- [ ] **Step 4: Add the background service**

Create `vord/src/services.core/Observability/HeartbeatService.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Hosting;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Increments the heartbeat counter on a fixed interval so that an alert rule can detect a
/// service which has stopped exporting metrics entirely.
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeartbeatService"/> class.
    /// </summary>
    public HeartbeatService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval, _timeProvider);

        VordMetrics.Heartbeat.Add(1);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                VordMetrics.Heartbeat.Add(1);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested; stopping is the correct response.
        }
    }
}
```

The first `Add` happens before the loop so a service that is shut down quickly still reports at least once.

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*HeartbeatServiceTests*"
```

Expected: PASS.

- [ ] **Step 6: Register it inside AddCoreObservability**

Inside `AddCoreObservability`, immediately before the final `return services;`:

```csharp
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<HeartbeatService>();
```

Add `using Microsoft.Extensions.DependencyInjection.Extensions;` in alphabetical order. `TryAddSingleton` is used so a service that already registers its own `TimeProvider` keeps it — several tests substitute a fake one.

Registering inside the guarded section means no heartbeat when telemetry is disabled, which is correct: nothing would export it.

- [ ] **Step 7: Build and run the full services.core suite**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
```

Expected: zero warnings, all tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/services.core/Observability src/services.core/Extensions/ServiceCollectionExtensions.cs test/unit/services.core/Observability/HeartbeatServiceTests.cs test/unit/services.core/unit.services.core.csproj
git commit -m "Add a service heartbeat so silence can be alerted on"
```

---

### Task 3: Guard the pre-existing meter names

`AddCoreObservability` references three meter names as string literals because they live in projects `services.core` cannot see. A rename would compile cleanly and silently stop exporting those instruments.

**Files:**
- Test: `vord/test/unit/server/Observability/MeterRegistrationTests.cs`

**Interfaces:**
- Consumes: `TelemetryService.MeterName`, `RedisRateLimiter.MeterName`, `RedisTelemetryDeduplicationService.MeterName` — all `public const string`.
- Produces: nothing.

- [ ] **Step 1: Write the test**

Create `vord/test/unit/server/Observability/MeterRegistrationTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Services.Core.Telemetry;

namespace Framlux.FleetManagement.Test.Server.Observability;

/// <summary>
/// AddCoreObservability registers these meters by string literal, because they live in projects
/// services.core does not reference. Renaming a constant would compile cleanly and silently stop
/// exporting the instrument, so the literals are pinned here.
/// </summary>
public sealed class MeterRegistrationTests
{
    /// <summary>
    /// The telemetry ingest meter name registered with the exporter.
    /// </summary>
    [Test]
    public async Task TelemetryMeterName_MatchesExporterRegistration()
    {
        await Assert.That(TelemetryService.MeterName).IsEqualTo("Framlux.FleetManagement.Server.Telemetry");
    }

    /// <summary>
    /// The rate limiter meter name registered with the exporter.
    /// </summary>
    [Test]
    public async Task RateLimiterMeterName_MatchesExporterRegistration()
    {
        await Assert.That(RedisRateLimiter.MeterName).IsEqualTo("Framlux.FleetManagement.Server.RateLimiting");
    }

    /// <summary>
    /// The dedup meter name registered with the exporter.
    /// </summary>
    [Test]
    public async Task DedupMeterName_MatchesExporterRegistration()
    {
        await Assert.That(RedisTelemetryDeduplicationService.MeterName)
            .IsEqualTo("Framlux.FleetManagement.Services.Core.TelemetryDedup");
    }
}
```

Correct the `using` namespaces if they differ from the actual declarations — verify each with the LSP `goToDefinition` rather than guessing, since these are the exact names the test exists to pin.

- [ ] **Step 2: Run the test**

```bash
dotnet run --project test/unit/server/unit.server.csproj --treenode-filter "*MeterRegistrationTests*"
```

Expected: 3 tests pass immediately. This is a pinning test, not a red-green cycle.

- [ ] **Step 3: Commit**

```bash
git add test/unit/server/Observability/MeterRegistrationTests.cs
git commit -m "Pin the meter names the exporter registers by literal"
```

---

### Task 4: Wire the three vord workloads

**Files:**
- Modify: `vord/src/server/Program.cs`
- Modify: `vord/src/services.worker/Program.cs` (after `builder.Host.AddCoreSerilog();`, line 30)
- Modify: `vord/src/migrationRunner/Program.cs`
- Modify: `vord/src/migrationRunner/migrationRunner.csproj`

**Interfaces:**
- Consumes: `AddCoreObservability(IServiceCollection, IConfiguration, string serviceName)`.
- Produces: nothing new.

The `serviceName` values must match the Kubernetes workload names exactly — `api-server`, `services-worker`, `migration-runner` — because `resource_to_telemetry_conversion` turns them into the `service_name` label the alert rules key on.

- [ ] **Step 1: Wire api-server and services-worker**

In `vord/src/services.worker/Program.cs`, immediately after `builder.Host.AddCoreSerilog();`:

```csharp
builder.Services.AddCoreObservability(builder.Configuration, "services-worker");
```

In `vord/src/server/Program.cs`, add the equivalent immediately after its own `AddCoreSerilog` call:

```csharp
builder.Services.AddCoreObservability(builder.Configuration, "api-server");
```

- [ ] **Step 2: Wire migrationRunner with its own registration**

`migrationRunner.csproj` references only `database.csproj`, not `services.core`. Adding that reference would pull Hangfire, Redis, the billing gRPC client, and the AWS SDK into the migration image for no benefit. Duplicate the registration instead, consistent with the spec's decision to duplicate rather than publish a shared package.

Add packages:

```bash
dotnet add src/migrationRunner/migrationRunner.csproj package OpenTelemetry.Extensions.Hosting
dotnet add src/migrationRunner/migrationRunner.csproj package OpenTelemetry.Exporter.OpenTelemetryProtocol
dotnet add src/migrationRunner/migrationRunner.csproj package OpenTelemetry.Instrumentation.AspNetCore
dotnet add src/migrationRunner/migrationRunner.csproj package OpenTelemetry.Instrumentation.Runtime
```

In `vord/src/migrationRunner/Program.cs`, immediately before `WebApplication app = builder.Build();`:

```csharp
// OpenTelemetry export. Registered inline rather than through services.core, which this project
// deliberately does not reference — pulling it in would drag Hangfire, Redis and the billing
// client into the migration image. No OTLP endpoint means no telemetry, which is the supported
// self-hosted configuration.
string? otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

if (string.IsNullOrWhiteSpace(otlpEndpoint) == false)
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource
            .AddService(serviceName: "migration-runner")
            .AddAttributes([new KeyValuePair<string, object>("service.namespace", "vord")]))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter("Npgsql")
            .AddOtlpExporter())
        .WithTracing(tracing => tracing
            .SetSampler(new AlwaysOnSampler())
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter());
}
```

Add `using OpenTelemetry.Metrics;`, `using OpenTelemetry.Resources;`, `using OpenTelemetry.Trace;` in alphabetical order.

Note this service is long-running despite its name — a `Deployment` serving `/healthz` and `/readyz`, running migrations through a hosted service and gating readiness on completion. No explicit provider flush is needed; the default export interval is fine.

- [ ] **Step 3: Add the endpoint to the fleet config**

In `stack/clusters/prod/apps/vord-platform/base/kustomization.yaml`, add to the fleet `configMapGenerator` literals block (the one containing `Resend__FromEmail` around line 40):

```yaml
      - OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-gateway.framlux-observability.svc.cluster.local:4317
      - OTEL_EXPORTER_OTLP_PROTOCOL=grpc
```

Confirm the same configmap is consumed by the api-server, services-worker, and migration-runner deployments. If any of the three loads a different configmap, add the two literals there too — a workload without the endpoint silently exports nothing, which is exactly the failure this whole plan exists to eliminate.

No NetworkPolicy change is required: `gateway-otlp-cluster-only` accepts ingress on 4317 from `namespaceSelector: {}`, and the vord-fleet policies declare `policyTypes: Ingress` only, so egress is unrestricted.

- [ ] **Step 4: Build and test**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/web/functional.web.csproj
```

Expected: zero warnings, all suites pass. Functional tests run without an OTLP endpoint configured, so the registration no-ops and behaviour is unchanged — that is itself the assertion that the guard works.

- [ ] **Step 5: Commit**

```bash
git -C vord add src/server/Program.cs src/services.worker/Program.cs src/migrationRunner/Program.cs src/migrationRunner/migrationRunner.csproj
git -C vord commit -m "Export OpenTelemetry from the three fleet workloads"

git -C stack add clusters/prod/apps/vord-platform/base/kustomization.yaml
git -C stack commit -m "Point the fleet workloads at the collector gateway"
```

---

### Task 5: Prove the pipeline end to end before writing domain metrics

No code. This gate exists because every later task assumes the transport works, and debugging a broken export path through ten instruments is far worse than through one.

**Files:** none modified.

**Interfaces:** none.

- [ ] **Step 1: Deploy and confirm the pods restarted with the endpoint set**

```bash
kubectl -n vord-fleet get pods
kubectl -n vord-fleet exec deploy/api-server -- printenv OTEL_EXPORTER_OTLP_ENDPOINT
```

Expected: `http://otel-gateway.framlux-observability.svc.cluster.local:4317`.

- [ ] **Step 2: Confirm the gateway is accepting the data**

```bash
kubectl -n framlux-observability logs deploy/otel-gateway --since=5m | grep -i -E "refused|error|permission"
```

Expected: no connection refusals from the vord-fleet namespace.

- [ ] **Step 3: Confirm the heartbeat reached Prometheus**

Query Prometheus for `vord_service_heartbeat`. Expected: one series per service, with `service_name` values `api-server`, `services-worker`, and `migration-runner`, each increasing.

If the metric is absent but the gateway shows no errors, check whether the name arrived with a unit suffix — `add_metric_suffixes: false` is set on the exporter, so it should not have one. The alert rules in Task 10 depend on this being right.

- [ ] **Step 4: Confirm traces reached Tempo**

Issue an HTTP request against the fleet API, then search Tempo in Grafana for `service.name = "api-server"`. Expected: a span for the request, with Npgsql child spans if the request touched the database.

- [ ] **Step 5: Confirm log-to-trace correlation works**

Open the same request's log line in Grafana's Loki view. Expected: a `TraceID` link that resolves to the Tempo trace from Step 4. Both services already log through `RenderedCompactJsonFormatter` and the Loki datasource already has the `"@tr"` derived field, so this should work with no configuration — if the link is missing, the trace context is not reaching the logger and that must be fixed before continuing.

- [ ] **Step 6: Record the baseline**

Note the observed trace volume per second. Task 11 uses it to decide whether the gateway's `expected_new_traces_per_sec: 100` needs raising.

---

### Task 6: api-server domain metrics

**Files:**
- Modify: `vord/src/services.core/Observability/VordMetrics.cs`
- Modify: `vord/src/server/Endpoints/grpc/TelemetryService.cs` — record ingest lag
- Modify: `vord/src/services.core/Services/Notifications/ResendEmailService.cs` — record send failures
- Modify: the Redis availability and active-machine sources identified below
- Test: `vord/test/unit/services.core/Observability/VordMetricsRecordingTests.cs`

**Interfaces:**
- Consumes: `VordMetrics.Meter`.
- Produces: `VordMetrics.TelemetryIngestLag` (`Histogram<double>`), `VordMetrics.EmailSendFailures` (`Counter<long>`), plus observable gauges registered via `Meter.CreateObservableGauge`.

- [ ] **Step 1: Write the failing tests**

Create `vord/test/unit/services.core/Observability/VordMetricsRecordingTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Tests that the shared instruments record what their names claim, and that none of them carry
/// tenant or machine dimensions — those are unbounded and would multiply series without limit.
/// </summary>
public sealed class VordMetricsRecordingTests
{
    /// <summary>
    /// Ingest lag is recorded in seconds as a measurement value.
    /// </summary>
    [Test]
    public async Task TelemetryIngestLag_RecordsSeconds()
    {
        using MetricCollector<double> collector = new(VordMetrics.Meter, "vord.telemetry.ingest.lag");

        VordMetrics.TelemetryIngestLag.Record(2.5);

        CollectedMeasurement<double> measurement = collector.GetMeasurementSnapshot().Single();
        await Assert.That(measurement.Value).IsEqualTo(2.5);
        await Assert.That(measurement.Tags.ContainsKey("tenant.id")).IsFalse();
        await Assert.That(measurement.Tags.ContainsKey("machine.id")).IsFalse();
    }

    /// <summary>
    /// Email failures are counted and tagged by status only — a bounded dimension.
    /// </summary>
    [Test]
    public async Task EmailSendFailures_TagsStatusOnly()
    {
        using MetricCollector<long> collector = new(VordMetrics.Meter, "vord.email.send.failures");

        VordMetrics.EmailSendFailures.Add(1, new KeyValuePair<string, object?>("status", 403));

        CollectedMeasurement<long> measurement = collector.GetMeasurementSnapshot().Single();
        await Assert.That(measurement.Value).IsEqualTo(1);
        await Assert.That(measurement.Tags["status"]).IsEqualTo(403);
        await Assert.That(measurement.Tags.ContainsKey("tenant.id")).IsFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*VordMetricsRecordingTests*"
```

Expected: compile failure — the instruments do not exist.

- [ ] **Step 3: Add the instruments**

Append to `VordMetrics`:

```csharp
    /// <summary>
    /// Seconds between an agent producing a telemetry envelope and the server accepting it.
    /// Recorded as a measurement value rather than a tag so cardinality stays bounded.
    /// </summary>
    public static readonly Histogram<double> TelemetryIngestLag = Meter.CreateHistogram<double>(
        "vord.telemetry.ingest.lag",
        unit: "s",
        description: "Seconds between agent envelope production and server acceptance.");

    /// <summary>
    /// Outbound email rejected by the provider, tagged with the HTTP status only. This is the
    /// guard for the invitation outage in which every send was rejected and the only trace was a
    /// log line nobody read.
    /// </summary>
    public static readonly Counter<long> EmailSendFailures = Meter.CreateCounter<long>(
        "vord.email.send.failures",
        description: "Outbound emails rejected by the provider.");
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*VordMetricsRecordingTests*"
```

Expected: 2 tests pass.

- [ ] **Step 5: Record ingest lag**

In `vord/src/server/Endpoints/grpc/TelemetryService.cs`, in the envelope handling path that already computes clock skew against the agent timestamp, record the lag alongside the existing `ClockSkewHistogram` call. Reuse the timestamp already parsed for the skew check rather than reading the clock a second time, so the two instruments cannot disagree.

```csharp
VordMetrics.TelemetryIngestLag.Record((receivedAt - envelopeProducedAt).TotalSeconds);
```

Substitute the actual local variable names present in that method. Add `using Framlux.FleetManagement.Services.Core.Observability;` in alphabetical order.

- [ ] **Step 6: Record email send failures**

In `vord/src/services.core/Services/Notifications/ResendEmailService.cs`, at each of the two non-2xx branches that commit `57a92a3` escalated to `LogError` — one in `SendInvitationEmailAsync`, one in `SendAlertEmailAsync` — add immediately after the log call:

```csharp
VordMetrics.EmailSendFailures.Add(1, new KeyValuePair<string, object?>("status", (int)response.StatusCode));
```

Use the response variable name already in scope. This is the instrument that finally closes `vord-xoo`'s second exit criterion.

- [ ] **Step 7: Add the observable gauges**

Append to `VordMetrics` a registration method, called once from `AddCoreObservability` for api-server only:

```csharp
    /// <summary>
    /// Registers gauges that are observed on collection rather than recorded at a call site.
    /// Callbacks must be cheap and must not perform I/O — they run on the exporter's schedule.
    /// </summary>
    public static void RegisterApiServerGauges(
        Func<int> redisAvailable,
        Func<long> activeMachines)
    {
        Meter.CreateObservableGauge(
            "vord.redis.available",
            redisAvailable,
            description: "1 when Redis is reachable, 0 when the service is running fail-open.");

        Meter.CreateObservableGauge(
            "vord.machines.active",
            activeMachines,
            description: "Machines currently reporting telemetry across all tenants.");
    }
```

Wire the callbacks from api-server's `Program.cs` after the container is built. Source `redisAvailable` from the `IConnectionMultiplexer` registered in `ServiceCollectionExtensions` (`IsConnected`), and `activeMachines` from a cached value maintained by the existing machine-ping service — **not** a database query. The callback runs on every export and a query here would put a periodic full scan on the ingest path. If no cached count exists, add one to the ping service rather than querying inside the callback.

- [ ] **Step 8: Build and run the suites**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
```

Expected: zero warnings, all pass.

- [ ] **Step 9: Commit**

```bash
git add src/services.core/Observability/VordMetrics.cs src/server src/services.core/Services/Notifications/ResendEmailService.cs test/unit/services.core/Observability/VordMetricsRecordingTests.cs
git commit -m "Instrument telemetry ingest lag, Redis availability, active machines and email failures"
```

---

### Task 7: services-worker domain metrics

**Files:**
- Modify: `vord/src/services.core/Observability/VordMetrics.cs`
- Modify: `vord/src/services.worker/Program.cs`
- Modify: the alert evaluation service in `vord/src/services.core/Alerts/`
- Test: `vord/test/unit/services.core/Observability/WorkerMetricsTests.cs`

**Interfaces:**
- Consumes: `VordMetrics.Meter`, `MachineStateProjectionCursor` (`src/database/Models/`), Hangfire's `IMonitoringApi`.
- Produces: `VordMetrics.AlertEvaluationDuration` (`Histogram<double>`) and `VordMetrics.RegisterWorkerGauges(Func<double> projectionLagSeconds, Func<IEnumerable<Measurement<long>>> queueDepth, Func<long> failedJobs)`.

- [ ] **Step 1: Write the failing test**

Create `vord/test/unit/services.core/Observability/WorkerMetricsTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Tests for the worker-owned instruments. Queue depth is tagged by queue name, which is a
/// bounded set defined in code; nothing here is tagged by tenant.
/// </summary>
public sealed class WorkerMetricsTests
{
    /// <summary>
    /// Alert evaluation duration is recorded in seconds.
    /// </summary>
    [Test]
    public async Task AlertEvaluationDuration_RecordsSeconds()
    {
        using MetricCollector<double> collector = new(VordMetrics.Meter, "vord.alert.evaluation.duration");

        VordMetrics.AlertEvaluationDuration.Record(0.42);

        CollectedMeasurement<double> measurement = collector.GetMeasurementSnapshot().Single();
        await Assert.That(measurement.Value).IsEqualTo(0.42);
        await Assert.That(measurement.Tags.ContainsKey("tenant.id")).IsFalse();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*WorkerMetricsTests*"
```

Expected: compile failure.

- [ ] **Step 3: Add the instruments**

Append to `VordMetrics`:

```csharp
    /// <summary>
    /// Wall-clock seconds taken by a full alert-rule evaluation sweep.
    /// </summary>
    public static readonly Histogram<double> AlertEvaluationDuration = Meter.CreateHistogram<double>(
        "vord.alert.evaluation.duration",
        unit: "s",
        description: "Seconds taken by a full alert-rule evaluation sweep.");

    /// <summary>
    /// Registers the worker-side observable gauges. Callbacks read cached values only — they run
    /// on the exporter's schedule and must never query the database or Hangfire storage directly.
    /// </summary>
    public static void RegisterWorkerGauges(
        Func<double> projectionLagSeconds,
        Func<IEnumerable<Measurement<long>>> queueDepth,
        Func<long> failedJobs)
    {
        Meter.CreateObservableGauge(
            "vord.projection.hwm.lag",
            projectionLagSeconds,
            unit: "s",
            description: "Seconds between the newest machine state row and the projection high-water mark.");

        Meter.CreateObservableGauge(
            "vord.hangfire.queue.depth",
            queueDepth,
            description: "Enqueued Hangfire jobs, by queue.");

        Meter.CreateObservableGauge(
            "vord.hangfire.jobs.failed",
            failedJobs,
            description: "Jobs currently in the Hangfire failed state.");
    }
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*WorkerMetricsTests*"
```

Expected: PASS.

- [ ] **Step 5: Record alert evaluation duration**

In the alert evaluation sweep in `vord/src/services.core/Alerts/`, wrap the sweep body:

```csharp
long startTimestamp = Stopwatch.GetTimestamp();

// existing sweep body

VordMetrics.AlertEvaluationDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
```

Use `Stopwatch.GetTimestamp` rather than `TimeProvider`: this measures elapsed duration, not a point in time, and must not be affected by a fake clock in tests. Place the `Record` so it runs even when the sweep throws, using `try`/`finally`, so a failing sweep still reports how long it ran.

- [ ] **Step 6: Add a cached-value poller and wire the gauges**

Observable-gauge callbacks run inside the export path. A database query or Hangfire storage read there would stall the exporter on every collection, so the poller owns all I/O and the callbacks are pure field reads.

Create `vord/src/services.worker/Observability/WorkerGaugePoller.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Observability;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.ServicesWorker.Observability;

/// <summary>
/// Refreshes the values behind the worker's observable gauges on a fixed interval. The gauge
/// callbacks registered with the meter read these cached fields and nothing else: they are invoked
/// on the exporter's schedule, so any I/O there would block metric collection.
/// A refresh failure deliberately leaves the previous value in place rather than propagating —
/// a transient database blip must not take the metrics down with it.
/// </summary>
public sealed class WorkerGaugePoller : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkerGaugePoller> _logger;

    private double _projectionLagSeconds;
    private long _failedJobs;
    private IReadOnlyList<Measurement<long>> _queueDepth = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkerGaugePoller"/> class.
    /// </summary>
    public WorkerGaugePoller(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<WorkerGaugePoller> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;

        VordMetrics.RegisterWorkerGauges(
            () => _projectionLagSeconds,
            () => _queueDepth,
            () => _failedJobs);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval, _timeProvider);

        await RefreshAsync(stoppingToken);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested; stopping is the correct response.
        }
    }

    /// <summary>
    /// Reads the current values into the cached fields. Exposed for testing so the refresh can be
    /// driven directly without running the timer loop.
    /// </summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            IMachineStateRepository machineState =
                scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

            DateTimeOffset? newestRow = await machineState.GetNewestStateTimestampAsync(ct);
            DateTimeOffset? cursor = await machineState.GetProjectionCursorTimestampAsync(ct);

            if ((newestRow is not null) && (cursor is not null))
            {
                _projectionLagSeconds = Math.Max(0, (newestRow.Value - cursor.Value).TotalSeconds);
            }

            IMonitoringApi monitoring = JobStorage.Current.GetMonitoringApi();
            _failedJobs = monitoring.FailedCount();
            _queueDepth = monitoring
                .Queues()
                .Select(queue => new Measurement<long>(
                    queue.Length,
                    new KeyValuePair<string, object?>("queue", queue.Name)))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh worker gauge values; keeping the previous values");
        }
    }
}
```

If `IMachineStateRepository` does not already expose `GetNewestStateTimestampAsync` and `GetProjectionCursorTimestampAsync`, add them to the interface and its implementation in `src/database/Repositories/DatabaseRepository.MachineState.cs`, reading the newest `MachineStateSummary` timestamp and the `MachineStateProjectionCursor` high-water mark respectively. Use the LSP to confirm the existing member names before adding — the repository already reads the cursor somewhere, and reusing that method is better than adding a near-duplicate.

Register it in `vord/src/services.worker/Program.cs` alongside the other hosted services:

```csharp
builder.Services.AddHostedService<WorkerGaugePoller>();
```

- [ ] **Step 6b: Test the poller**

Create `vord/test/unit/services.core/Observability/WorkerGaugePollerTests.cs` — or the worker's own unit project if one exists — asserting with `FakeTimeProvider` and NSubstitute repositories that:

```csharp
    /// <summary>
    /// A refresh reads the repository and updates the cached lag value.
    /// </summary>
    [Test]
    public async Task RefreshAsync_UpdatesProjectionLag()
    {
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetNewestStateTimestampAsync(Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UnixEpoch.AddSeconds(500));
        repository.GetProjectionCursorTimestampAsync(Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UnixEpoch.AddSeconds(200));

        using MetricCollector<double> collector = new(VordMetrics.Meter, "vord.projection.hwm.lag");
        WorkerGaugePoller poller = CreatePoller(repository);

        await poller.RefreshAsync(CancellationToken.None);
        collector.RecordObservableInstruments();

        await Assert.That(collector.GetMeasurementSnapshot().Last().Value).IsEqualTo(300);
    }

    /// <summary>
    /// A repository failure must leave the previous value intact rather than throwing into the
    /// export path.
    /// </summary>
    [Test]
    public async Task RefreshAsync_RepositoryThrows_KeepsPreviousValue()
    {
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetNewestStateTimestampAsync(Arg.Any<CancellationToken>())
            .Returns<DateTimeOffset?>(_ => throw new InvalidOperationException("database unavailable"));

        WorkerGaugePoller poller = CreatePoller(repository);

        await poller.RefreshAsync(CancellationToken.None);
    }
```

Write `CreatePoller` as a private helper building the poller over a substituted `IServiceScopeFactory` that resolves the supplied repository. Never introduce a wall-clock delay in these tests — drive time with `FakeTimeProvider` only.

- [ ] **Step 7: Build and run the suites**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/functional/hangfire/functional.hangfire.csproj
```

Expected: zero warnings, all pass.

- [ ] **Step 8: Commit**

```bash
git add src/services.core/Observability/VordMetrics.cs src/services.core/Alerts src/services.worker test/unit/services.core/Observability/WorkerMetricsTests.cs
git commit -m "Instrument projection lag, Hangfire queue depth and alert evaluation"
```

---

### Task 8: Hangfire trace context propagation and span attribute hygiene

Jobs are enqueued in one process and executed later in another, through Postgres. There is no auto-instrumentation, so without explicit propagation every job is a disconnected root span and "this webhook caused this job to fail" cannot be answered. This is the largest single piece of the plan.

The same task closes the span-attribute hole: traces are retained for seven days and visible to everyone with Grafana access, and the telemetry ingest path carries a machine API key on every request.

**Files:**
- Create: `vord/src/services.core/Observability/HangfireTracingFilter.cs`
- Modify: `vord/src/services.core/Extensions/ServiceCollectionExtensions.cs:188` (`AddHangfireClient`) and the server-side Hangfire registration
- Test: `vord/test/unit/services.core/Observability/HangfireTracingFilterTests.cs`

**Interfaces:**
- Consumes: Hangfire `IClientFilter` (`OnCreating`/`OnCreated`), `IServerFilter` (`OnPerforming`/`OnPerformed`), `VordActivitySource.Instance`.
- Produces: `public sealed class HangfireTracingFilter : IClientFilter, IServerFilter`.

- [ ] **Step 1: Write the failing test**

Create `vord/test/unit/services.core/Observability/HangfireTracingFilterTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using System.Diagnostics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Tests that a job enqueued inside a trace is executed inside a span that is a child of that
/// same trace. Without this, background work shows up as disconnected roots and the chain from a
/// request to the job it scheduled is lost.
/// </summary>
public sealed class HangfireTracingFilterTests
{
    /// <summary>
    /// The traceparent captured at enqueue must reappear as the parent of the execution span.
    /// </summary>
    [Test]
    public async Task EnqueueThenPerform_LinksExecutionSpanToEnqueueTrace()
    {
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == VordActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        HangfireTracingFilter filter = new();

        using Activity enqueueActivity = VordActivitySource.Instance.StartActivity("enqueue")!;
        string capturedTraceParent = filter.CaptureTraceParent();
        string expectedTraceId = enqueueActivity.TraceId.ToString();
        enqueueActivity.Stop();

        using Activity? executionActivity = filter.StartExecutionActivity("TestJob", capturedTraceParent);

        await Assert.That(executionActivity).IsNotNull();
        await Assert.That(executionActivity!.TraceId.ToString()).IsEqualTo(expectedTraceId);
    }

    /// <summary>
    /// A job enqueued with no ambient trace must still produce a usable root span rather than
    /// throwing or producing nothing.
    /// </summary>
    [Test]
    public async Task Perform_NoTraceParent_StartsRootSpan()
    {
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == VordActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        HangfireTracingFilter filter = new();

        using Activity? activity = filter.StartExecutionActivity("TestJob", string.Empty);

        await Assert.That(activity).IsNotNull();
    }
}
```

`CaptureTraceParent` and `StartExecutionActivity` are deliberately exposed as testable methods so the trace-linking logic can be verified without standing up a Hangfire server. The `IClientFilter` and `IServerFilter` members are thin wrappers over them.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*HangfireTracingFilterTests*"
```

Expected: compile failure.

- [ ] **Step 3: Write the filter**

Create `vord/src/services.core/Observability/HangfireTracingFilter.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Hangfire.Client;
using Hangfire.Common;
using Hangfire.Server;
using System.Diagnostics;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Carries W3C trace context across the Hangfire enqueue boundary. Hangfire has no OpenTelemetry
/// instrumentation, and a job is enqueued in one process and executed later in another, so
/// without this every background job appears as a disconnected root span and the link from the
/// request that scheduled the work to the work itself is lost.
/// The traceparent is stored as a job parameter at enqueue time and restored as the parent
/// context when the job runs.
/// </summary>
public sealed class HangfireTracingFilter : IClientFilter, IServerFilter
{
    private const string TraceParentParameter = "TraceParent";
    private const string ActivityItemKey = "VordTracingActivity";

    /// <summary>
    /// Returns the current W3C traceparent, or an empty string when there is no ambient activity.
    /// </summary>
    public string CaptureTraceParent()
    {
        return Activity.Current?.Id ?? string.Empty;
    }

    /// <summary>
    /// Starts the span representing job execution, parented to the enqueueing trace when a
    /// traceparent was captured. An unparseable or absent traceparent yields a root span rather
    /// than no span, because losing the job entirely from tracing is worse than losing its link.
    /// </summary>
    public Activity? StartExecutionActivity(string jobName, string traceParent)
    {
        if (ActivityContext.TryParse(traceParent, null, out ActivityContext parentContext))
        {
            return VordActivitySource.Instance.StartActivity(
                $"hangfire {jobName}",
                ActivityKind.Consumer,
                parentContext);
        }

        return VordActivitySource.Instance.StartActivity($"hangfire {jobName}", ActivityKind.Consumer);
    }

    /// <inheritdoc/>
    public void OnCreating(CreatingContext filterContext)
    {
        filterContext.SetJobParameter(TraceParentParameter, CaptureTraceParent());
    }

    /// <inheritdoc/>
    public void OnCreated(CreatedContext filterContext)
    {
        // No action required once the job has been created.
    }

    /// <inheritdoc/>
    public void OnPerforming(PerformingContext filterContext)
    {
        string traceParent = filterContext.GetJobParameter<string>(TraceParentParameter) ?? string.Empty;
        Activity? activity = StartExecutionActivity(filterContext.BackgroundJob.Job.Type.Name, traceParent);

        if (activity is not null)
        {
            filterContext.Items[ActivityItemKey] = activity;
        }
    }

    /// <inheritdoc/>
    public void OnPerformed(PerformedContext filterContext)
    {
        if (filterContext.Items.TryGetValue(ActivityItemKey, out object? item) && (item is Activity activity))
        {
            if (filterContext.Exception is not null)
            {
                activity.SetStatus(ActivityStatusCode.Error, filterContext.Exception.Message);
            }

            activity.Dispose();
        }
    }
}
```

Setting `ActivityStatusCode.Error` matters beyond tidiness: the gateway's `keep-errors` tail-sampling policy selects on span status, so a failed job is retained rather than subjected to the 10% sample.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*HangfireTracingFilterTests*"
```

Expected: 2 tests pass.

- [ ] **Step 5: Register the filter on both client and server**

In `vord/src/services.core/Extensions/ServiceCollectionExtensions.cs`, in `AddHangfireClient` (line 188) and in `AddHangfireJobTypes` (line 351) or wherever the Hangfire server is configured, add the filter to the global filter collection:

```csharp
GlobalJobFilters.Filters.Add(new HangfireTracingFilter());
```

Register it exactly once per process — adding it in both an `AddHangfireClient` and an `AddHangfireServer` path that both run in services-worker would double every span. If both paths execute in the same process, register in one place only and confirm by checking that a single job produces a single `hangfire *` span in Tempo.

Add `using Hangfire;` if not already present, in alphabetical order.

- [ ] **Step 6: Verify with a functional test**

```bash
dotnet run --project test/functional/hangfire/functional.hangfire.csproj
```

Expected: all pass. Add a functional test that enqueues a job inside an activity and asserts the executed job's activity shares the trace ID, using an `ActivityListener` as in the unit test.

- [ ] **Step 7: Write the failing span-hygiene test**

Create `vord/test/unit/services.core/Observability/SpanRedactionTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Traces are retained for seven days and readable by anyone with Grafana access, and the
/// telemetry ingest path carries a machine API key on every request. Nothing secret may reach a
/// span attribute.
/// </summary>
public sealed class SpanRedactionTests
{
    /// <summary>
    /// Header names that must never be copied onto a span.
    /// </summary>
    [Test]
    [Arguments("X-Api-Key")]
    [Arguments("x-api-key")]
    [Arguments("Authorization")]
    [Arguments("Cookie")]
    public async Task IsSensitiveHeader_KnownSecretHeaders_ReturnsTrue(string headerName)
    {
        await Assert.That(SpanRedaction.IsSensitiveHeader(headerName)).IsTrue();
    }

    /// <summary>
    /// Ordinary headers are not redacted, so useful diagnostic context is kept.
    /// </summary>
    [Test]
    [Arguments("Content-Type")]
    [Arguments("User-Agent")]
    public async Task IsSensitiveHeader_OrdinaryHeaders_ReturnsFalse(string headerName)
    {
        await Assert.That(SpanRedaction.IsSensitiveHeader(headerName)).IsFalse();
    }

    /// <summary>
    /// A null header name is a programming error rather than a silent pass.
    /// </summary>
    [Test]
    public async Task IsSensitiveHeader_Null_Throws()
    {
        await Assert.That(() => SpanRedaction.IsSensitiveHeader(null!)).Throws<ArgumentNullException>();
    }
}
```

Run it: `dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*SpanRedactionTests*"` — expected compile failure.

- [ ] **Step 8: Write the redaction helper**

Create `vord/src/services.core/Observability/SpanRedaction.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Decides which request headers must never be recorded as span attributes. Spans are retained
/// for seven days and are visible to every Grafana user, while the telemetry ingest path carries
/// a per-machine API key on every request, so the default is to keep secrets off spans entirely
/// rather than to redact them after the fact.
/// </summary>
public static class SpanRedaction
{
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "X-Vord-Signature",
        "Stripe-Signature",
    };

    /// <summary>
    /// Returns true when the named header must not be copied onto a span.
    /// </summary>
    public static bool IsSensitiveHeader(string headerName)
    {
        ArgumentNullException.ThrowIfNull(headerName);

        return SensitiveHeaders.Contains(headerName);
    }
}
```

Run the test again — expected PASS.

- [ ] **Step 9: Apply it to the tracing registration**

In `AddCoreObservability`, replace the bare `.AddAspNetCoreInstrumentation()` in the `WithTracing` block with an enriching version that never copies a sensitive header, and that tags spans with the tenant — which is safe and useful on traces, unlike on metrics:

```csharp
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.EnrichWithHttpRequest = (activity, request) =>
                    {
                        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in request.Headers)
                        {
                            if (SpanRedaction.IsSensitiveHeader(header.Key))
                            {
                                continue;
                            }
                        }

                        string? tenantId = request.HttpContext.User.FindFirst("tenant_id")?.Value;

                        if (string.IsNullOrWhiteSpace(tenantId) == false)
                        {
                            activity.SetTag("tenant.id", tenantId);
                        }
                    };
                })
```

Confirm the claim name used for the tenant matches what `CookiePrincipalValidator` actually issues — verify with the LSP rather than assuming, since a wrong claim name yields silently untagged spans.

The AspNetCore instrumentation does not record headers by default, so the loop above is a guard against a future enrichment adding one, not a fix for current behaviour. If a code review finds it dead weight, replace it with a comment recording that headers are deliberately not captured — but do not simply add header capture without routing it through `SpanRedaction`.

- [ ] **Step 10: Commit**

```bash
git add src/services.core/Observability src/services.core/Extensions/ServiceCollectionExtensions.cs test/unit/services.core/Observability/HangfireTracingFilterTests.cs test/unit/services.core/Observability/SpanRedactionTests.cs test/functional/hangfire
git commit -m "Carry trace context across the Hangfire enqueue boundary and keep secrets off spans"
```

---

### Task 9: billing-api instrumentation

**Files:**
- Create: `vord-internal/src/billing-api/Observability/BillingMetrics.cs`
- Create: `vord-internal/src/billing-api/Observability/ObservabilityExtensions.cs`
- Modify: `vord-internal/src/billing-api/Program.cs`
- Modify: `vord-internal/src/billing-api/Services/ResendEmailSender.cs`, `Services/StripeCanaryService.cs`
- Modify: `vord-internal/src/billing-api/api.csproj`
- Test: `vord-internal/test/billing/Observability/BillingMetricsTests.cs`

**Interfaces:**
- Produces: `public static class BillingMetrics` in `Framlux.Billing.Api.Observability` with `MeterName = "Framlux.Billing"`, `StripeSyncFailures` (`Counter<long>`), `EmailSendFailures` (`Counter<long>`), `Heartbeat` (`Counter<long>`); and `public static IServiceCollection AddBillingObservability(this IServiceCollection services, IConfiguration configuration)`.

- [ ] **Step 1: Add the packages**

```bash
dotnet add src/billing-api/api.csproj package OpenTelemetry.Extensions.Hosting
dotnet add src/billing-api/api.csproj package OpenTelemetry.Exporter.OpenTelemetryProtocol
dotnet add src/billing-api/api.csproj package OpenTelemetry.Instrumentation.AspNetCore
dotnet add src/billing-api/api.csproj package OpenTelemetry.Instrumentation.Http
dotnet add src/billing-api/api.csproj package OpenTelemetry.Instrumentation.GrpcNetClient
dotnet add src/billing-api/api.csproj package OpenTelemetry.Instrumentation.Runtime
dotnet add src/billing-api/api.csproj package Npgsql.OpenTelemetry
```

- [ ] **Step 2: Write the failing test**

Create `vord-internal/test/billing/Observability/BillingMetricsTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.Billing.Api.Observability;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Framlux.Billing.Api.Tests.Observability;

/// <summary>
/// Tests for the billing instruments. These are the metrics behind the two critical alerts that
/// page, so their names and tag sets are a contract with the alert rules.
/// </summary>
public sealed class BillingMetricsTests
{
    /// <summary>
    /// Stripe sync failures are counted and tagged by operation only.
    /// </summary>
    [Test]
    public async Task StripeSyncFailures_TagsOperationOnly()
    {
        using MetricCollector<long> collector = new(BillingMetrics.Meter, "vord.stripe.sync.failures");

        BillingMetrics.StripeSyncFailures.Add(1, new KeyValuePair<string, object?>("operation", "webhook"));

        CollectedMeasurement<long> measurement = collector.GetMeasurementSnapshot().Single();
        await Assert.That(measurement.Value).IsEqualTo(1);
        await Assert.That(measurement.Tags["operation"]).IsEqualTo("webhook");
        await Assert.That(measurement.Tags.ContainsKey("tenant.id")).IsFalse();
    }

    /// <summary>
    /// Email failures use the same metric name as the fleet service so one alert rule covers both.
    /// </summary>
    [Test]
    public async Task EmailSendFailures_UsesSharedMetricName()
    {
        using MetricCollector<long> collector = new(BillingMetrics.Meter, "vord.email.send.failures");

        BillingMetrics.EmailSendFailures.Add(1, new KeyValuePair<string, object?>("status", 403));

        await Assert.That(collector.GetMeasurementSnapshot().Single().Value).IsEqualTo(1);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

```bash
dotnet run --project test/billing/billing.csproj --treenode-filter "*BillingMetricsTests*"
```

Expected: compile failure.

- [ ] **Step 4: Create the metrics**

Create `vord-internal/src/billing-api/Observability/BillingMetrics.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics.Metrics;

namespace Framlux.Billing.Api.Observability;

/// <summary>
/// Owns the billing meter and its instruments. The email-failure metric deliberately shares its
/// name with the fleet service's instrument so a single alert rule covers outbound mail across
/// both services — the resource attributes distinguish which one reported it.
/// </summary>
public static class BillingMetrics
{
    /// <summary>
    /// The meter name registered with the OpenTelemetry exporter.
    /// </summary>
    public const string MeterName = "Framlux.Billing";

    /// <summary>
    /// The meter that owns every billing instrument.
    /// </summary>
    public static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Stripe operations that failed, tagged by operation. Money-affecting and paged on.
    /// </summary>
    public static readonly Counter<long> StripeSyncFailures = Meter.CreateCounter<long>(
        "vord.stripe.sync.failures",
        description: "Stripe API or webhook operations that failed.");

    /// <summary>
    /// Outbound email rejected by the provider, tagged with the HTTP status.
    /// </summary>
    public static readonly Counter<long> EmailSendFailures = Meter.CreateCounter<long>(
        "vord.email.send.failures",
        description: "Outbound emails rejected by the provider.");

    /// <summary>
    /// Incremented on a fixed interval so that service silence can be alerted on.
    /// </summary>
    public static readonly Counter<long> Heartbeat = Meter.CreateCounter<long>(
        "vord.service.heartbeat",
        description: "Incremented on a fixed interval to prove the service is still exporting.");
}
```

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet run --project test/billing/billing.csproj --treenode-filter "*BillingMetricsTests*"
```

Expected: 2 tests pass.

- [ ] **Step 6: Add the registration extension and heartbeat**

Create `vord-internal/src/billing-api/Observability/ObservabilityExtensions.cs` with `AddBillingObservability`, mirroring `AddCoreObservability` from Task 1 exactly — same guard on `OTEL_EXPORTER_OTLP_ENDPOINT`, same `AlwaysOnSampler`, same instrumentation set — with `serviceName: "billing-api"`, `.AddMeter(BillingMetrics.MeterName)`, and a hosted service incrementing `BillingMetrics.Heartbeat` every 30 seconds using an injected `TimeProvider` and `PeriodicTimer`, as in Task 2.

Call it from `Program.cs` alongside the other registrations:

```csharp
builder.Services.AddBillingObservability(builder.Configuration);
```

- [ ] **Step 7: Record the failures**

In `ResendEmailSender.SendAsync`, inside the non-2xx branch added by the email-hardening plan's Task 2, after the `LogError` call:

```csharp
BillingMetrics.EmailSendFailures.Add(1, new KeyValuePair<string, object?>("status", (int)response.StatusCode));
```

In `StripeCanaryService`, record a Stripe sync failure wherever the probe reports a failed leg:

```csharp
BillingMetrics.StripeSyncFailures.Add(1, new KeyValuePair<string, object?>("operation", result.LastErrorLeg ?? "unknown"));
```

Also record it in the Stripe webhook processing path in `WebhookProcessorService` when an event fails to process, tagged `"operation", "webhook"`. Confirm `LastErrorLeg` is a bounded set of known leg names before using it as a tag — if it can carry an arbitrary Stripe error string, map it to a fixed set first, or the series count grows without limit.

- [ ] **Step 8: Add the endpoint to the billing config**

In `stack/clusters/prod/apps/vord-platform/base/kustomization.yaml`, add to the billing `configMapGenerator` literals block (the one containing `Resend__FromEmail=Framlux Vord Billing ...` at line 74):

```yaml
      - OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-gateway.framlux-observability.svc.cluster.local:4317
      - OTEL_EXPORTER_OTLP_PROTOCOL=grpc
```

- [ ] **Step 9: Build and test**

```bash
dotnet build vord-internal.slnx -c Release
dotnet run --project test/billing/billing.csproj
```

Expected: zero warnings, all tests pass.

- [ ] **Step 10: Commit**

```bash
git -C vord-internal add src/billing-api/Observability src/billing-api/Program.cs src/billing-api/Services src/billing-api/api.csproj test/billing/Observability
git -C vord-internal commit -m "Export OpenTelemetry metrics and traces from billing-api"

git -C stack add clusters/prod/apps/vord-platform/base/kustomization.yaml
git -C stack commit -m "Point billing-api at the collector gateway"
```

---

### Task 10: Alert rules and promtool tests

**Files:**
- Modify: `stack/clusters/prod/apps/observability/base/prometheus/alerts.yaml`
- Modify: `stack/tests/rules/alerts_test.yaml`

**Interfaces:**
- Consumes: metric names from Tasks 2, 6, 7, and 9, as they appear in Prometheus — dots to underscores, no unit suffix, resource attributes as labels.
- Produces: alert names consumed by Alertmanager routing.

`severity: critical` routes to `page-all` (Discord, email, ntfy urgent). Anything else goes to Discord only.

- [ ] **Step 1: Add the critical rules**

Append a new group to `alerts.yaml`, following the formatting of the existing groups:

```yaml
  - name: vord-application-critical
    rules:
      - alert: VordServiceSilent
        expr: |
          (
            sum by (service_name) (increase(vord_service_heartbeat[5m])) == 0
          )
          or
          (
            absent(vord_service_heartbeat{service_name="api-server"})
            or absent(vord_service_heartbeat{service_name="services-worker"})
            or absent(vord_service_heartbeat{service_name="billing-api"})
          )
        for: 5m
        labels:
          severity: critical
        annotations:
          summary: "Vord service {{ $labels.service_name }} has stopped reporting"
          description: "Metrics are pushed, not scraped, so silence is the only failure signal. The service is down, its exporter is broken, or the gateway is rejecting it."

      - alert: VordStripeSyncFailing
        expr: increase(vord_stripe_sync_failures[15m]) > 0
        for: 5m
        labels:
          severity: critical
        annotations:
          summary: "Stripe sync is failing ({{ $labels.operation }})"
          description: "Billing operations are failing against Stripe. Subscription state may be diverging from what customers have paid for."

      - alert: VordEmailSendFailing
        expr: increase(vord_email_send_failures[15m]) > 0
        for: 5m
        labels:
          severity: critical
        annotations:
          summary: "Outbound email is being rejected (status {{ $labels.status }})"
          description: "Resend is rejecting sends. Tenant invitations and billing alerts are being dropped. This previously went undetected for months."

      - alert: VordHangfireJobsFailing
        expr: vord_hangfire_jobs_failed > 10
        for: 15m
        labels:
          severity: critical
        annotations:
          summary: "Hangfire has {{ $value }} failed jobs"
          description: "Background work is failing and not draining. Telemetry projection, alerting and billing sync all run through Hangfire."
```

- [ ] **Step 2: Add the warning rules**

```yaml
  - name: vord-application-warning
    rules:
      - alert: VordTelemetryIngestLagHigh
        expr: histogram_quantile(0.95, sum by (le) (rate(vord_telemetry_ingest_lag_bucket[5m]))) > 120
        for: 15m
        labels:
          severity: warning
        annotations:
          summary: "Telemetry ingest lag p95 is {{ $value }}s"
          description: "Agents' data is arriving late. Threshold is provisional and should be re-tuned against a week of production data."

      - alert: VordProjectionLagHigh
        expr: vord_projection_hwm_lag > 300
        for: 15m
        labels:
          severity: warning
        annotations:
          summary: "Machine state projection is {{ $value }}s behind"
          description: "The projection high-water mark is falling behind ingest, so the UI is showing stale machine state."

      - alert: VordHangfireQueueBacklog
        expr: sum by (queue) (vord_hangfire_queue_depth) > 1000
        for: 15m
        labels:
          severity: warning
        annotations:
          summary: "Hangfire queue {{ $labels.queue }} has {{ $value }} jobs enqueued"
          description: "Jobs are being enqueued faster than they drain."

      - alert: VordDbPoolSaturated
        expr: |
          (
            sum by (service_name) (npgsql_connections_busy)
            /
            clamp_min(sum by (service_name) (npgsql_connections_max), 1)
          ) > 0.9
        for: 10m
        labels:
          severity: warning
        annotations:
          summary: "{{ $labels.service_name }} is using {{ $value }} of its database pool"
          description: "Connection pool saturation. See the pooling work in vord-cf9 if this is sustained."

      - alert: VordRedisUnavailable
        expr: vord_redis_available == 0
        for: 5m
        labels:
          severity: warning
        annotations:
          summary: "{{ $labels.service_name }} cannot reach Redis"
          description: "The service is running fail-open: rate limiting and telemetry dedup are degraded but the fleet is still serving."
```

Verify the exact Npgsql metric names against what actually arrives in Prometheus before committing `VordDbPoolSaturated` — the Npgsql meter's instrument names are set by the driver, not by this codebase, and guessing them produces a rule that never fires. Query Prometheus for `{__name__=~"npgsql.*"}` and use what is really there.

- [ ] **Step 3: Sync the rules into the test fixture**

```bash
./tests/rules/extract.sh
```

- [ ] **Step 4: Write promtool tests**

Append to `tests/rules/alerts_test.yaml`, following the existing style — every alert gets both a firing case and a non-firing case:

```yaml
  # A service that stops incrementing its heartbeat pages after 5m.
  - interval: 1m
    input_series:
      - series: 'vord_service_heartbeat{service_name="api-server"}'
        values: '1 2 3 3 3 3 3 3'
    alert_rule_test:
      - eval_time: 7m
        alertname: VordServiceSilent
        exp_alerts:
          - exp_labels:
              severity: critical
              service_name: api-server
            exp_annotations:
              summary: "Vord service api-server has stopped reporting"
              description: "Metrics are pushed, not scraped, so silence is the only failure signal. The service is down, its exporter is broken, or the gateway is rejecting it."

  # A healthy heartbeat must never fire it.
  - interval: 1m
    input_series:
      - series: 'vord_service_heartbeat{service_name="api-server"}'
        values: '1 2 3 4 5 6 7 8'
    alert_rule_test:
      - eval_time: 7m
        alertname: VordServiceSilent
        exp_alerts: []

  # A single email rejection pages.
  - interval: 1m
    input_series:
      - series: 'vord_email_send_failures{status="403",service_name="api-server"}'
        values: '0 0 1 1 1 1 1 1'
    alert_rule_test:
      - eval_time: 8m
        alertname: VordEmailSendFailing
        exp_alerts:
          - exp_labels:
              severity: critical
              status: "403"
              service_name: api-server
            exp_annotations:
              summary: "Outbound email is being rejected (status 403)"
              description: "Resend is rejecting sends. Tenant invitations and billing alerts are being dropped. This previously went undetected for months."

  # Email sending cleanly must never fire it.
  - interval: 1m
    input_series:
      - series: 'vord_email_send_failures{status="403",service_name="api-server"}'
        values: '0 0 0 0 0 0 0 0'
    alert_rule_test:
      - eval_time: 8m
        alertname: VordEmailSendFailing
        exp_alerts: []
```

Write the equivalent firing and non-firing pair for `VordStripeSyncFailing`, `VordHangfireJobsFailing`, `VordTelemetryIngestLagHigh`, `VordProjectionLagHigh`, `VordHangfireQueueBacklog`, `VordDbPoolSaturated`, and `VordRedisUnavailable`. Annotation strings must match the rules exactly — promtool compares them literally.

- [ ] **Step 5: Run promtool**

```bash
./tests/validate-configs.sh
promtool test rules tests/rules/alerts_test.yaml
```

Expected: all tests pass, zero rule-syntax errors.

- [ ] **Step 6: Commit**

```bash
git add clusters/prod/apps/observability/base/prometheus/alerts.yaml tests/rules/alerts.yaml tests/rules/alerts_test.yaml
git commit -m "Alert on application silence, money failures and lag"
```

---

### Task 11: RED dashboard

**Files:**
- Create: `stack/clusters/prod/apps/observability/base/grafana/dashboards/vord-red.json`
- Modify: `stack/clusters/prod/apps/observability/base/grafana/kustomization.yaml:33-36`

**Interfaces:**
- Consumes: `http.server.request.duration` from the AspNetCore auto-instrumentation, as it lands in Prometheus; datasource UIDs `prometheus` and `tempo`.
- Produces: nothing consumed by other tasks.

- [ ] **Step 1: Confirm the real metric names first**

Query Prometheus for `{__name__=~"http_server.*"}` and note exactly what the AspNetCore instrumentation produced. Names differ between OpenTelemetry semantic-convention versions, and `add_metric_suffixes: false` removes the suffixes you might otherwise expect. Build the panels against observed names, not assumed ones. This step is why the dashboard comes after Task 5's verification rather than before.

- [ ] **Step 2: Author the dashboard**

Create `vord-red.json` with one row per service (`api-server`, `services-worker`, `billing-api`) and three panels per row:

- **Rate** — `sum by (service_name) (rate(http_server_request_duration_count[5m]))`
- **Errors** — `sum by (service_name) (rate(http_server_request_duration_count{http_response_status_code=~"5.."}[5m]))` over the same denominator, displayed as a percentage
- **Duration** — `histogram_quantile(0.95, sum by (le, service_name) (rate(http_server_request_duration_bucket[5m])))`

Match the existing dashboards' conventions: `"uid"` set explicitly and stably, datasource referenced as `{"type": "prometheus", "uid": "prometheus"}`, and the same time-range defaults used by `application-health.json`. Read that file first and follow it rather than emitting Grafana's default export shape.

Add a Tempo-linked panel so a spike in the errors panel can be opened directly as traces, using the `tempo` datasource UID.

- [ ] **Step 3: Register the dashboard**

In `stack/clusters/prod/apps/observability/base/grafana/kustomization.yaml`, add to the `grafana-dashboards` `files:` list at lines 33-36:

```yaml
      - dashboards/vord-red.json
```

- [ ] **Step 4: Validate**

```bash
./tests/validate-manifests.sh
python3 -c "import json; json.load(open('clusters/prod/apps/observability/base/grafana/dashboards/vord-red.json'))"
```

Expected: manifests valid, JSON parses.

- [ ] **Step 5: Verify in Grafana**

After sync, open the dashboard in the Platform folder. Confirm all three rows render real data and that no panel shows "No data" — a "No data" panel means Step 1's metric names were wrong.

- [ ] **Step 6: Commit**

```bash
git add clusters/prod/apps/observability/base/grafana/dashboards/vord-red.json clusters/prod/apps/observability/base/grafana/kustomization.yaml
git commit -m "Add the RED dashboard for the Vord services"
```

---

### Task 12: Production verification and close-out

**Files:** none modified, except a possible gateway re-tune.

**Interfaces:** none.

- [ ] **Step 1: Confirm every instrument is arriving**

Query Prometheus for each of: `vord_service_heartbeat`, `vord_telemetry_ingest_lag_count`, `vord_projection_hwm_lag`, `vord_hangfire_queue_depth`, `vord_hangfire_jobs_failed`, `vord_alert_evaluation_duration_count`, `vord_redis_available`, `vord_machines_active`, `vord_email_send_failures`, `vord_stripe_sync_failures`.

Every one must return a series. Any absentee is a wiring bug, not a quiet metric — a counter that has never been incremented still registers once its instrument is created and exported.

- [ ] **Step 2: Prove one alert actually pages**

Pick the safest real path — for instance, temporarily scale `services-worker` to zero and confirm `VordServiceSilent` fires and arrives on Discord, email, and ntfy. Scale back up and confirm the resolved notification arrives.

This is `vord-8gq`'s exit criterion: metrics scrapeable plus at least one paging alert wired. An alert rule that has never fired is not a wired alert.

- [ ] **Step 3: Confirm Hangfire traces are linked**

Trigger a request that enqueues a background job. In Tempo, find the request trace and confirm the `hangfire *` span appears as a child of it rather than as a separate root.

- [ ] **Step 4: Re-tune tail sampling if needed**

Compare the observed trace rate against the gateway's `expected_new_traces_per_sec: 100`. If the real rate is materially higher, raise it in `collector/gateway-config.yaml` and check Tempo's PVC usage against its 10Gi with 168h retention. Commit any change with the measured number in the message so the next person knows where it came from.

- [ ] **Step 5: Verify no secrets reached span attributes**

In Tempo, inspect spans from the telemetry ingest path and the billing webhook path. Confirm no machine API key, Resend or Stripe key, session cookie, or user email appears in any attribute. If any does, fix it before closing — traces are retained for seven days and are visible to anyone with Grafana access.

- [ ] **Step 6: Close the beads**

```bash
bd close vord-8gq
bd close vord-2eb
bd close vord-xoo
```

`vord-xoo` closes here rather than in the email plan: its second exit criterion is a guard that fails loudly instead of dropping mail silently, and `VordEmailSendFailing` is that guard.

- [ ] **Step 7: Record what was deferred**

File beads for anything discovered and not fixed — most likely metric cardinality worth trimming (`vord-tpy` may already cover it), and the duplicate `TelemetryPipelineFailing` alert name at `alerts.yaml` lines 86 and 109, which collapses two distinct conditions into one notification group under `group_by: [alertname, k8s_namespace_name]`.

---

## Verification

`vord-8gq`, `vord-2eb`, and `vord-xoo` are closeable when:

- All four workloads export metrics and traces; every named instrument returns a series in Prometheus.
- A real alert has fired and been received on all three critical channels, and its resolution was received too.
- Hangfire job spans are children of the request that enqueued them.
- Log lines link to their traces in Grafana.
- The RED dashboard renders real data with no empty panels.
- No metric carries a tenant or machine dimension; no span carries a secret.
- Both repos build warning-free and every suite passes; promtool passes.
