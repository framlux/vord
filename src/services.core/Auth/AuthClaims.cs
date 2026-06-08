// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;

namespace Framlux.FleetManagement.Services.Core.Auth;

/// <summary>
/// Central constants and helpers for the custom claims this application stamps onto
/// authenticated users. Centralizing prevents drift between the Admin authorization policy,
/// the Hangfire dashboard authorization filter, the cookie-principal validator, and any
/// future iga-checking endpoint code.
/// </summary>
public static class AuthClaims
{
    /// <summary>The "is global admin" claim name written into the cookie identity.</summary>
    public const string IsGlobalAdmin = "iga";

    /// <summary>The canonical truthy value for <see cref="IsGlobalAdmin"/> — matches
    /// <see cref="bool.TrueString"/> ("True"). Comparisons are case-insensitive so
    /// "true", "TRUE", etc. all read as true.</summary>
    public const string IsGlobalAdminValueTrue = "True";

    /// <summary>
    /// Returns whether the supplied principal carries the <see cref="IsGlobalAdmin"/> claim with
    /// a truthy value. Returns <c>false</c> if the claim is absent OR the value is anything other
    /// than (case-insensitive) "true".
    /// </summary>
    /// <param name="user">The principal to inspect.</param>
    /// <returns>True if the user has the iga claim set to True.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="user"/> is null.</exception>
    public static bool IsUserGlobalAdmin(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        string? value = user.FindFirstValue(IsGlobalAdmin);

        return string.Equals(value, IsGlobalAdminValueTrue, StringComparison.OrdinalIgnoreCase);
    }
}
