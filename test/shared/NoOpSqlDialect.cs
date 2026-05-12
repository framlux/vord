// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// No-op SQL dialect for functional tests. The real PostgreSQL dialect is not compatible
/// with SQLite. The health sweep uses the SqliteSqlDialect directly in functional tests.
/// </summary>
internal sealed class NoOpSqlDialect : ISqlDialect
{
    /// <inheritdoc/>
    public bool SupportsPartitioning => false;

    /// <inheritdoc/>
    public bool SupportsJsonbFilters => false;

    /// <inheritdoc/>
    public bool SupportsJsonbSort => false;

    /// <inheritdoc/>
    public string HealthSweepForTenant => string.Empty;

    /// <inheritdoc/>
    public string StaleSweepSql => string.Empty;
}
