// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Builds real metric classes for tests that construct an instrumented service directly.
/// </summary>
/// <remarks>
/// Real instruments rather than substitutes, because recording is a fire-and-forget call with no
/// return value to stub: a substitute would prove only that a method was called, while the real
/// class also proves the tag values are the ones the alert rules will match. A test that wants to
/// observe the measurements collects them from the same meter factory.
/// </remarks>
public static class TestMetricsFactory
{
    /// <summary>
    /// Creates a meter factory backed by its own service provider, so instruments created from it
    /// are isolated from every other test.
    /// </summary>
    /// <returns>A meter factory.</returns>
    public static IMeterFactory CreateMeterFactory()
    {
        ServiceCollection services = new();
        services.AddMetrics();

        return services.BuildServiceProvider().GetRequiredService<IMeterFactory>();
    }

    /// <summary>
    /// Creates ingest instruments on a fresh meter factory.
    /// </summary>
    /// <returns>Ingest metrics ready to record into.</returns>
    public static IngestMetrics CreateIngestMetrics()
    {
        return new IngestMetrics(CreateMeterFactory());
    }

    /// <summary>
    /// Creates email instruments on a fresh meter factory.
    /// </summary>
    /// <returns>Email metrics ready to record into.</returns>
    public static EmailMetrics CreateEmailMetrics()
    {
        return new EmailMetrics(CreateMeterFactory());
    }
}
