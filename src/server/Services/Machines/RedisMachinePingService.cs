// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using LinqToDB.Data;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Redis-backed implementation of <see cref="IMachinePingService"/> using sorted sets.
/// Also updates MachineState.LastPingAt for SQL-level online/offline filtering.
/// </summary>
public sealed class RedisMachinePingService : IMachinePingService
{
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(7);

    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISqlDialect _dialect;
    private readonly ILogger<RedisMachinePingService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisMachinePingService"/> class.
    /// </summary>
    public RedisMachinePingService(
        IConnectionMultiplexer redis,
        IServiceScopeFactory scopeFactory,
        ISqlDialect dialect,
        ILogger<RedisMachinePingService> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task RecordPingAsync(long machineId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            IDatabase db = _redis.GetDatabase();
            string key = GetKey(machineId);
            double nowMs = now.ToUnixTimeMilliseconds();

            await db.SortedSetAddAsync(key, nowMs.ToString(), nowMs);

            double cutoffMs = now.Subtract(RetentionWindow).ToUnixTimeMilliseconds();
            await db.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, cutoffMs);
        }, logger: _logger, operationName: "RecordPing");

        // Also update MachineState.LastPingAt for SQL-level online/offline filtering.
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
            await db.ExecuteAsync(
                _dialect.UpdateLastPing,
                CancellationToken.None,
                new DataParameter("machineId", machineId),
                new DataParameter("ts", now));
        }
        catch (Exception ex)
        {
            // Best effort — Redis is the authoritative ping store, MachineState is for query optimization.
            _logger.LogWarning(ex, "Failed to update MachineState.LastPingAt for machine {MachineId}", machineId);
        }
    }

    /// <inheritdoc/>
    public async Task<DateTimeOffset?> GetLastPingAsync(long machineId)
    {
        IDatabase db = _redis.GetDatabase();
        string key = GetKey(machineId);

        SortedSetEntry[] entries = await db.SortedSetRangeByScoreWithScoresAsync(
            key,
            order: Order.Descending,
            take: 1);

        if (entries.Length == 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)entries[0].Score);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<DateTimeOffset>> GetPingHistoryAsync(long machineId, TimeSpan window)
    {
        IDatabase db = _redis.GetDatabase();
        string key = GetKey(machineId);
        double startMs = DateTimeOffset.UtcNow.Subtract(window).ToUnixTimeMilliseconds();
        double endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        SortedSetEntry[] entries = await db.SortedSetRangeByScoreWithScoresAsync(
            key,
            start: startMs,
            stop: endMs,
            order: Order.Ascending);

        return entries.Select(e => DateTimeOffset.FromUnixTimeMilliseconds((long)e.Score));
    }

    /// <inheritdoc/>
    public async Task<bool> IsOnlineAsync(long machineId, TimeSpan threshold)
    {
        DateTimeOffset? lastPing = await GetLastPingAsync(machineId);
        if (lastPing is null)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - lastPing.Value <= threshold;
    }

    /// <inheritdoc/>
    public async Task<Dictionary<long, bool>> AreOnlineAsync(IEnumerable<long> machineIds, TimeSpan threshold)
    {
        Dictionary<long, DateTimeOffset?> lastPings = await GetLastPingsAsync(machineIds);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Dictionary<long, bool> result = new(lastPings.Count);
        foreach (KeyValuePair<long, DateTimeOffset?> kvp in lastPings)
        {
            result[kvp.Key] = kvp.Value.HasValue && now - kvp.Value.Value <= threshold;
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<Dictionary<long, DateTimeOffset?>> GetLastPingsAsync(IEnumerable<long> machineIds)
    {
        IDatabase db = _redis.GetDatabase();
        IBatch batch = db.CreateBatch();

        List<(long Id, Task<SortedSetEntry[]> Task)> pending = [];
        foreach (long machineId in machineIds)
        {
            string key = GetKey(machineId);
            Task<SortedSetEntry[]> task = batch.SortedSetRangeByScoreWithScoresAsync(
                key,
                order: Order.Descending,
                take: 1);
            pending.Add((machineId, task));
        }

        batch.Execute();
        await Task.WhenAll(pending.Select(p => p.Task));

        Dictionary<long, DateTimeOffset?> result = new(pending.Count);
        foreach ((long id, Task<SortedSetEntry[]> task) in pending)
        {
            SortedSetEntry[] entries = task.Result;
            result[id] = entries.Length > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)entries[0].Score)
                : null;
        }

        return result;
    }

    private static string GetKey(long machineId)
    {
        return $"machine:ping:{machineId}";
    }
}
