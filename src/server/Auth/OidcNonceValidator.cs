// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Validates the OIDC nonce for the manual code-exchange path and enforces single-use semantics
/// so a captured id_token cannot be replayed. The middleware's built-in nonce validation is
/// skipped by that path, so this check restores it.
/// </summary>
public static class OidcNonceValidator
{
    /// <summary>
    /// Cookie name prefix that ASP.NET Core's OpenID Connect handler uses for the nonce cookie.
    /// </summary>
    public const string NonceCookiePrefix = ".AspNetCore.OpenIdConnect.Nonce.";

    private static readonly TimeSpan ReplayWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Confirms the token's nonce matches the value bound to the browser at challenge time and
    /// that this nonce has not been seen before. Both checks must pass.
    /// </summary>
    /// <param name="redisDb">The Redis database used for the single-use marker.</param>
    /// <param name="cookieNonce">The nonce value recovered from the request's nonce cookie.</param>
    /// <param name="tokenNonce">The nonce claim from the validated id_token.</param>
    /// <returns>True when the nonce is present, matches, and is being used for the first time.</returns>
    public static async Task<bool> ValidateAndConsumeAsync(IDatabase redisDb, string? cookieNonce, string? tokenNonce)
    {
        if (string.IsNullOrEmpty(cookieNonce) || string.IsNullOrEmpty(tokenNonce))
        {
            return false;
        }

        if (string.Equals(cookieNonce, tokenNonce, StringComparison.Ordinal) == false)
        {
            return false;
        }

        string replayKey = $"oidc:nonce:{tokenNonce}";
        bool firstUse = await redisDb.StringSetAsync(replayKey, "1", ReplayWindow, false, When.NotExists);

        return firstUse;
    }
}
