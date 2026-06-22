// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Provides the client IP address for the current request context, used when
/// persisting audit log entries. Implementations vary by host: HTTP requests
/// return the remote IP via <see cref="Microsoft.AspNetCore.Http.IHttpContextAccessor"/>;
/// background workers use the null-object default that leaves the IP unset.
/// </summary>
public interface IAuditContextAccessor
{
    /// <summary>
    /// Returns the client IP address for the current request, or <see langword="null"/>
    /// when no HTTP context is active (e.g., background jobs or worker processes).
    /// </summary>
    string? GetClientIp();
}
