// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;

namespace Framlux.FleetManagement.FunctionalTest.Infrastructure;

/// <summary>
/// No-op SQL dialect for functional tests. The real PostgreSQL dialect is not compatible
/// with SQLite, but since <see cref="IMachineStateUpdater"/> is replaced with a no-op,
/// this is never invoked.
/// </summary>
internal sealed class NoOpSqlDialect : ISqlDialect
{
    /// <inheritdoc/>
    public string UpsertSystemInfo => string.Empty;

    /// <inheritdoc/>
    public string UpsertOsVersion => string.Empty;

    /// <inheritdoc/>
    public string UpsertCpuInfo => string.Empty;

    /// <inheritdoc/>
    public string UpsertMemoryInfo => string.Empty;

    /// <inheritdoc/>
    public string UpsertDiskInfo => string.Empty;

    /// <inheritdoc/>
    public string UpsertCpuUsage => string.Empty;

    /// <inheritdoc/>
    public string UpsertMemoryUsage => string.Empty;

    /// <inheritdoc/>
    public string UpsertDiskUsage => string.Empty;

    /// <inheritdoc/>
    public string UpsertHardwareHealth => string.Empty;

    /// <inheritdoc/>
    public string UpsertPackageUpdates => string.Empty;

    /// <inheritdoc/>
    public string UpsertServiceStatus => string.Empty;

    /// <inheritdoc/>
    public string UpsertLastTelemetry => string.Empty;

    /// <inheritdoc/>
    public (string Sql, string SessionsValue) BuildUpsertSshSessions(string? existingSessions, string newPayload)
    {
        return (string.Empty, string.Empty);
    }
}
