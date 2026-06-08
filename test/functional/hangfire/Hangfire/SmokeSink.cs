// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.FunctionalTest.Hangfire;

/// <summary>
/// Test-only sink that the Hangfire smoke job writes to. Registered as a singleton so the
/// per-job DI scope and the test method share the same instance, allowing the test to
/// observe the side effect of the background job having run.
/// </summary>
public sealed class SmokeSink
{
    /// <summary>The UTC timestamp at which the smoke job ran, or null if it has not run.</summary>
    public DateTimeOffset? RanAt { get; set; }
}
