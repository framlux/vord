// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Background service that manages daily range partitions for time-series tables on PostgreSQL.
/// Creates future partitions ahead of time and drops partitions that exceed the maximum
/// retention period across all tenants plus a buffer period.
/// This service is a no-op on SQLite.
/// </summary>
public sealed class PartitionManagementService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(30);
    private const string LockKey = "lock:partition-management";
    private const int DaysAhead = 7;
    internal const int DropBufferDays = 2;

    /// <summary>
    /// The earliest date from which partitions may exist. Partitions prior to this are never checked.
    /// Matches the initial migration's partition range.
    /// </summary>
    private static readonly DateOnly PartitionOriginDate = new(2026, 1, 1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISqlDialect _sqlDialect;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<PartitionManagementService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="PartitionManagementService"/> class.
    /// </summary>
    public PartitionManagementService(
        IServiceScopeFactory scopeFactory,
        ISqlDialect sqlDialect,
        IDistributedLock distributedLock,
        ILogger<PartitionManagementService> logger)
    {
        _scopeFactory = scopeFactory;
        _sqlDialect = sqlDialect;
        _distributedLock = distributedLock;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_sqlDialect.SupportsPartitioning == false)
        {
            _logger.LogDebug("Partition management: skipping, database does not support partitioning");

            return;
        }

        await Task.Delay(StartupDelay, stoppingToken);

        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await using LockHandle? lockHandle = await _distributedLock.TryAcquireAsync(LockKey, LockTtl);
                if (lockHandle is null)
                {
                    _logger.LogDebug("Partition management: another instance holds the lock, skipping this cycle");
                }
                else
                {
                    await ManagePartitionsAsync(stoppingToken);
                }

                await Task.Delay(RunInterval, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error managing partitions");
            }
        }
    }

    private async Task ManagePartitionsAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        await CreateFuturePartitionsAsync(db, ct);
        await DropExpiredPartitionsAsync(db, ct);
    }

    private async Task CreateFuturePartitionsAsync(DatabaseContext db, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int partitionsCreated = 0;

        foreach (PartitionedTableConfig.PartitionedTable table in PartitionedTableConfig.Tables)
        {
            for (int offset = 0; offset <= DaysAhead; offset++)
            {
                DateOnly target = DateOnly.FromDateTime(now.AddDays(offset).UtcDateTime);
                string sql = BuildCreatePartitionSql(table.TableName, target);

                try
                {
                    await db.ExecuteAsync(sql, ct);
                    partitionsCreated++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Partition management: could not create partition for {Table} ({Date})",
                        table.TableName, target);
                }
            }
        }

        if (partitionsCreated > 0)
        {
            _logger.LogInformation("Partition management: ensured {Count} partition(s) exist", partitionsCreated);
        }
    }

    internal async Task DropExpiredPartitionsAsync(DatabaseContext db, CancellationToken ct)
    {
        // Determine the oldest date we need to keep: max retention across all tiers + buffer days.
        int? maxRetentionDays = await db.TierFeatureLimits
            .MaxAsync(l => (int?)l.RetentionDays, ct);

        if (maxRetentionDays is null)
        {
            return;
        }

        DateOnly cutoff = DateOnly.FromDateTime(
            DateTimeOffset.UtcNow
                .AddDays(-maxRetentionDays.Value)
                .AddDays(-DropBufferDays)
                .UtcDateTime);

        // Walk from the partition origin and drop partitions whose day is before the cutoff.
        // Each daily partition covers exactly [date, date+1), so it is safe to drop when date < cutoff.
        // Non-existent partitions are silently skipped by the IF EXISTS clause.
        foreach (PartitionedTableConfig.PartitionedTable table in PartitionedTableConfig.Tables)
        {
            DateOnly cursor = PartitionOriginDate;

            while (cursor < cutoff)
            {
                string partitionName = BuildPartitionName(table.TableName, cursor);
                string dropSql = $@"DROP TABLE IF EXISTS ""{partitionName}""";

                try
                {
                    await db.ExecuteAsync(dropSql, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Partition management: could not drop partition {Partition}", partitionName);
                }

                cursor = cursor.AddDays(1);
            }
        }

        _logger.LogDebug(
            "Partition management: expired partition cleanup complete (cutoff: {Cutoff})", cutoff);
    }

    /// <summary>
    /// Builds the partition table name for a given date.
    /// </summary>
    internal static string BuildPartitionName(string tableName, DateOnly date)
    {
        return $"{tableName.ToLowerInvariant()}_d{date:yyyyMMdd}";
    }

    /// <summary>
    /// Builds the SQL statement to create a daily partition for the given table and date.
    /// </summary>
    internal static string BuildCreatePartitionSql(string tableName, DateOnly date)
    {
        string partitionName = BuildPartitionName(tableName, date);
        DateOnly nextDay = date.AddDays(1);

        return $"""
            CREATE TABLE IF NOT EXISTS "{partitionName}" PARTITION OF "{tableName}"
            FOR VALUES FROM ('{date:yyyy-MM-dd}') TO ('{nextDay:yyyy-MM-dd}')
            """;
    }
}
