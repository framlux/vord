// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Configuration options for CORS origins.
/// </summary>
public sealed class AppCorsOptions
{
    /// <summary>
    /// The allowed CORS origins.
    /// </summary>
    public string[] Origins { get; set; } = [];
}
