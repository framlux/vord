// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.DataExport;

/// <summary>
/// Background service that polls for pending data export jobs and processes them.
/// Uses a distributed lock to ensure only one replica processes exports at a time.
/// </summary>
public sealed class DataExportBackgroundService : BackgroundService
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(30);
    private const string LockKey = "lock:data-export";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedLock _distributedLock;
    private readonly IDataExportHandler _handler;
    private readonly ILogger<DataExportBackgroundService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="DataExportBackgroundService"/> class.
    /// </summary>
    public DataExportBackgroundService(
        IServiceScopeFactory scopeFactory,
        IDistributedLock distributedLock,
        IDataExportHandler handler,
        ILogger<DataExportBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(distributedLock);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _distributedLock = distributedLock;
        _handler = handler;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await using LockHandle? lockHandle = await _distributedLock.TryAcquireAsync(LockKey, LockTtl);
                if (lockHandle is null)
                {
                    _logger.LogDebug("Data export: another instance holds the lock, skipping this cycle");
                }
                else
                {
                    await ProcessPendingJobsAsync(stoppingToken);
                }

                await Task.Delay(RunInterval, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error processing data export jobs");
            }
        }
    }

    private async Task ProcessPendingJobsAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IDataExportRepository exportRepo = scope.ServiceProvider.GetRequiredService<IDataExportRepository>();

        List<DataExportJob> pendingJobs = await exportRepo.GetPendingExportJobsAsync(ct);

        foreach (DataExportJob job in pendingJobs)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            _logger.LogInformation("Processing data export job {JobId} for tenant {TenantId}", job.Id, job.TenantId);
            await _handler.ProcessExportJobAsync(job.Id, ct);
        }
    }
}
