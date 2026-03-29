// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.MigrationRunner.Services;

/// <summary>
/// Abstraction for executing database schema migrations.
/// </summary>
public interface IDbMigrator
{
    /// <summary>
    /// Executes all pending migrations (idempotent).
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken);
}
