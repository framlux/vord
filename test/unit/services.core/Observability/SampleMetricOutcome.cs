// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// A stand-in vocabulary for the tag-value tests, deliberately not one of the real metric enums so
/// the conversion rule is pinned by shape — single word, two words, a trailing acronym-free
/// compound — rather than by whichever spellings the product happens to use today.
/// </summary>
public enum SampleMetricOutcome
{
    /// <summary>A single-word member.</summary>
    Ok,

    /// <summary>A two-word member, the case that actually diverges between implementations.</summary>
    LoadFailed,

    /// <summary>Another two-word member.</summary>
    SchedulingError,

    /// <summary>A negated two-word member.</summary>
    NotConfigured,
}
