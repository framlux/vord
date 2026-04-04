// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using LinqToDB.Data;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Periodically recomputes HealthStatus for all MachineState rows.
/// This ensures that machines which stop sending telemetry and pings
/// transition to Offline in the database, keeping fleet overview counts accurate.
/// </summary>
public sealed class HealthRecomputationService : BackgroundService
{
    /// <summary>
    /// How often to recompute health statuses for all machines.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISqlDialect _dialect;
    private readonly ServerConfigurationService _configService;
    private readonly ILogger<HealthRecomputationService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="HealthRecomputationService"/> class.
    /// </summary>
    public HealthRecomputationService(
        IServiceScopeFactory scopeFactory,
        ISqlDialect dialect,
        ServerConfigurationService configService,
        ILogger<HealthRecomputationService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief delay to let the application finish starting.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        _logger.LogInformation("Health recomputation service started");

        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await RecomputeAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health recomputation sweep");
            }

            await Task.Delay(Interval, stoppingToken);
        }

        _logger.LogInformation("Health recomputation service stopped");
    }

    private async Task RecomputeAllAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);

        int rowsAffected = await db.ExecuteAsync(
            _dialect.RecomputeAllHealthStatuses,
            ct,
            new DataParameter("onlineThresholdSeconds", (int)onlineThreshold.TotalSeconds));

        if (rowsAffected > 0)
        {
            _logger.LogDebug("Health recomputation updated {Count} machine state rows", rowsAffected);
        }
    }
}
