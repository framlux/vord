// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Background service that manages monthly range partitions for time-series tables on PostgreSQL.
/// Creates future partitions ahead of time and drops partitions that exceed the maximum
/// retention period across all tenants plus a configurable grace period.
/// This service is a no-op on SQLite.
/// </summary>
public sealed class PartitionManagementService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(30);
    private const string LockKey = "lock:partition-management";
    private const int MonthsAhead = 3;

    /// <summary>
    /// The earliest year from which partitions may exist. Partitions prior to this are never checked.
    /// Matches the initial migration's partition range.
    /// </summary>
    private const int PartitionOriginYear = 2026;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISqlDialect _sqlDialect;
    private readonly ServerConfigurationService _configService;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<PartitionManagementService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="PartitionManagementService"/> class.
    /// </summary>
    public PartitionManagementService(
        IServiceScopeFactory scopeFactory,
        ISqlDialect sqlDialect,
        ServerConfigurationService configService,
        IDistributedLock distributedLock,
        ILogger<PartitionManagementService> logger)
    {
        _scopeFactory = scopeFactory;
        _sqlDialect = sqlDialect;
        _configService = configService;
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
            for (int offset = 0; offset <= MonthsAhead; offset++)
            {
                DateTimeOffset target = now.AddMonths(offset);
                string sql = BuildCreatePartitionSql(table.TableName, target.Year, target.Month);

                try
                {
                    await db.ExecuteAsync(sql, ct);
                    partitionsCreated++;
                }
                catch (Exception ex)
                {
                    // Partition may already exist or overlap with an existing range.
                    _logger.LogDebug(ex, "Partition management: could not create partition for {Table} ({Year}-{Month:D2})",
                        table.TableName, target.Year, target.Month);
                }
            }
        }

        if (partitionsCreated > 0)
        {
            _logger.LogInformation("Partition management: ensured {Count} partition(s) exist", partitionsCreated);
        }
    }

    private async Task DropExpiredPartitionsAsync(DatabaseContext db, CancellationToken ct)
    {
        // Determine the oldest date we need to keep: max retention across all tenants + grace period.
        int? maxRetentionDays = await db.TenantSubscriptions
            .MaxAsync(s => (int?)s.RetentionDays, ct);

        if (maxRetentionDays is null)
        {
            return;
        }

        TimeSpan gracePeriod = await _configService.GetTelemetryCleanupGracePeriodAsync(ct);
        DateTimeOffset cutoff = DateTimeOffset.UtcNow
            .AddDays(-maxRetentionDays.Value)
            .Subtract(gracePeriod);

        // Walk from the partition origin up through the cutoff month and issue DROP TABLE IF EXISTS.
        // Non-existent partitions are silently skipped by the IF EXISTS clause.
        foreach (PartitionedTableConfig.PartitionedTable table in PartitionedTableConfig.Tables)
        {
            DateTimeOffset cursor = new(PartitionOriginYear, 1, 1, 0, 0, 0, TimeSpan.Zero);

            while (cursor < cutoff)
            {
                string partitionName = BuildPartitionName(table.TableName, cursor.Year, cursor.Month);
                string dropSql = $@"DROP TABLE IF EXISTS ""{partitionName}""";

                try
                {
                    await db.ExecuteAsync(dropSql, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Partition management: could not drop partition {Partition}", partitionName);
                }

                cursor = cursor.AddMonths(1);
            }
        }

        _logger.LogDebug(
            "Partition management: expired partition cleanup complete (cutoff: {Cutoff})", cutoff);
    }

    private static string BuildPartitionName(string tableName, int year, int month)
    {
        return $"{tableName.ToLowerInvariant()}_y{year}m{month:D2}";
    }

    private static string BuildCreatePartitionSql(string tableName, int year, int month)
    {
        int nextYear = month == 12 ? year + 1 : year;
        int nextMonth = month == 12 ? 1 : month + 1;
        string partitionName = BuildPartitionName(tableName, year, month);

        return $"""
            CREATE TABLE IF NOT EXISTS "{partitionName}" PARTITION OF "{tableName}"
            FOR VALUES FROM ('{year}-{month:D2}-01') TO ('{nextYear}-{nextMonth:D2}-01')
            """;
    }
}
