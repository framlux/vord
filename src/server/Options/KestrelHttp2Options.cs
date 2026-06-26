// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Configurable Kestrel HTTP/2 and connection limits for the gRPC endpoint. Bounds the number of
/// concurrent streams a single agent connection can open so one misbehaving agent cannot exhaust
/// the server, and caps total concurrent connections per replica.
/// </summary>
public sealed class KestrelHttp2Options
{
    /// <summary>Maximum concurrent HTTP/2 streams a single connection may open. Defaults to 100.</summary>
    public int MaxStreamsPerConnection { get; set; } = 100;

    /// <summary>Maximum total concurrent connections per replica. Defaults to 20000.</summary>
    public long MaxConcurrentConnections { get; set; } = 20000;
}
