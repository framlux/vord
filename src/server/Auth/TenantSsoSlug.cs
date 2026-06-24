// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Globalization;
using Microsoft.AspNetCore.DataProtection;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Builds and resolves the opaque slug that the SSO email-lookup hands to the browser in place of
/// the raw numeric tenant id. The slug is a data-protected token of the tenant id, so it can be
/// reversed only server-side and exposes nothing about the tenant to an enumeration attacker.
/// </summary>
public static class TenantSsoSlug
{
    /// <summary>
    /// The data-protection purpose string scoping the slug protector. Must be identical at build
    /// and resolve time for the token to round-trip.
    /// </summary>
    public const string Purpose = "TenantSsoChallengeSlug";

    /// <summary>
    /// Builds an opaque, non-enumerable slug for the given tenant id.
    /// </summary>
    /// <param name="protector">A protector created from <see cref="Purpose"/>.</param>
    /// <param name="tenantId">The tenant id to encode.</param>
    /// <returns>An opaque token that never contains the decimal tenant id.</returns>
    public static string Build(IDataProtector protector, int tenantId)
    {
        ArgumentNullException.ThrowIfNull(protector);

        return protector.Protect(tenantId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Resolves an opaque slug back to its tenant id.
    /// </summary>
    /// <param name="protector">A protector created from <see cref="Purpose"/>.</param>
    /// <param name="slug">The opaque slug supplied by the client.</param>
    /// <param name="tenantId">The resolved tenant id when successful; otherwise zero.</param>
    /// <returns><c>true</c> when the slug is valid and round-trips to a tenant id; otherwise <c>false</c>.</returns>
    public static bool TryResolve(IDataProtector protector, string? slug, out int tenantId)
    {
        ArgumentNullException.ThrowIfNull(protector);

        tenantId = 0;
        if (string.IsNullOrWhiteSpace(slug))
        {
            return false;
        }

        string plaintext;
        try
        {
            plaintext = protector.Unprotect(slug);
        }
        catch (Exception)
        {
            return false;
        }

        return int.TryParse(plaintext, NumberStyles.Integer, CultureInfo.InvariantCulture, out tenantId);
    }
}
