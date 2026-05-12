// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.History;

/// <summary>
/// History response for SSH sessions as raw events (no aggregation).
/// </summary>
public sealed class SshHistoryResponseDto
{
    /// <summary>SSH session events ordered by timestamp descending.</summary>
    public required List<SshEventDto> Events { get; init; }

    /// <summary>Total number of events in the time range (may exceed returned count).</summary>
    public required int TotalEvents { get; init; }
}
