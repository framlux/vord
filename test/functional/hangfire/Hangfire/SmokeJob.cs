// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.FunctionalTest.Hangfire;

/// <summary>
/// Test-only Hangfire job. Resolves <see cref="SmokeSink"/> through the per-job DI scope
/// (which exercises the JobActivator wiring) and records the time at which it ran. Used
/// exclusively by <c>HangfireSmokeTest</c> to verify the end-to-end Hangfire pipeline.
/// </summary>
public sealed class SmokeJob
{
    private readonly SmokeSink _sink;

    /// <summary>
    /// Initializes a new <see cref="SmokeJob"/>.
    /// </summary>
    /// <param name="sink">The shared sink to record the run timestamp into.</param>
    public SmokeJob(SmokeSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    /// <summary>
    /// Records the current UTC timestamp on the shared sink.
    /// </summary>
    /// <param name="ct">Cancellation token supplied by Hangfire.</param>
    /// <returns>A completed task.</returns>
    public Task RunAsync(CancellationToken ct)
    {
        _sink.RanAt = DateTimeOffset.UtcNow;

        return Task.CompletedTask;
    }
}
