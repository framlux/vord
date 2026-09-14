// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// A stand-in counter class that always fails to pre-record, so the initialiser's swallow-and-carry-on
/// behaviour can be asserted.
/// </summary>
public sealed class ThrowingInitialisableMetrics : IInitialisableMetrics
{
    /// <inheritdoc />
    public void InitialiseSeries()
    {
        throw new InvalidOperationException("pre-recording is unavailable");
    }
}
