// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models;

/// <summary>
/// Shared bounds for paginated collection endpoints.
/// </summary>
public static class PaginationLimits
{
    /// <summary>
    /// The largest page a collection endpoint will serve. A caller asking for more is refused
    /// rather than quietly given a short page: a silently reduced page is indistinguishable from
    /// the end of the collection, and a caller that cannot tell the difference will believe it has
    /// seen everything.
    /// </summary>
    public const int MaxPageSize = 100;

    /// <summary>
    /// The page size used when a caller does not ask for one.
    /// </summary>
    public const int DefaultPageSize = 25;

    /// <summary>
    /// Whether an explicitly requested page size is one an endpoint will serve.
    /// </summary>
    /// <param name="pageSize">The page size the caller asked for.</param>
    /// <returns><c>true</c> when the value is within the servable range.</returns>
    public static bool IsValidPageSize(int pageSize)
    {
        return (pageSize >= 1) && (pageSize <= MaxPageSize);
    }
}
