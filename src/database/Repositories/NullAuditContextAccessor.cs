// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Null-object implementation of <see cref="IAuditContextAccessor"/> that always returns
/// <see langword="null"/>. Used as the safe default for background workers and any context
/// where no HTTP request is in flight.
/// </summary>
public sealed class NullAuditContextAccessor : IAuditContextAccessor
{
    /// <inheritdoc/>
    public string? GetClientIp()
    {
        return null;
    }
}
