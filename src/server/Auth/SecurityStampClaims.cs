// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Claim names for session-binding values stamped onto the cookie identity: the per-user security
/// stamp (revoked on logout and on security changes) and the authentication provider used to log in.
/// </summary>
public static class SecurityStampClaims
{
    /// <summary>The per-user security stamp claim. A mismatch with the live stamp rejects the cookie.</summary>
    public const string SecurityStampClaim = "sst";

    /// <summary>The authentication provider claim, carrying the <c>AuthProviderType</c> numeric value.</summary>
    public const string AuthProviderClaim = "apr";
}
