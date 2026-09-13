// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Test.Integration.Services.Machines;

/// <summary>
/// The single input matrix for the machine health rule.
/// </summary>
/// <remarks>
/// The rule is transcribed once per dialect, and the expectations are asserted from two directions:
/// <see cref="HealthSweepThresholdLiveTests"/> pins the production PostgreSQL SQL to these values,
/// and <see cref="HealthRuleDialectAgreementLiveTests"/> pins the SQLite copy used by the unit and
/// functional harnesses to whatever PostgreSQL answers. Both read this matrix so a boundary can
/// never be covered on one side and missed on the other.
/// </remarks>
public static class HealthRuleCases
{
    /// <summary>
    /// Threshold, in seconds, that every case in this matrix is evaluated against.
    /// </summary>
    public const int OnlineThresholdSeconds = 300;

    /// <summary>
    /// Every case in the matrix, as a TUnit method data source.
    /// </summary>
    public static IEnumerable<Func<HealthRuleCase>> All()
    {
        foreach (HealthRuleCase testCase in Cases)
        {
            HealthRuleCase captured = testCase;

            yield return () => captured;
        }
    }

    // Each case sits at or just below a boundary, where an off-by-one in a transcription shows up
    // and an interior data point would not. Ages are far from the online threshold on purpose: a
    // case a second either side of it would be deciding the outcome on execution latency.
    private static readonly HealthRuleCase[] Cases =
    [
        new(10, 10, 10, 0, false, false, 60, 0, "interior baseline"),

        new(79, 10, 10, 0, false, false, 60, 0, "cpu below warning"),
        new(80, 10, 10, 0, false, false, 60, 1, "cpu at warning"),
        new(94, 10, 10, 0, false, false, 60, 1, "cpu below critical"),
        new(95, 10, 10, 0, false, false, 60, 2, "cpu at critical"),

        new(10, 79, 10, 0, false, false, 60, 0, "memory below warning"),
        new(10, 80, 10, 0, false, false, 60, 1, "memory at warning"),
        new(10, 94, 10, 0, false, false, 60, 1, "memory below critical"),
        new(10, 95, 10, 0, false, false, 60, 2, "memory at critical"),

        new(10, 10, 79, 0, false, false, 60, 0, "disk below warning"),
        new(10, 10, 80, 0, false, false, 60, 1, "disk at warning"),
        new(10, 10, 94, 0, false, false, 60, 1, "disk below critical"),
        new(10, 10, 95, 0, false, false, 60, 2, "disk at critical"),

        new(10, 10, 10, 1, false, false, 60, 2, "one failed service"),
        new(10, 10, 10, 0, true, false, 60, 2, "disk health issue"),
        new(10, 10, 10, 0, false, true, 60, 2, "hardware issue"),

        // A machine that stopped reporting while its last known metrics were critical must read
        // Offline, not Critical, or the fleet would show a dead machine as a live emergency.
        new(99, 99, 99, 5, true, true, 360, 3, "stale receipt outranks critical metrics"),
        new(10, 10, 10, 0, false, false, null, 3, "never seen"),

        // A machine that reports in but has not yet sent usage telemetry has nulls here. Disk and
        // failed services are coalesced; CPU and memory compare as nulls, which is not true in SQL.
        new(null, null, null, 0, false, false, 60, 0, "no metrics reported yet"),
    ];
}
