// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Implemented by metric classes that must exist in Prometheus before anything has gone wrong.
/// </summary>
/// <remarks>
/// A counter exports no series until its first measurement, and this Prometheus does not ingest
/// created timestamps — so a failure series born mid-window shows no increase, because the pre-birth
/// zero was never observed, and the first failure is invisible to every rule watching for it.
/// Recording an explicit zero for each closed-enum value at startup is what makes that first failure
/// detectable. It only works for dimensions whose values are known up front: a tenant-dimensioned
/// series cannot be pre-created, so detecting a single failure there genuinely needs two events.
/// </remarks>
public interface IInitialisableMetrics
{
    /// <summary>
    /// Records a zero measurement for every closed-enum combination this class can emit.
    /// </summary>
    void InitialiseSeries();
}
