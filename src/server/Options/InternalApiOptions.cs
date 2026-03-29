// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Configuration options for internal API authentication.
/// </summary>
public sealed class InternalApiOptions
{
    /// <summary>
    /// The shared key used to authenticate internal gRPC calls.
    /// </summary>
    public string Key { get; set; } = string.Empty;
}
