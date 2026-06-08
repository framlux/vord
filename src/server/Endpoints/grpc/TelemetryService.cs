// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Globalization;
using System.Text.Json;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Grpc.AgentTelemetry;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Telemetry;
using Grpc.Core;
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
    /// Maximum allowed clock skew between the agent's timestamp and server time.
    /// Envelopes with an agent_timestamp outside this window are rejected — the
    /// agent's clock is too far off to produce meaningful telemetry.
    /// </summary>
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITelemetryDeduplicationService _dedupService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IEventAlertService _eventAlertService;
    private readonly ResiliencePipeline _dbPipeline;
    private readonly IConnectionMultiplexer _redis;
    private readonly TelemetryOptions _options;
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
        IEventAlertService eventAlertService,
        ResiliencePipeline dbPipeline,
        IConnectionMultiplexer redis,
        IOptions<TelemetryOptions> options,
        ILogger<TelemetryService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(dedupService);
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(eventAlertService);
        ArgumentNullException.ThrowIfNull(dbPipeline);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _dedupService = dedupService;
        _subscriptionService = subscriptionService;
        _eventAlertService = eventAlertService;
        _dbPipeline = dbPipeline;
        _redis = redis;
        _options = options.Value;
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
        bool acquired = await TryAcquireStreamSlotAsync(machineId, slotTtl);
        if (acquired == false)
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
        DateTimeOffset lastSubscriptionCheck = DateTimeOffset.UtcNow;

        try
        {
            await foreach (TelemetryEnvelope envelope in requestStream.ReadAllAsync(linkedCts.Token))
            {
                // Re-check subscription state mid-stream so a tenant that lapses to PastDue
                // during a long-lived stream stops ingesting within one recheck window.
                if ((DateTimeOffset.UtcNow - lastSubscriptionCheck) >= SubscriptionRecheckInterval)
                {
                    lastSubscriptionCheck = DateTimeOffset.UtcNow;
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
            await ReleaseStreamSlotAsync(machineId);
        }

        _logger.LogInformation("Telemetry stream closed for machine {MachineId} after {Count} envelopes", machineId, envelopeCount);
    }

    /// <summary>
    /// Tries to claim a concurrent-stream slot for the given machine. Returns
    /// <see langword="true"/> on success; <see langword="false"/> if the cap is reached.
    /// Slot key is <c>telemetry:stream:{machineId}</c>; INCR returns the post-increment value,
    /// so the very first slot for a machine sees count==1 and we set the TTL accordingly.
    /// Subsequent slots over the cap immediately DECR and return false so the count stays bounded.
    /// </summary>
    internal async Task<bool> TryAcquireStreamSlotAsync(long machineId, TimeSpan slotTtl)
    {
        try
        {
            IDatabase db = _redis.GetDatabase();
            string key = StreamCountKeyPrefix + machineId.ToString(CultureInfo.InvariantCulture);
            long count = await db.StringIncrementAsync(key);
            if (count == 1)
            {
                await db.KeyExpireAsync(key, slotTtl);
            }

            if (count > _options.MaxConcurrentStreamsPerMachine)
            {
                await db.StringDecrementAsync(key);

                return false;
            }

            return true;
        }
        catch (RedisException ex)
        {
            // Fail-open on Redis outage — telemetry ingest must keep working even if the cap
            // is unenforceable at the moment. The global rate limiter and per-stream limits
            // provide secondary protection.
            _logger.LogWarning(ex, "Telemetry stream-slot acquire failed for machine {MachineId}; allowing", machineId);

            return true;
        }
    }

    /// <summary>
    /// Releases a previously-acquired stream slot. If the count reaches zero, the key is
    /// deleted to keep Redis tidy.
    /// </summary>
    internal async Task ReleaseStreamSlotAsync(long machineId)
    {
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
        DateTimeOffset receivedAt = DateTimeOffset.UtcNow;

        // Reject envelopes without a timestamp or with clocks too far from server time.
        // A missing or skewed agent_timestamp means collected_at values are unreliable,
        // producing misleading telemetry that would pollute dashboards.
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
            _logger.LogWarning(
                "Envelope {BatchId} from machine {MachineId} rejected: agent clock skew {Skew} exceeds limit of {Max}",
                envelope.BatchId, machineId, skew, MaxClockSkew);

            return new TelemetryAck
            {
                BatchId = envelope.BatchId,
                Success = false,
                ErrorMessage = $"Agent clock skew ({skew.TotalSeconds:F0}s) exceeds maximum allowed ({MaxClockSkew.TotalSeconds:F0}s)"
            };
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
                    ReceivedAt = receivedAt,
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

                // Evaluate event-based alerts for SSH sessions after persisting telemetry
                await EvaluateSshAlertEventsAsync(tenantId, machineId, newItems, ct);
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

            return new TelemetryAck
            {
                BatchId = envelope.BatchId,
                AcknowledgedEventIds = { acknowledgedIds },
                Success = false,
                ErrorMessage = "Internal server error"
            };
        }
    }

    private async Task EvaluateSshAlertEventsAsync(
        int tenantId,
        long machineId,
        List<(TelemetryItem Item, short Type, string Payload)> items,
        CancellationToken ct)
    {
        foreach ((TelemetryItem item, short _, string _) in items)
        {
            if (item.PayloadCase != TelemetryItem.PayloadOneofCase.SshSession)
            {
                continue;
            }

            SshSessionRecord ssh = item.SshSession;

            try
            {
                if (string.Equals(ssh.Action, "connect", StringComparison.OrdinalIgnoreCase))
                {
                    await _eventAlertService.EvaluateSshConnectAsync(
                        tenantId, machineId, ssh.User, ssh.SourceIp, ssh.SourcePort, ssh.AuthMethod, ct);
                }
                else if (string.Equals(ssh.Action, "disconnect", StringComparison.OrdinalIgnoreCase))
                {
                    await _eventAlertService.ResolveSshDisconnectAsync(machineId, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to evaluate SSH alert for machine {MachineId}", machineId);
            }
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
