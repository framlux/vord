// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Repositories;
using Hangfire;
using Npgsql;

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// Hangfire recurring job that manages daily partitions for time-series tables on PostgreSQL. Creates
/// future partitions ahead of time and drops expired ones. Simple range tables use the maximum
/// (Long-class) retention window; <c>MachineTelemetry</c> is composite — one set of daily leaves per
/// retention class under a per-class parent — so each class is created and dropped on its own
/// schedule. No-op on SQLite. Replaces the former PartitionManagementService.
/// </summary>
public sealed class PartitionManagementJob
{
    private const int DaysAhead = RetentionClassPolicy.PartitionCreateAheadDays;

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

    /// <summary>
    /// Creates the future daily partitions (today through <see cref="DaysAhead"/>) for every retention
    /// class and simple range table. Exposed as internal for the unit tests; the production path calls
    /// it via <see cref="RunAsync"/>.
    /// </summary>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    internal async Task CreateFuturePartitionsAsync(CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int partitionsCreated = 0;

        for (int offset = 0; offset <= DaysAhead; offset++)
        {
            DateOnly target = DateOnly.FromDateTime(now.AddDays(offset).UtcDateTime);

            // MachineTelemetry is composite: one daily leaf per retention class under its class parent.
            foreach (RetentionClass retentionClass in PartitionedTableConfig.TelemetryRetentionClasses)
            {
                string sql = BuildCreateClassPartitionSql(retentionClass, target);
                if (await TryCreatePartitionAsync(sql, ClassParentTableName(retentionClass), target, ct))
                {
                    partitionsCreated++;
                }
            }

            // Simple daily-range tables.
            foreach (PartitionedTableConfig.PartitionedTable table in PartitionedTableConfig.RangeTables)
            {
                string sql = BuildCreatePartitionSql(table.TableName, target);
                if (await TryCreatePartitionAsync(sql, table.TableName, target, ct))
                {
                    partitionsCreated++;
                }
            }
        }

        if (partitionsCreated > 0)
        {
            _logger.LogInformation("Partition management: ensured {Count} partition(s) exist", partitionsCreated);
        }
    }

    /// <summary>
    /// Executes a single partition-create DDL statement, classifying the outcome. Returns true when a
    /// partition was created. A PostgreSQL "already exists" (SqlState 42P07) is expected on every run
    /// because the create-future loop overlaps the previous run and stays at Debug; any other failure
    /// (disk-full, permissions, lock timeout) is a real problem surfaced at Warning.
    /// </summary>
    private async Task<bool> TryCreatePartitionAsync(string sql, string parentTable, DateOnly target, CancellationToken ct)
    {
        try
        {
            await _partitionRepository.ExecutePartitionDdlAsync(sql, ct);

            return true;
        }
        catch (Exception ex)
        {
            if ((ex is PostgresException pg) && (pg.SqlState == "42P07"))
            {
                _logger.LogDebug(ex, "Partition management: partition {Table} ({Date}) already exists",
                    parentTable, target);
            }
            else
            {
                _logger.LogWarning(ex, "Partition management: failed to create partition {Table} ({Date})",
                    parentTable, target);
            }

            return false;
        }
    }

    /// <summary>
    /// Drops partitions whose date range is past the configured retention plus a safety buffer.
    /// Exposed as internal for the unit tests; the production path calls it via <see cref="RunAsync"/>.
    /// </summary>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    internal async Task DropExpiredPartitionsAsync(CancellationToken ct)
    {
        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);

        // The Long window is the only class window that can be extended (by a >365-day override); it
        // also governs the simple range tables, which retain at the maximum window.
        int longWindowDays = await _partitionRepository.GetLongClassRetentionDaysAsync(ct);

        // MachineTelemetry: drop each retention class's leaves past that class's own window. Short and
        // Medium use fixed constants; only Long honors the (possibly extended) longWindowDays.
        foreach (RetentionClass retentionClass in PartitionedTableConfig.TelemetryRetentionClasses)
        {
            int windowDays = ClassWindowDays(retentionClass, longWindowDays);
            await DropExpiredLeavesAsync(today, windowDays, cursor => BuildClassPartitionName(retentionClass, cursor), ct);
        }

        // Simple range tables retain at the maximum (Long) window.
        foreach (PartitionedTableConfig.PartitionedTable table in PartitionedTableConfig.RangeTables)
        {
            await DropExpiredLeavesAsync(today, longWindowDays, cursor => BuildPartitionName(table.TableName, cursor), ct);
        }

        _logger.LogDebug("Partition management: expired partition cleanup complete");
    }

    /// <summary>
    /// Drops the daily leaves of one partition set whose day is before the retention cutoff. Each
    /// daily partition covers exactly [date, date+1), so it is safe to drop when date &lt; cutoff. The
    /// walk is bounded to the window plus a 7-day safety buffer so the DDL count stays constant per
    /// run rather than growing with deployment lifetime; non-existent partitions are skipped by the
    /// IF EXISTS clause.
    /// </summary>
    private async Task DropExpiredLeavesAsync(
        DateOnly today, int windowDays, Func<DateOnly, string> leafNameForDay, CancellationToken ct)
    {
        DateOnly cutoff = ComputeDropCutoff(today, windowDays);
        DateOnly cursor = ComputeWalkStart(cutoff, windowDays);

        while (cursor < cutoff)
        {
            string partitionName = leafNameForDay(cursor);
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

    /// <summary>
    /// The drop window, in days, of a retention class. Short and Medium are fixed constants; Long uses
    /// the resolved window, which may be extended beyond its 365-day floor by a rare override.
    /// </summary>
    internal static int ClassWindowDays(RetentionClass retentionClass, int longWindowDays)
    {
        return retentionClass switch
        {
            RetentionClass.Medium => RetentionClassPolicy.MediumWindowDays,
            RetentionClass.Long => longWindowDays,
            _ => RetentionClassPolicy.ShortWindowDays,
        };
    }

    /// <summary>
    /// The exclusive cutoff day: partitions strictly older than this are expired. Equals today minus
    /// the retention window minus the safety buffer.
    /// </summary>
    internal static DateOnly ComputeDropCutoff(DateOnly today, int windowDays)
    {
        return today.AddDays(-(windowDays + DropBufferDays));
    }

    /// <summary>
    /// The bounded start of the drop walk: the cutoff minus the window and a 7-day safety buffer,
    /// clamped to the partition origin so the walk never scans before any partition could exist.
    /// </summary>
    internal static DateOnly ComputeWalkStart(DateOnly cutoff, int windowDays)
    {
        DateOnly walkStart = cutoff.AddDays(-(windowDays + 7));

        return walkStart < PartitionOriginDate ? PartitionOriginDate : walkStart;
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

    /// <summary>
    /// The LIST parent table name for a MachineTelemetry retention class, e.g.
    /// <c>MachineTelemetry_Short</c>. Validated against <see cref="PostgresIdentifierValidator"/>
    /// before use.
    /// </summary>
    internal static string ClassParentTableName(RetentionClass retentionClass)
    {
        string parentTable = $"MachineTelemetry_{retentionClass}";
        PostgresIdentifierValidator.Validate(parentTable, nameof(retentionClass));

        return parentTable;
    }

    /// <summary>
    /// Builds the class-qualified daily leaf partition name for MachineTelemetry, e.g.
    /// <c>MachineTelemetry_Short_20260722</c>. The class parent is validated and the date is formatted
    /// via invariant culture and shape-checked before interpolation.
    /// </summary>
    internal static string BuildClassPartitionName(RetentionClass retentionClass, DateOnly date)
    {
        string parentTable = ClassParentTableName(retentionClass);
        string datePart = date.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        if (System.Text.RegularExpressions.Regex.IsMatch(datePart, @"^\d{8}$") == false)
        {
            throw new ArgumentException(
                $"Formatted date '{datePart}' is not 8 digits; refusing to interpolate.",
                nameof(date));
        }

        return $"{parentTable}_{datePart}";
    }

    /// <summary>
    /// Builds the SQL to create a daily leaf partition for a MachineTelemetry retention class under its
    /// class parent. The class parent is validated and the date bounds are formatted via invariant
    /// culture and shape-checked before interpolation.
    /// </summary>
    internal static string BuildCreateClassPartitionSql(RetentionClass retentionClass, DateOnly date)
    {
        string parentTable = ClassParentTableName(retentionClass);
        string partitionName = BuildClassPartitionName(retentionClass, date);
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
            CREATE TABLE IF NOT EXISTS "{partitionName}" PARTITION OF "{parentTable}"
            FOR VALUES FROM ('{startBound}') TO ('{endBound}')
            """;
    }
}
