// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Collections.Frozen;
using FastEndpoints;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Decision helper for the global-by-default antiforgery enrollment performed inside
/// <c>FastEndpoints.Endpoints.Configurator</c>. Pure function — given an
/// <see cref="EndpointDefinition"/>, returns whether the endpoint should be enrolled in
/// FE's antiforgery middleware. Factored out so the rule is unit-testable without spinning
/// up the FastEndpoints host.
/// </summary>
internal static class AntiforgeryEnrollment
{
    private static readonly FrozenSet<string> SafeVerbs = FrozenSet.ToFrozenSet(
        new[] { "GET", "HEAD", "OPTIONS" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <c>true</c> when the endpoint should have FE antiforgery enabled.
    /// Rules, in order:
    /// <list type="number">
    ///   <item>All HTTP verbs the endpoint accepts are safe (no state change) → <c>false</c>.</item>
    ///   <item>Endpoint type carries <see cref="SkipAntiforgeryAttribute"/> → <c>false</c>.</item>
    ///   <item>Otherwise → <c>true</c>.</item>
    /// </list>
    /// Non-cookie-authenticated callers are skipped at request-time by the
    /// <c>UseAntiforgeryFE</c> <c>skipRequestFilter</c>, so this enrollment decision does not
    /// consider the auth scheme.
    /// </summary>
    /// <param name="endpoint">The endpoint definition being configured.</param>
    /// <returns><c>true</c> if FE antiforgery should be enabled; otherwise, <c>false</c>.</returns>
    public static bool ShouldEnforce(EndpointDefinition endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return ShouldEnforce(endpoint.Verbs, endpoint.EndpointAttributes);
    }

    /// <summary>
    /// Testable overload that operates on the primitive inputs of <see cref="ShouldEnforce(EndpointDefinition)"/>
    /// so the rule can be unit-tested without constructing a <see cref="EndpointDefinition"/>
    /// (whose mutable state is set internally by FastEndpoints at registration time).
    /// </summary>
    /// <param name="verbs">The HTTP verbs the endpoint accepts.</param>
    /// <param name="attributes">The attribute instances declared on the endpoint type.</param>
    /// <returns><c>true</c> if FE antiforgery should be enabled; otherwise, <c>false</c>.</returns>
    public static bool ShouldEnforce(string[]? verbs, object[]? attributes)
    {
        if (HasOnlySafeVerbs(verbs))
        {
            return false;
        }

        if (HasOptOutAttribute(attributes))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns <c>true</c> when every verb in <paramref name="verbs"/> is in the safe set
    /// (GET, HEAD, OPTIONS). Null/empty arrays return <c>false</c> — a misconfigured endpoint
    /// with no declared verbs should fail closed (enforce antiforgery) rather than fail open.
    /// </summary>
    internal static bool HasOnlySafeVerbs(string[]? verbs)
    {
        if ((verbs is null) || (verbs.Length == 0))
        {
            return false;
        }

        foreach (string verb in verbs)
        {
            if (SafeVerbs.Contains(verb) == false)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns <c>true</c> when the attribute list contains a <see cref="SkipAntiforgeryAttribute"/>.
    /// </summary>
    internal static bool HasOptOutAttribute(object[]? attributes)
    {
        if ((attributes is null) || (attributes.Length == 0))
        {
            return false;
        }

        foreach (object attribute in attributes)
        {
            if (attribute is SkipAntiforgeryAttribute)
            {
                return true;
            }
        }

        return false;
    }
}
