// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Machines;

namespace Framlux.FleetManagement.FunctionalTest.Infrastructure;

/// <summary>
/// No-op implementation of <see cref="IMachineStateUpdater"/> for functional testing.
/// The real implementation uses PostgreSQL-specific SQL for upserts which is incompatible
/// with the SQLite test database.
/// </summary>
public sealed class NoOpMachineStateUpdater : IMachineStateUpdater
{
    /// <inheritdoc/>
    public Task UpdateAsync(long machineId, short telemetryType, string payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateBatchAsync(Dictionary<long, List<StateUpdateMessage>> updatesByMachine, CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
