// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Hangfire;
using Npgsql;

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// Hangfire recurring job that manages daily range partitions for time-series tables on PostgreSQL.
/// Creates future partitions ahead of time and drops partitions that exceed the maximum retention
/// period across all tenants plus a buffer period. No-op on SQLite. Replaces the former
/// PartitionManagementService.
/// </summary>
public sealed class PartitionManagementJob
{
    private const int DaysAhead = 7;

    /// <summary>
    /// Number of additional days of headroom kept beyond the maximum retention period before
    /// dropping expired partitions. Prevents accidental data loss at the edge of retention.
    /// </summary>
    internal const int DropBufferDays = 2;

    /// <summary>
    /// The earliest date from which partitions may exist. Partitions prior to this are never checked.
    /// Matches the initial migration's partition range.
    /// </summary>
    private static readonly DateOnly PartitionOriginDate = new(2026, 1, 1);

    private readonly IPartitionRepository _partitionRepository;
    private readonly ISqlDialect _sqlDialect;
    private readonly ILogger<PartitionManagementJob> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="PartitionManagementJob"/> class.
    /// </summary>
    /// <param name="partitionRepository">Repository for partition DDL and retention queries.</param>
    /// <param name="sqlDialect">The SQL dialect, used to gate partition operations to PostgreSQL.</param>
    /// <param name="logger">The logger.</param>
    public PartitionManagementJob(
        IPartitionRepository partitionRepository,
        ISqlDialect sqlDialect,
        ILogger<PartitionManagementJob> logger)
    {
        ArgumentNullException.ThrowIfNull(partitionRepository);
        ArgumentNullException.ThrowIfNull(sqlDialect);
        ArgumentNullException.ThrowIfNull(logger);

        _partitionRepository = partitionRepository;
        _sqlDialect = sqlDialect;
        _logger = logger;
    }

    /// <summary>
    /// Runs the partition maintenance pass.
    /// </summary>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("long")]
    public async Task RunAsync(CancellationToken ct)
    {
        if (_sqlDialect.SupportsPartitioning == false)
        {
            _logger.LogDebug("Partition management: skipping, database does not support partitioning");

            return;
        }

        await CreateFuturePartitionsAsync(ct);
        await DropExpiredPartitionsAsync(ct);
    }

    private async Task CreateFuturePartitionsAsync(CancellationToken ct)
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
                    await _partitionRepository.ExecutePartitionDdlAsync(sql, ct);
                    partitionsCreated++;
                }
                catch (Exception ex)
                {
                    // PostgreSQL SqlState 42P07 means the partition already exists — expected on every
                    // run because the create-future loop overlaps with the previous run. Any other
                    // failure (disk-full, permissions, lock timeout) is a real problem that must be
                    // visible in production logs at Warning rather than silenced at Debug.
                    if ((ex is PostgresException pg) && (pg.SqlState == "42P07"))
                    {
                        _logger.LogDebug(ex, "Partition management: partition {Table} ({Date}) already exists",
                            table.TableName, target);
                    }
                    else
                    {
                        _logger.LogWarning(ex, "Partition management: failed to create partition {Table} ({Date})",
                            table.TableName, target);
                    }
                }
            }
        }

        if (partitionsCreated > 0)
        {
            _logger.LogInformation("Partition management: ensured {Count} partition(s) exist", partitionsCreated);
        }
    }

    /// <summary>
    /// Drops partitions whose date range is past the configured retention plus a safety buffer.
    /// Exposed as internal for the unit tests; the production path calls it via <see cref="RunAsync"/>.
    /// </summary>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    internal async Task DropExpiredPartitionsAsync(CancellationToken ct)
    {
        int? maxRetentionDays = await _partitionRepository.GetMaxRetentionDaysAsync(ct);

        if (maxRetentionDays is null)
        {
            return;
        }

        DateOnly cutoff = DateOnly.FromDateTime(
            DateTimeOffset.UtcNow
                .AddDays(-maxRetentionDays.Value)
                .AddDays(-DropBufferDays)
                .UtcDateTime);

        // Bound the lookback to MaxRetentionDays + a 7-day safety buffer. Anything older than that
        // has already been dropped on a prior tick — DROP IF EXISTS makes additional attempts
        // harmless, but scanning every day from PartitionOriginDate forever is O(deployment
        // lifetime), producing hundreds of DDL no-ops per table per run after a year or two.
        int lookbackDays = maxRetentionDays.Value + 7;
        DateOnly walkStart = cutoff.AddDays(-lookbackDays);

        if (walkStart < PartitionOriginDate)
        {
            walkStart = PartitionOriginDate;
        }

        // Walk from the bounded start and drop partitions whose day is before the cutoff.
        // Each daily partition covers exactly [date, date+1), so it is safe to drop when date < cutoff.
        // Non-existent partitions are silently skipped by the IF EXISTS clause.
        foreach (PartitionedTableConfig.PartitionedTable table in PartitionedTableConfig.Tables)
        {
            DateOnly cursor = walkStart;

            while (cursor < cutoff)
            {
                string partitionName = BuildPartitionName(table.TableName, cursor);
                string dropSql = $@"DROP TABLE IF EXISTS ""{partitionName}""";

                try
                {
                    await _partitionRepository.ExecutePartitionDdlAsync(dropSql, ct);
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
    /// Builds the partition table name for a given date. The table name is validated against
    /// <see cref="PostgresIdentifierValidator.Validate"/> before interpolation; the date is
    /// formatted via <see cref="System.Globalization.CultureInfo.InvariantCulture"/> and the
    /// formatter output is verified to match the expected <c>yyyyMMdd</c> shape before use.
    /// </summary>
    internal static string BuildPartitionName(string tableName, DateOnly date)
    {
        PostgresIdentifierValidator.Validate(tableName, nameof(tableName));
        string datePart = date.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        if (System.Text.RegularExpressions.Regex.IsMatch(datePart, @"^\d{8}$") == false)
        {
            throw new ArgumentException(
                $"Formatted date '{datePart}' is not 8 digits; refusing to interpolate.",
                nameof(date));
        }

        return $"{tableName.ToLowerInvariant()}_d{datePart}";
    }

    /// <summary>
    /// Builds the SQL statement to create a daily partition for the given table and date.
    /// The table name is validated against <see cref="PostgresIdentifierValidator.Validate"/>
    /// before interpolation. The date bound values are formatted via invariant culture and
    /// verified to match <c>yyyy-MM-dd</c> before use.
    /// </summary>
    internal static string BuildCreatePartitionSql(string tableName, DateOnly date)
    {
        PostgresIdentifierValidator.Validate(tableName, nameof(tableName));
        string partitionName = BuildPartitionName(tableName, date);
        DateOnly nextDay = date.AddDays(1);
        string startBound = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string endBound = nextDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (System.Text.RegularExpressions.Regex.IsMatch(startBound, @"^\d{4}-\d{2}-\d{2}$") == false
            || System.Text.RegularExpressions.Regex.IsMatch(endBound, @"^\d{4}-\d{2}-\d{2}$") == false)
        {
            throw new ArgumentException(
                $"Formatted partition bounds [{startBound}, {endBound}] failed shape check; refusing to interpolate.",
                nameof(date));
        }

        return $"""
            CREATE TABLE IF NOT EXISTS "{partitionName}" PARTITION OF "{tableName}"
            FOR VALUES FROM ('{startBound}') TO ('{endBound}')
            """;
    }
}
