// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Test.Services.Infrastructure;

/// <summary>
/// Tests for <see cref="RetentionDateHelper"/>.
/// Guards against sign errors, wrong units, and non-UTC results.
/// </summary>
public class RetentionDateHelperTests
{
    // ========== Core behavior: cutoff is in the past for positive retention ==========

    [Test]
    public async Task GetRetentionCutoff_PositiveRetentionDays_CutoffIsInThePast()
    {
        // Intent: A positive retention value must produce a cutoff BEFORE now.
        // Catches the most dangerous bug: AddDays(N) instead of AddDays(-N).
        DateTimeOffset now = DateTimeOffset.UtcNow;

        DateTimeOffset cutoff = RetentionDateHelper.GetRetentionCutoff(30);

        await Assert.That(cutoff < now).IsTrue();
    }

    // ========== Ordering: longer retention = older cutoff ==========

    [Test]
    public async Task GetRetentionCutoff_LongerRetention_ProducesOlderCutoff()
    {
        // Intent: Team (365d) cutoff must be older than Free (1d) cutoff.
        // Catches wrong sign or wrong scaling.
        DateTimeOffset freeCutoff = RetentionDateHelper.GetRetentionCutoff(1);
        DateTimeOffset teamCutoff = RetentionDateHelper.GetRetentionCutoff(365);

        await Assert.That(teamCutoff < freeCutoff).IsTrue();
    }

    // ========== Magnitude: cutoff is approximately retentionDays ago ==========

    [Test]
    public async Task GetRetentionCutoff_NinetyDays_ApproximatelyNinetyDaysAgo()
    {
        // Intent: The actual offset should be close to the requested days.
        // Catches unit errors (e.g., hours instead of days).
        DateTimeOffset cutoff = RetentionDateHelper.GetRetentionCutoff(90);

        double actualDaysAgo = (DateTimeOffset.UtcNow - cutoff).TotalDays;

        await Assert.That(actualDaysAgo).IsGreaterThanOrEqualTo(89.99);
        await Assert.That(actualDaysAgo).IsLessThanOrEqualTo(90.01);
    }

    // ========== UTC: result must have zero offset ==========

    [Test]
    public async Task GetRetentionCutoff_AnyInput_ResultIsUtc()
    {
        // Intent: Non-UTC cutoffs would cause partition pruning mismatches
        // against UTC-stored ReceivedAt columns.
        DateTimeOffset cutoff = RetentionDateHelper.GetRetentionCutoff(30);

        await Assert.That(cutoff.Offset).IsEqualTo(TimeSpan.Zero);
    }

    // ========== Zero retention: cutoff is approximately now ==========

    [Test]
    public async Task GetRetentionCutoff_ZeroDays_CutoffIsApproximatelyNow()
    {
        // Intent: Zero-day retention means "show nothing older than right now."
        DateTimeOffset cutoff = RetentionDateHelper.GetRetentionCutoff(0);

        double secondsAgo = (DateTimeOffset.UtcNow - cutoff).TotalSeconds;

        await Assert.That(secondsAgo).IsGreaterThanOrEqualTo(0);
        await Assert.That(secondsAgo).IsLessThanOrEqualTo(1);
    }
}
