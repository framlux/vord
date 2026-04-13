// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Grpc.AgentTelemetry;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.Telemetry;
using Grpc.Core;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

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
    /// Maximum number of envelopes allowed per stream.
    /// </summary>
    private const int MaxEnvelopesPerStream = 1000;

    /// <summary>
    /// Maximum duration for a single telemetry stream.
    /// </summary>
    private static readonly TimeSpan MaxStreamDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum allowed clock skew between the agent's timestamp and server time.
    /// Envelopes with an agent_timestamp outside this window are rejected — the
    /// agent's clock is too far off to produce meaningful telemetry.
    /// </summary>
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITelemetryDeduplicationService _dedupService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ILogger<TelemetryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryService"/> class.
    /// </summary>
    public TelemetryService(
        IServiceScopeFactory scopeFactory,
        ITelemetryDeduplicationService dedupService,
        ISubscriptionService subscriptionService,
        ILogger<TelemetryService> logger)
    {
        _scopeFactory = scopeFactory;
        _dedupService = dedupService;
        _subscriptionService = subscriptionService;
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

        _logger.LogInformation("Telemetry stream opened for machine {MachineId}", machineId);

        using CancellationTokenSource streamTimeout = new(MaxStreamDuration);
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, streamTimeout.Token);
        int envelopeCount = 0;

        try
        {
            await foreach (TelemetryEnvelope envelope in requestStream.ReadAllAsync(linkedCts.Token))
            {
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

        _logger.LogInformation("Telemetry stream closed for machine {MachineId} after {Count} envelopes", machineId, envelopeCount);
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
                DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

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
                    await db.BulkCopyAsync(new BulkCopyOptions { BulkCopyType = BulkCopyType.MultipleRows }, rows, ct);
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
                            await db.InsertAsync(row, token: ct);
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
