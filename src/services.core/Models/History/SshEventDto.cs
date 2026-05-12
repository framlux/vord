// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.History;

/// <summary>
/// A single SSH session event.
/// </summary>
public sealed class SshEventDto
{
    /// <summary>Event timestamp.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Username.</summary>
    public required string User { get; init; }

    /// <summary>Source IP address.</summary>
    public required string SourceIp { get; init; }

    /// <summary>Source port.</summary>
    public required int SourcePort { get; init; }

    /// <summary>Action (connect/disconnect/failed).</summary>
    public required string Action { get; init; }

    /// <summary>Authentication method.</summary>
    public required string AuthMethod { get; init; }
}
