// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Implemented by metric classes whose instruments are observable gauges.
/// </summary>
/// <remarks>
/// A gauge's series come into being when the class is constructed and the observable instrument is
/// created, so a gauge class nothing injects is never constructed and its series never exist — while
/// every unit test still passes, because the tests construct the class directly. Resolving this
/// marker at startup is what makes construction a guarantee rather than a side effect of whichever
/// other type happens to depend on it today.
/// </remarks>
public interface IObservableMetrics
{
}
