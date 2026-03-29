// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Framlux.FleetManagement.MigrationRunner.Services;

/// <summary>
/// Health check reporting readiness only after migrations succeed.
/// </summary>
public sealed class MigrationCompletionHealthCheck : IHealthCheck
{
    private readonly MigrationState _state;

    /// <summary>
    /// Creates a new instance of <see cref="MigrationCompletionHealthCheck"/>.
    /// </summary>
    /// <param name="state">The migration state machine</param>
    public MigrationCompletionHealthCheck(MigrationState state) => _state = state;

    /// <summary>
    /// Checks the health of the migrations.
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        // If migrations are not yet complete, report unhealthy.
        if (_state.Completed == false)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Migrations not finished"));
        }

        // If migrations failed, report unhealthy with error.
        if (_state.Succeeded == false)
        {
            string message = _state.Error is null ? "Migrations failed" : $"Migrations failed: {_state.Error.Message}";

            return Task.FromResult(HealthCheckResult.Unhealthy(message, _state.Error));
        }

        // If migrations completed successfully, report healthy.

        return Task.FromResult(HealthCheckResult.Healthy("Migrations complete"));
    }
}
