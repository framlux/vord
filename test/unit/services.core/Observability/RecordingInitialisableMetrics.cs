// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// A stand-in counter class that counts how many times its series were pre-recorded.
/// </summary>
public sealed class RecordingInitialisableMetrics : IInitialisableMetrics
{
    /// <summary>How many times <see cref="InitialiseSeries"/> was called.</summary>
    public int InitialiseCount { get; private set; }

    /// <inheritdoc />
    public void InitialiseSeries()
    {
        InitialiseCount++;
    }
}
