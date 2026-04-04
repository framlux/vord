// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Periodically scans for telemetry rows that are newer than the corresponding
/// MachineState.LastTelemetryAt, indicating that the Redis Stream publish was
/// missed (e.g., due to a process crash or Redis outage). Re-derives state
/// updates from those rows and applies them via the batch updater.
/// </summary>
public sealed class MachineStateReconciliationService : BackgroundService
{
    /// <summary>
    /// How often to run the reconciliation sweep.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of telemetry rows to process per sweep to bound memory usage.
    /// </summary>
    private const int MaxRowsPerSweep = 1000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMachineStateUpdater _stateUpdater;
    private readonly ILogger<MachineStateReconciliationService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineStateReconciliationService"/> class.
    /// </summary>
    public MachineStateReconciliationService(
        IServiceScopeFactory scopeFactory,
        IMachineStateUpdater stateUpdater,
        ILogger<MachineStateReconciliationService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _stateUpdater = stateUpdater ?? throw new ArgumentNullException(nameof(stateUpdater));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay startup so the main consumer processes the majority of updates first.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        _logger.LogInformation("MachineState reconciliation service started");

        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                int reconciled = await ReconcileAsync(stoppingToken);
                if (reconciled > 0)
                {
                    _logger.LogInformation("Reconciliation applied state updates for {Count} machines", reconciled);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during MachineState reconciliation sweep");
            }

            await Task.Delay(Interval, stoppingToken);
        }

        _logger.LogInformation("MachineState reconciliation service stopped");
    }

    /// <summary>
    /// Finds telemetry rows newer than each machine's LastTelemetryAt and re-applies them.
    /// Returns the number of machines that were reconciled.
    /// </summary>
    private async Task<int> ReconcileAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        // Find telemetry rows that are newer than the corresponding MachineState.LastTelemetryAt.
        // This catches rows where the Redis Stream publish failed after the DB insert.
        List<MachineTelemetry> orphaned = await (
            from t in db.MachineTelemetry
            join s in db.MachineStates on t.MachineId equals s.MachineId into stateJoin
            from s in stateJoin.DefaultIfEmpty()
            where s == null || t.ReceivedAt > s.LastTelemetryAt
            orderby t.ReceivedAt
            select t)
            .Take(MaxRowsPerSweep)
            .ToListAsync(ct);

        if (orphaned.Count == 0)
        {
            return 0;
        }

        // Group by machine and build update messages.
        Dictionary<long, List<StateUpdateMessage>> updatesByMachine = [];
        foreach (MachineTelemetry row in orphaned)
        {
            if (updatesByMachine.TryGetValue(row.MachineId, out List<StateUpdateMessage>? updates) == false)
            {
                updates = [];
                updatesByMachine[row.MachineId] = updates;
            }

            updates.Add(new StateUpdateMessage
            {
                TelemetryType = row.TelemetryType,
                Payload = row.Payload,
                ReceivedAt = row.ReceivedAt,
            });
        }

        await _stateUpdater.UpdateBatchAsync(updatesByMachine, ct);

        return updatesByMachine.Count;
    }
}
