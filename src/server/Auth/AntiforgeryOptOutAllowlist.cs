// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Collections.Immutable;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Reviewed list of endpoint types that are permitted to carry
/// <see cref="SkipAntiforgeryAttribute"/>. Every <c>[SkipAntiforgery]</c> usage must have
/// a matching entry in <see cref="Entries"/>; regression tests in
/// <c>AntiforgeryEnrollmentRegressionTests</c> fail when:
/// <list type="bullet">
///   <item>An endpoint carries the attribute but its type is missing from the allowlist.</item>
///   <item>An allowlist entry no longer points to an endpoint that carries the attribute.</item>
/// </list>
/// This makes opting out of CSRF protection a deliberate, code-reviewable change rather
/// than a silent bypass that slips into a PR alongside unrelated work.
/// </summary>
internal static class AntiforgeryOptOutAllowlist
{
    /// <summary>
    /// Full type names (<c>Type.FullName</c>) of endpoint classes permitted to opt out.
    /// Empty for now — no endpoint legitimately needs an opt-out today. Add an entry only
    /// after the security justification has been reviewed.
    /// </summary>
    public static readonly ImmutableHashSet<string> Entries = ImmutableHashSet.Create<string>(
        StringComparer.Ordinal);
}
