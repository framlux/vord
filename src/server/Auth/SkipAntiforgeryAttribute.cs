// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Marker attribute that opts a FastEndpoints endpoint out of the project's
/// global-by-default antiforgery enforcement.
///
/// Default policy: every state-changing FastEndpoints endpoint (verbs other than
/// GET/HEAD/OPTIONS) is enrolled in antiforgery enforcement during
/// <c>Endpoints.Configurator</c> startup. Endpoints carrying this attribute are skipped.
///
/// Adding this attribute to a new endpoint requires a matching entry in
/// <see cref="AntiforgeryOptOutAllowlist"/>. A regression test enforces that link so every
/// opt-out is a deliberate, reviewable change rather than a silent CSRF bypass.
///
/// Use sparingly. Legitimate reasons to opt out:
/// <list type="bullet">
///   <item>Webhook endpoints that authenticate via HMAC signature (e.g., Stripe).</item>
///   <item>Public anonymous endpoints that intentionally accept cross-origin form posts
///         (rare — most should remain JSON-only and rely on CORS).</item>
///   <item>Endpoints called only via the agent API-key scheme (the global skip filter
///         already bypasses these at runtime, but the attribute documents intent).</item>
/// </list>
///
/// Per saved feedback (no path-string matching for security): we use this attribute on
/// the endpoint type rather than matching request paths in middleware.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class SkipAntiforgeryAttribute : Attribute
{
}
