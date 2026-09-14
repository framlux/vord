// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// The single meter name every vord instrument is created under.
/// </summary>
/// <remarks>
/// One name rather than one per class, because the meter name is what the SDK registration
/// subscribes to. A per-class name would mean every new metric class also needed a line in the
/// registration, and the failure mode of forgetting it is an instrument that records correctly and
/// exports nothing — the precise problem this work exists to fix.
/// </remarks>
public static class VordMeter
{
    /// <summary>
    /// The meter name passed to <see cref="System.Diagnostics.Metrics.IMeterFactory"/> and
    /// subscribed to by the SDK.
    /// </summary>
    public const string Name = "Framlux.Vord";
}
