// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Grpc.AgentTelemetry;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Telemetry;
using Grpc.Core;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly;
using Polly.Timeout;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Endpoints.Grpc;

/// <summary>
/// gRPC service for receiving batched telemetry from agents.
/// </summary>
[Authorize(ApiKeyAuthenticationHandler.SchemeName)]
public sealed class TelemetryService : Telemetry.TelemetryBase
{

    /// <summary>
    /// Meter name for telemetry-ingest instruments. Subscribe to this name from an OpenTelemetry /
    /// metrics listener to observe agent clock-skew and other ingest signals.
    /// </summary>
    public const string MeterName = "Framlux.FleetManagement.Server.Telemetry";

    /// <summary>
    /// Meter that owns telemetry-ingest instruments. Static so the instrument is shared across all
    /// per-request service instances the gRPC framework constructs.
    /// </summary>
    private static readonly Meter TelemetryMeter = new(MeterName);

    /// <summary>
    /// Records the magnitude of agent clock skew for envelopes whose skew exceeded <see cref="MaxClockSkew"/>.
    /// The telemetry is still ingested; this instrument makes drifted agent clocks observable. Skew magnitude
    /// is captured as a measurement value rather than a tag to keep the metric bounded in cardinality.
    /// </summary>
    private static readonly Histogram<double> ClockSkewHistogram = TelemetryMeter.CreateHistogram<double>(
        "telemetry.agent.clock_skew_seconds",
        unit: "s",
        description: "Agent clock skew magnitude in seconds for envelopes exceeding the skew threshold.");

    /// <summary>
    /// PostgreSQL error code for unique constraint violation.
    /// </summary>
    private const string PostgresUniqueViolation = "23505";

    /// <summary>
    /// Maximum number of items allowed per telemetry envelope.
    /// </summary>
    private const int MaxItemsPerEnvelope = 500;

    /// <summary>
    /// Redis key prefix for per-machine concurrent-stream tracking.
    /// </summary>
    private const string StreamCountKeyPrefix = "telemetry:stream:";

    /// <summary>
    /// Days of future daily partitions the partition job pre-creates. Mirrors
    /// PartitionManagementJob.DaysAhead; the dedup timestamp clamp must not exceed it so derived
    /// timestamps always land in an existing partition.
    /// </summary>
    private const int PartitionJobDaysAhead = 7;

    /// <summary>
    /// Maximum days into the past the dedup timestamp may be clamped to. Partition drop uses the
    /// maximum tenant retention plus a safety buffer, so the oldest retained partition is always
    /// older than this floor; a clamped timestamp therefore always lands in a partition that still exists.
    /// </summary>
    private const int MaxPartitionLookbackDays = 7;

    /// <summary>
    /// Threshold above which an agent's clock skew is logged and measured. The skew no longer
    /// rejects telemetry — the server stamps its own authoritative receipt time and derives the
    /// dedup/partition timestamp within its own partition window — but skew beyond this is recorded
    /// for observability because it indicates a drifted real-time clock on the agent.
    /// </summary>
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITelemetryDeduplicationService _dedupService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ResiliencePipeline _dbPipeline;
    private readonly IConnectionMultiplexer _redis;
    private readonly TelemetryOptions _options;
    private readonly ProcessStreamSlotLimiter _processSlotLimiter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TelemetryService> _logger;

    /// <summary>
    /// Maximum duration for a single telemetry stream (from <see cref="TelemetryOptions"/>).
    /// </summary>
    private TimeSpan MaxStreamDuration => TimeSpan.FromMinutes(_options.MaxStreamDurationMinutes);

    /// <summary>
    /// Maximum envelopes per stream (from <see cref="TelemetryOptions"/>).
    /// </summary>
    private int MaxEnvelopesPerStream => _options.MaxEnvelopesPerStream;

    /// <summary>
    /// Subscription recheck interval (from <see cref="TelemetryOptions"/>).
    /// </summary>
    private TimeSpan SubscriptionRecheckInterval => TimeSpan.FromSeconds(_options.SubscriptionRecheckIntervalSeconds);

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryService"/> class.
    /// </summary>
    public TelemetryService(
        IServiceScopeFactory scopeFactory,
        ITelemetryDeduplicationService dedupService,
        ISubscriptionService subscriptionService,
        IBackgroundJobClient backgroundJobs,
        ResiliencePipeline dbPipeline,
        IConnectionMultiplexer redis,
        IOptions<TelemetryOptions> options,
        ProcessStreamSlotLimiter processSlotLimiter,
        TimeProvider timeProvider,
        ILogger<TelemetryService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(dedupService);
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(backgroundJobs);
        ArgumentNullException.ThrowIfNull(dbPipeline);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processSlotLimiter);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _dedupService = dedupService;
        _subscriptionService = subscriptionService;
        _backgroundJobs = backgroundJobs;
        _dbPipeline = dbPipeline;
        _redis = redis;
        _options = options.Value;
        _processSlotLimiter = processSlotLimiter;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Handles bidirectional streaming telemetry — receives envelopes, processes, and sends acks.
    /// </summary>
    public override async Task StreamTelemetry(
        IAsyncStreamReader<TelemetryEnvelope> requestStream,
        IServerStreamWriter<TelemetryAck> responseStream,
        ServerCallContext context)
    {
        long machineId = ExtractMachineId(context);
        if (machineId <= 0)
        {
            StatusCode code = machineId == -1 ? StatusCode.PermissionDenied : StatusCode.Unauthenticated;
            string message = machineId == -1 ? "Machine ID mismatch between API key and header" : "Could not determine machine identity";
            context.Status = new Status(code, message);

            return;
        }

        int tenantId = ExtractTenantId(context);

        if (await IsSubscriptionActiveAsync(context, context.CancellationToken) == false)
        {
            context.Status = new Status(StatusCode.PermissionDenied, "Tenant subscription is not active");

            return;
        }

        // Cap concurrent streams per machine. A misbehaving agent (or a malicious holder of
        // a stolen API key) cannot pin many simultaneous streams against the server.
        TimeSpan slotTtl = MaxStreamDuration + TimeSpan.FromSeconds(60);
        StreamSlotSource? slotSource = await TryAcquireStreamSlotAsync(machineId, slotTtl);
        if (slotSource is null)
        {
            context.Status = new Status(StatusCode.ResourceExhausted,
                $"Machine {machineId} has reached the concurrent-stream limit");
            _logger.LogWarning(
                "Telemetry stream refused for machine {MachineId}: concurrent-stream limit ({Limit}) reached",
                machineId, _options.MaxConcurrentStreamsPerMachine);

            return;
        }

        _logger.LogInformation("Telemetry stream opened for machine {MachineId}", machineId);

        using CancellationTokenSource streamTimeout = new(MaxStreamDuration);
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, streamTimeout.Token);
        int envelopeCount = 0;
        // Track the last subscription-check timestamp so we re-verify periodically.
        DateTimeOffset lastSubscriptionCheck = _timeProvider.GetUtcNow();

        try
        {
            await foreach (TelemetryEnvelope envelope in requestStream.ReadAllAsync(linkedCts.Token))
            {
                // Re-check subscription state mid-stream so a tenant that lapses to PastDue
                // during a long-lived stream stops ingesting within one recheck window.
                if ((_timeProvider.GetUtcNow() - lastSubscriptionCheck) >= SubscriptionRecheckInterval)
                {
                    lastSubscriptionCheck = _timeProvider.GetUtcNow();
                    if (await IsSubscriptionActiveAsync(context, linkedCts.Token) == false)
                    {
                        _logger.LogInformation(
                            "Telemetry stream for machine {MachineId} closing — subscription no longer active",
                            machineId);

                        break;
                    }
                }

                envelopeCount++;
                if (envelopeCount > MaxEnvelopesPerStream)
                {
                    _logger.LogWarning("Stream for machine {MachineId} exceeded {MaxEnvelopes} envelope limit, closing", machineId, MaxEnvelopesPerStream);

                    break;
                }

                TelemetryAck ack = await ProcessEnvelopeAsync(machineId, tenantId, envelope, linkedCts.Token);
                await responseStream.WriteAsync(ack, linkedCts.Token);
            }
        }
        catch (OperationCanceledException) when (streamTimeout.IsCancellationRequested)
        {
            _logger.LogInformation("Stream for machine {MachineId} closed after {Duration} timeout", machineId, MaxStreamDuration);
        }
        finally
        {
            // Always release the slot — both graceful close and timeout/cancellation paths.
            await ReleaseStreamSlotAsync(machineId, slotSource.Value);
        }

        _logger.LogInformation("Telemetry stream closed for machine {MachineId} after {Count} envelopes", machineId, envelopeCount);
    }

    /// <summary>
    /// Lua script that claims a concurrent-stream slot atomically: INCR, cap-check (self-DECR and deny
    /// when over cap), then (re)set the TTL on every successful acquire. Single round-trip and
    /// all-or-nothing, so a failed call never leaves the count incremented without a TTL — the bug that
    /// could otherwise strand a machine's slot count above zero and lock it out of streaming. Returns 1
    /// when the slot is granted, 0 when the cap is reached.
    /// </summary>
    private const string AcquireStreamSlotScript = """
        local count = redis.call("INCR", KEYS[1])
        if count > tonumber(ARGV[1]) then
            redis.call("DECR", KEYS[1])
            return 0
        end
        redis.call("EXPIRE", KEYS[1], ARGV[2])
        return 1
        """;

    /// <summary>
    /// Tries to claim a concurrent-stream slot for the given machine. Returns the granting source on
    /// success or <see langword="null"/> when the per-machine cap is reached. The Redis path runs the
    /// atomic <see cref="AcquireStreamSlotScript"/>; if Redis is unavailable the call falls back to the
    /// per-process limiter. The TTL is refreshed on every successful acquire so overlapping streams
    /// cannot let the key expire mid-stream.
    /// </summary>
    internal async Task<StreamSlotSource?> TryAcquireStreamSlotAsync(long machineId, TimeSpan slotTtl)
    {
        try
        {
            IDatabase db = _redis.GetDatabase();
            string key = StreamCountKeyPrefix + machineId.ToString(CultureInfo.InvariantCulture);
            long ttlSeconds = Math.Max(1, (long)slotTtl.TotalSeconds);

            RedisResult result = await db.ScriptEvaluateAsync(
                AcquireStreamSlotScript,
                [(RedisKey)key],
                [(RedisValue)_options.MaxConcurrentStreamsPerMachine, (RedisValue)ttlSeconds]);

            return (long)result == 1 ? StreamSlotSource.Redis : null;
        }
        catch (RedisException ex)
        {
            // Redis is unavailable: fall back to a conservative per-process cap so a single replica
            // cannot accept unbounded concurrent streams while the distributed cap is unenforceable. The
            // script is all-or-nothing, so nothing was mutated in Redis and no compensation is needed.
            _logger.LogWarning(ex, "Telemetry stream-slot acquire via Redis failed for machine {MachineId}; using per-process fallback", machineId);

            return _processSlotLimiter.TryAcquire() ? StreamSlotSource.Process : null;
        }
    }

    /// <summary>
    /// Releases a previously-acquired stream slot, returning it to the limiter that granted it.
    /// A process-fallback slot is returned locally; a Redis slot is decremented and, if the count
    /// reaches zero, the key is deleted to keep Redis tidy.
    /// </summary>
    internal async Task ReleaseStreamSlotAsync(long machineId, StreamSlotSource source)
    {
        if (source == StreamSlotSource.Process)
        {
            _processSlotLimiter.Release();

            return;
        }

        try
        {
            IDatabase db = _redis.GetDatabase();
            string key = StreamCountKeyPrefix + machineId.ToString(CultureInfo.InvariantCulture);
            long count = await db.StringDecrementAsync(key);
            if (count <= 0)
            {
                await db.KeyDeleteAsync(key);
            }
        }
        catch (RedisException ex)
        {
            // Slot leak is bounded by the TTL applied at acquire time; log and continue.
            _logger.LogWarning(ex, "Telemetry stream-slot release failed for machine {MachineId}", machineId);
        }
    }

    /// <summary>
    /// Handles unary telemetry submission — single envelope in, single ack out.
    /// </summary>
    public override async Task<TelemetryAck> SubmitTelemetry(TelemetryEnvelope request, ServerCallContext context)
    {
        long machineId = ExtractMachineId(context);
        int tenantId = ExtractTenantId(context);
        if (machineId <= 0)
        {
            string message = machineId == -1 ? "Machine ID mismatch between API key and header" : "Could not determine machine identity";

            return new TelemetryAck
            {
                BatchId = request.BatchId,
                Success = false,
                ErrorMessage = message
            };
        }

        if (await IsSubscriptionActiveAsync(context, context.CancellationToken) == false)
        {
            return new TelemetryAck
            {
                BatchId = request.BatchId,
                Success = false,
                ErrorMessage = "Tenant subscription is not active"
            };
        }

        return await ProcessEnvelopeAsync(machineId, tenantId, request, context.CancellationToken);
    }

    private async Task<TelemetryAck> ProcessEnvelopeAsync(
        long machineId,
        int tenantId,
        TelemetryEnvelope envelope,
        CancellationToken ct)
    {
        List<string> acknowledgedIds = [];
        DateTimeOffset receivedAt = _timeProvider.GetUtcNow();

        // A missing agent_timestamp still indicates a broken agent — reject it. A skewed (but present)
        // timestamp is tolerated below because the server no longer trusts the agent clock for correctness.
        if (envelope.AgentTimestamp is null)
        {
            _logger.LogWarning(
                "Envelope {BatchId} from machine {MachineId} rejected: missing agent_timestamp",
                envelope.BatchId, machineId);

            return new TelemetryAck
            {
                BatchId = envelope.BatchId,
                Success = false,
                ErrorMessage = "agent_timestamp is required"
            };
        }

        DateTimeOffset agentTime = envelope.AgentTimestamp.ToDateTimeOffset();
        TimeSpan skew = (agentTime - receivedAt).Duration();
        if (skew > MaxClockSkew)
        {
            // Drifted real-time clocks are expected on the target hardware. The server stamps its own
            // authoritative receipt time and derives the dedup/partition timestamp within its own
            // partition window, so a skewed agent clock no longer threatens correctness. Record the
            // skew for observability instead of dropping the telemetry.
            _logger.LogWarning(
                "Envelope {BatchId} from machine {MachineId} has agent clock skew {Skew} exceeding {Max}; accepting and recording skew",
                envelope.BatchId, machineId, skew, MaxClockSkew);
            RecordClockSkew(skew);
        }

        if (envelope.Items.Count > MaxItemsPerEnvelope)
        {
            _logger.LogWarning("Envelope {BatchId} from machine {MachineId} contains {Count} items, exceeding limit of {Max}",
                envelope.BatchId, machineId, envelope.Items.Count, MaxItemsPerEnvelope);

            return new TelemetryAck
            {
                BatchId = envelope.BatchId,
                Success = false,
                ErrorMessage = $"Envelope exceeds maximum item count of {MaxItemsPerEnvelope}"
            };
        }

        // Event IDs this call newly marked in Redis. If the insert fails we must unmark them so the
        // agent's retry is not classified a duplicate and silently dropped. Populated after the mark.
        List<string> markedEventIds = [];

        try
        {
            // Layer 1: Redis dedup — batch check all event IDs in one round-trip.
            List<string> eventIdsToCheck = envelope.Items
                .Where(item => string.IsNullOrEmpty(item.EventId) == false)
                .Select(item => item.EventId)
                .ToList();

            Dictionary<string, bool> dedupResults = eventIdsToCheck.Count > 0
                ? await _dedupService.TryMarkSeenBatchAsync(eventIdsToCheck)
                : [];

            markedEventIds = dedupResults.Where(kv => kv.Value).Select(kv => kv.Key).ToList();

            // Build the list of new items (not duplicates).
            List<(TelemetryItem Item, short Type, string Payload)> newItems = [];
            foreach (TelemetryItem item in envelope.Items)
            {
                // If the item has an event ID and Redis says it's a duplicate, skip it.
                if (string.IsNullOrEmpty(item.EventId) == false &&
                    dedupResults.TryGetValue(item.EventId, out bool isNew) &&
                    isNew == false)
                {
                    acknowledgedIds.Add(item.EventId);

                    continue;
                }

                short telemetryType = (short)item.Type;
                string payload = SerializePayload(item);
                newItems.Add((item, telemetryType, payload));
            }

            if (newItems.Count > 0)
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                IMachineStateRepository machineStateRepo = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

                // Bulk insert all new telemetry rows.
                List<MachineTelemetry> rows = newItems.Select(n => new MachineTelemetry
                {
                    MachineId = machineId,
                    TenantId = tenantId,
                    TelemetryType = n.Type,
                    Payload = n.Payload,
                    ReceivedAt = ResolveDedupTimestamp(n.Item, receivedAt),
                    ServerReceivedAt = receivedAt,
                    SourceEventId = n.Item.EventId,
                }).ToList();

                try
                {
                    await _dbPipeline.ExecuteAsync(async token =>
                    {
                        await machineStateRepo.BulkInsertTelemetryAsync(rows, token);
                    }, ct);
                }
                catch (Polly.CircuitBreaker.BrokenCircuitException)
                {
                    _logger.LogWarning("Circuit breaker open for telemetry writes, signaling backpressure to machine {MachineId}", machineId);

                    // Compensate before NACKing so the retry (which arrives within the dedup TTL) is not
                    // classified a duplicate and dropped. Unmark must complete before the NACK is returned.
                    await _dedupService.UnmarkSeenBatchAsync(markedEventIds);

                    return new TelemetryAck
                    {
                        BatchId = envelope.BatchId,
                        AcknowledgedEventIds = { acknowledgedIds },
                        Success = false,
                        ErrorMessage = "Service temporarily unavailable, please retry later"
                    };
                }
                catch (TimeoutRejectedException)
                {
                    _logger.LogWarning("Telemetry write timed out for machine {MachineId} batch {BatchId}", machineId, envelope.BatchId);

                    await _dedupService.UnmarkSeenBatchAsync(markedEventIds);

                    return new TelemetryAck
                    {
                        BatchId = envelope.BatchId,
                        AcknowledgedEventIds = { acknowledgedIds },
                        Success = false,
                        ErrorMessage = "Database write timed out, please retry later"
                    };
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresUniqueViolation)
                {
                    // Layer 2 safety net: a rare crash-and-retry hit the unique index.
                    // Fall back to individual inserts, skipping duplicates.
                    _logger.LogDebug("Bulk insert hit unique constraint for machine {MachineId}, falling back to individual inserts", machineId);
                    foreach (MachineTelemetry row in rows)
                    {
                        try
                        {
                            await machineStateRepo.InsertTelemetryAsync(row, ct);
                        }
                        catch (PostgresException innerEx) when (innerEx.SqlState == PostgresUniqueViolation)
                        {
                            _logger.LogDebug("Skipping duplicate telemetry event {EventId}", row.SourceEventId);
                        }
                    }
                }

                foreach ((TelemetryItem item, short _, string _) in newItems)
                {
                    acknowledgedIds.Add(item.EventId);
                }

                // Enqueue SSH alert evaluation out of band so the ack is not blocked on per-item
                // alert evaluation; each SSH item becomes its own independently-retryable job.
                EnqueueSshAlertEvaluations(tenantId, machineId, newItems);
            }

            _logger.LogDebug("Processed {Count} telemetry items for machine {MachineId} batch {BatchId}",
                envelope.Items.Count, machineId, envelope.BatchId);

            return new TelemetryAck
            {
                BatchId = envelope.BatchId,
                AcknowledgedEventIds = { acknowledgedIds },
                Success = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing telemetry batch {BatchId} for machine {MachineId}",
                envelope.BatchId, machineId);

            // A failure anywhere between marking and a successful insert is a retryable NACK, so unmark
            // the event IDs we marked (best-effort) before returning so the retry is reprocessed.
            await _dedupService.UnmarkSeenBatchAsync(markedEventIds);

            return new TelemetryAck
            {
                BatchId = envelope.BatchId,
                AcknowledgedEventIds = { acknowledgedIds },
                Success = false,
                ErrorMessage = "Internal server error"
            };
        }
    }

    /// <summary>
    /// Resolves the partition-key timestamp used to deduplicate a telemetry item. For items with a
    /// source event id the value is derived from the item's immutable collected_at so a re-delivery
    /// produces the same (SourceEventId, ReceivedAt) tuple and collides on the unique index. The
    /// value is clamped to the window of maintained daily partitions around server time so a clock-
    /// skewed agent cannot route rows outside the existing partition range. Items without a source
    /// event id are not deduplicated and keep the server receipt time.
    /// </summary>
    internal static DateTimeOffset ResolveDedupTimestamp(TelemetryItem item, DateTimeOffset serverReceivedAt)
    {
        if (string.IsNullOrEmpty(item.EventId) || (item.CollectedAt is null))
        {
            return serverReceivedAt;
        }

        DateTimeOffset collected = item.CollectedAt.ToDateTimeOffset();
        DateTimeOffset lowerBound = serverReceivedAt.AddDays(-MaxPartitionLookbackDays);
        DateTimeOffset upperBound = serverReceivedAt.AddDays(PartitionJobDaysAhead);

        if (collected < lowerBound)
        {
            return lowerBound;
        }

        if (collected > upperBound)
        {
            return upperBound;
        }

        return collected;
    }

    /// <summary>
    /// Records an observed agent clock skew that exceeded <see cref="MaxClockSkew"/>. Captures the skew
    /// magnitude in the ingest skew histogram so drifted agent clocks are visible to metrics listeners
    /// without dropping the telemetry that produced the skew. Per-machine detail is preserved in the
    /// surrounding warning log; the metric carries no machine dimension to keep cardinality bounded.
    /// </summary>
    private static void RecordClockSkew(TimeSpan skew)
    {
        ClockSkewHistogram.Record(skew.TotalSeconds);
    }

    private void EnqueueSshAlertEvaluations(
        int tenantId,
        long machineId,
        List<(TelemetryItem Item, short Type, string Payload)> items)
    {
        foreach ((TelemetryItem item, short _, string _) in items)
        {
            if (item.PayloadCase != TelemetryItem.PayloadOneofCase.SshSession)
            {
                continue;
            }

            SshSessionRecord ssh = item.SshSession;
            _backgroundJobs.Enqueue<SshAlertEvaluationJob>(
                job => job.RunAsync(tenantId, machineId, ssh.Action, ssh.User, ssh.SourceIp, ssh.SourcePort, ssh.AuthMethod, CancellationToken.None));
        }
    }

    private static string SerializePayload(TelemetryItem item)
    {
        object? payload = item.PayloadCase switch
        {
            TelemetryItem.PayloadOneofCase.SystemInfo => item.SystemInfo,
            TelemetryItem.PayloadOneofCase.OsVersion => item.OsVersion,
            TelemetryItem.PayloadOneofCase.CpuInfo => item.CpuInfo,
            TelemetryItem.PayloadOneofCase.MemoryInfo => item.MemoryInfo,
            TelemetryItem.PayloadOneofCase.DiskInfo => item.DiskInfo,
            TelemetryItem.PayloadOneofCase.CpuUtilization => item.CpuUtilization,
            TelemetryItem.PayloadOneofCase.MemoryUtilization => item.MemoryUtilization,
            TelemetryItem.PayloadOneofCase.DiskUtilization => item.DiskUtilization,
            TelemetryItem.PayloadOneofCase.SshSession => item.SshSession,
            TelemetryItem.PayloadOneofCase.HardwareHealth => item.HardwareHealth,
            TelemetryItem.PayloadOneofCase.PackageUpdates => item.PackageUpdates,
            TelemetryItem.PayloadOneofCase.ServiceStatus => item.ServiceStatus,
            _ => null
        };

        return payload is not null
            ? JsonSerializer.Serialize(payload, JsonDefaults.SnakeCase)
            : "{}";
    }

    private async Task<bool> IsSubscriptionActiveAsync(ServerCallContext context, CancellationToken ct)
    {
        System.Security.Claims.Claim? tenantIdClaim = context.GetHttpContext().User.FindFirst("TenantId");
        if ((tenantIdClaim is null) || (int.TryParse(tenantIdClaim.Value, out int tenantId) == false))
        {
            return false;
        }

        Database.Models.TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);

        return subscription is not null && subscription.Status == Database.Enums.SubscriptionStatus.Active;
    }

    private static int ExtractTenantId(ServerCallContext context)
    {
        System.Security.Claims.Claim? tenantIdClaim = context.GetHttpContext().User.FindFirst("TenantId");
        if ((tenantIdClaim is not null) && int.TryParse(tenantIdClaim.Value, out int tenantId))
        {
            return tenantId;
        }

        return 0;
    }

    private long ExtractMachineId(ServerCallContext context)
    {
        // Primary: derive machine ID from authenticated API key claim.
        System.Security.Claims.Claim? machineIdClaim = context.GetHttpContext().User.FindFirst("MachineId");
        if ((machineIdClaim is null) || (long.TryParse(machineIdClaim.Value, out long machineId) == false))
        {
            return 0;
        }

        // Cross-validate: if x-machine-id header is also present, it must match.
        Metadata.Entry? headerEntry = context.RequestHeaders.Get("x-machine-id");
        if (headerEntry is not null && long.TryParse(headerEntry.Value, out long headerMachineId))
        {
            if (headerMachineId != machineId)
            {
                _logger.LogWarning(
                    "Machine ID mismatch: claim={ClaimMachineId}, header={HeaderMachineId}",
                    machineId, headerMachineId);

                return -1;
            }
        }

        return machineId;
    }
}
