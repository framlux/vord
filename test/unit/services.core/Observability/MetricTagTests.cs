// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Locks the tag-value convention. PromQL label matching is case-sensitive and silent on mismatch,
/// so a rule written against one spelling and a series emitted with another simply never fires.
/// Multi-word members are the case that actually diverges in practice, so they are pinned by value.
/// </summary>
public sealed class MetricTagTests
{
    [Test]
    public async Task From_SingleWordMember_IsLowercased()
    {
        await Assert.That(MetricTag.From(SampleMetricOutcome.Ok)).IsEqualTo("ok");
    }

    [Test]
    [Arguments(SampleMetricOutcome.LoadFailed, "load_failed")]
    [Arguments(SampleMetricOutcome.SchedulingError, "scheduling_error")]
    [Arguments(SampleMetricOutcome.NotConfigured, "not_configured")]
    public async Task From_MultiWordMember_IsSnakeCased(SampleMetricOutcome value, string expected)
    {
        await Assert.That(MetricTag.From(value)).IsEqualTo(expected);
    }

    [Test]
    public async Task From_NeverProducesAnUppercaseCharacter()
    {
        string result = MetricTag.From(SampleMetricOutcome.SchedulingError);

        await Assert.That(result.Any(char.IsUpper)).IsFalse();
    }

    [Test]
    public async Task From_ValueOutsideTheEnum_StillProducesAStableTag()
    {
        // An undefined member cannot come from a closed vocabulary, but a cast can manufacture one.
        // Answering with the numeric value keeps the tag bounded rather than throwing inside a
        // recording path, where the exception would travel somewhere unrelated.
        string result = MetricTag.From((SampleMetricOutcome)99);

        await Assert.That(result).IsEqualTo("99");
    }

    [Test]
    public async Task From_RepeatedCalls_ReturnTheSameInstance()
    {
        // The lookup is what keeps this off the allocation path of every measurement.
        string first = MetricTag.From(SampleMetricOutcome.LoadFailed);
        string second = MetricTag.From(SampleMetricOutcome.LoadFailed);

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task Unknown_IsTheDefinedNoTenantBucket()
    {
        // Compared through string.Equals rather than asserted directly on the constant, because a
        // literal-to-literal assertion is a tautology the analyser rightly refuses.
        await Assert.That(string.Equals(MetricTag.Unknown, "unknown", StringComparison.Ordinal)).IsTrue();
        await Assert.That(MetricTag.Unknown.Any(char.IsUpper)).IsFalse();
    }
}
