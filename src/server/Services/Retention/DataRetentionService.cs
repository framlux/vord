// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Server.Services.Retention;

/// <summary>
/// Background service that periodically soft-deletes alert events, audit log entries,
/// and remote commands based on per-tenant retention policies. Uses a distributed lock
/// to ensure only one replica runs at a time.
/// </summary>
public sealed class DataRetentionService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan LockTtl = TimeSpan.FromHours(2);
    private const string LockKey = "lock:data-retention";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<DataRetentionService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="DataRetentionService"/> class.
    /// </summary>
    public DataRetentionService(
        IServiceScopeFactory scopeFactory,
        IDistributedLock distributedLock,
        ILogger<DataRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _distributedLock = distributedLock;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken);

        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await using LockHandle? lockHandle = await _distributedLock.TryAcquireAsync(LockKey, LockTtl);
                if (lockHandle is null)
                {
                    _logger.LogDebug("Data retention: another instance holds the lock, skipping this cycle");
                }
                else
                {
                    await PurgeOldDataAsync(stoppingToken);
                }

                await Task.Delay(RunInterval, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error purging old data for retention");
            }
        }
    }

    private async Task PurgeOldDataAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        List<TenantSubscription> subscriptions = await db.TenantSubscriptions
            .ToListAsync(ct);

        int totalAlertEvents = 0;
        int totalAuditEntries = 0;
        int totalRemoteCommands = 0;

        foreach (TenantSubscription subscription in subscriptions)
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-subscription.RetentionDays);

            int alertEvents = await db.AlertEvents
                .Where(e => e.TenantId == subscription.TenantId &&
                    e.TriggeredAt < cutoff &&
                    e.DeletedAt == null)
                .Set(e => e.DeletedAt, DateTimeOffset.UtcNow)
                .UpdateAsync(ct);

            int auditEntries = await db.AuditLog
                .Where(e => e.TenantId == subscription.TenantId &&
                    e.Timestamp < cutoff &&
                    e.DeletedAt == null)
                .Set(e => e.DeletedAt, DateTimeOffset.UtcNow)
                .UpdateAsync(ct);

            int remoteCommands = await db.RemoteCommands
                .Where(c => c.TenantId == subscription.TenantId &&
                    c.CreatedAt < cutoff &&
                    c.DeletedAt == null)
                .Set(c => c.DeletedAt, DateTimeOffset.UtcNow)
                .UpdateAsync(ct);

            if ((alertEvents > 0) || (auditEntries > 0) || (remoteCommands > 0))
            {
                _logger.LogInformation(
                    "Data retention: soft-deleted {AlertEvents} alerts, {AuditEntries} audit entries, {RemoteCommands} commands for tenant {TenantId} (retention: {Days} days)",
                    alertEvents, auditEntries, remoteCommands, subscription.TenantId, subscription.RetentionDays);
            }

            totalAlertEvents += alertEvents;
            totalAuditEntries += auditEntries;
            totalRemoteCommands += remoteCommands;
        }

        _logger.LogInformation(
            "Data retention complete: soft-deleted {AlertEvents} alerts, {AuditEntries} audit entries, {RemoteCommands} commands",
            totalAlertEvents, totalAuditEntries, totalRemoteCommands);
    }
}
