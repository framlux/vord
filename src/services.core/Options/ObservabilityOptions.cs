// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Configuration for OpenTelemetry export. The presence of <see cref="OtlpEndpoint"/> is the only
/// thing that decides whether telemetry leaves the process: instruments are always created and
/// always recorded, so no call site has to check whether observability is switched on.
/// </summary>
/// <remarks>
/// Deliberately independent of <c>Deployment:SelfHosted</c>. That flag already decides the billing
/// client, service mapping, job registration, email transport and entitlement limits; making it
/// decide observability too would earn nothing and would stop a self-hoster pointing this at their
/// own collector.
/// </remarks>
public sealed class ObservabilityOptions
{
    /// <summary>
    /// The OTLP endpoint to export to, for example <c>http://otel-agent:4317</c>. Absent or blank
    /// means no exporter is registered and measurements go nowhere.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>
    /// The value reported as <c>service.name</c>, distinguishing this process from its siblings in
    /// every query and dashboard.
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Whether an exporter should be registered at all.
    /// </summary>
    public bool IsExportEnabled => string.IsNullOrWhiteSpace(OtlpEndpoint) == false;
}
