// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the authentication provider used by a user account.
/// </summary>
public enum AuthProviderType : short
{
    /// <summary>Unknown or pre-existing user with no recorded provider.</summary>
    Unknown = 0,
    /// <summary>Authenticated via GitHub OAuth.</summary>
    GitHub = 1,
    /// <summary>Authenticated via Google OAuth.</summary>
    Google = 2,
    /// <summary>Authenticated via Microsoft OAuth.</summary>
    Microsoft = 3,
    /// <summary>Authenticated via tenant-specific custom OIDC.</summary>
    CustomOidc = 4,
}
