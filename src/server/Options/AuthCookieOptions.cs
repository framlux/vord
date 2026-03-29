// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Configuration options for authentication cookie settings.
/// </summary>
public sealed class AuthCookieOptions
{
    /// <summary>
    /// The domain used for authentication cookies.
    /// </summary>
    public string CookieDomain { get; set; } = string.Empty;
}
