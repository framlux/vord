// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models;

namespace Framlux.FleetManagement.Server.Endpoints;

/// <summary>
/// Reads the pageSize query parameter for a paginated collection endpoint.
/// </summary>
/// <remarks>
/// Every paginated endpoint answers the same way so a caller can learn one rule: an omitted page
/// size gets the default, a servable one is honoured, and anything else is refused. Endpoints used
/// to clamp instead, which handed back a short page indistinguishable from the end of the
/// collection — the bug that let the alert-rule picker believe it had drawn a whole fleet.
/// </remarks>
internal static class PageSizeQuery
{
    /// <summary>
    /// The refusal message. Names the ceiling, because a caller who guessed wrong has to learn the
    /// real number here or the guessing simply moves one step later.
    /// </summary>
    internal static string OutOfRangeMessage => $"pageSize must be between 1 and {PaginationLimits.MaxPageSize}.";

    /// <summary>
    /// Resolves the page size an endpoint should serve.
    /// </summary>
    /// <param name="requested">The value the caller supplied, or null when it was omitted.</param>
    /// <param name="pageSize">The page size to serve when this returns <c>true</c>.</param>
    /// <returns><c>true</c> when the request can be served, <c>false</c> when it must be refused.</returns>
    internal static bool TryResolve(int? requested, out int pageSize)
    {
        pageSize = requested ?? PaginationLimits.DefaultPageSize;

        return (requested is null) || PaginationLimits.IsValidPageSize(requested.Value);
    }
}
