// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.History;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;
using Framlux.FleetManagement.Server.Services.History;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Telemetry;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines.History;

/// <summary>
/// Returns historical service status data for a machine (failed/total services over time).
/// </summary>
public sealed class ServiceHistoryEndpoint : EndpointWithoutRequest
{
    private readonly IMachineStateRepository _stateRepo;
    private readonly HistoryRequestValidator _validator;

    /// <summary>
    /// Creates a new instance of the <see cref="ServiceHistoryEndpoint"/> class.
    /// </summary>
    public ServiceHistoryEndpoint(
        IMachineStateRepository stateRepo,
        HistoryRequestValidator validator)
    {
        _stateRepo = stateRepo;
        _validator = validator;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/{id}/history/services");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long machineId = Route<long>("id");
        string? range = Query<string?>("range", isRequired: false) ?? "24h";

        HistoryRequestContext? context = await _validator.ValidateAsync(
            machineId, range, HttpContext, ct);

        if (context is null)
        {
            return;
        }

        List<MachineTelemetry> rows = await _stateRepo.GetTelemetryHistoryAsync(
            context.MachineId, TelemetryTypeIds.ServiceStatus, context.RangeStart, context.RangeEnd, ct);

        // Extract failed count for aggregation, but also keep total count for the response
        List<TimestampedValue> failedValues = [];
        List<(DateTimeOffset Timestamp, int FailedCount, int TotalCount)> rawEntries = [];

        foreach (MachineTelemetry row in rows)
        {
            ServiceStatusPayload? payload = JsonSerializer.Deserialize<ServiceStatusPayload>(row.Payload, JsonDefaults.SnakeCase);
            if (payload is null)
            {
                continue;
            }

            int failedCount = payload.Services.Count(s => string.Equals(s.ActiveState, "failed", StringComparison.OrdinalIgnoreCase));
            int totalCount = payload.Services.Count;

            failedValues.Add(new TimestampedValue
            {
                Timestamp = row.ReceivedAt,
                Value = failedCount
            });

            rawEntries.Add((row.ReceivedAt, failedCount, totalCount));
        }

        AggregatedSeries aggregated = TelemetryAggregator.Aggregate(failedValues, context.RangeStart, context.RangeEnd);

        // Build response points with both failed and total counts
        List<ServiceHistoryPointDto> points = [];
        if (aggregated.BucketSeconds == 0)
        {
            // Raw mode: use actual values
            for (int i = 0; i < rawEntries.Count; i++)
            {
                points.Add(new ServiceHistoryPointDto
                {
                    Timestamp = rawEntries[i].Timestamp,
                    FailedCount = rawEntries[i].FailedCount,
                    TotalCount = rawEntries[i].TotalCount
                });
            }
        }
        else
        {
            // Bucketed mode: use aggregated failed count, take last total per bucket
            foreach (AggregatedPoint point in aggregated.Points)
            {
                int closestTotal = rawEntries
                    .Where(e => e.Timestamp <= point.Timestamp.AddSeconds(aggregated.BucketSeconds))
                    .Select(e => e.TotalCount)
                    .LastOrDefault();

                points.Add(new ServiceHistoryPointDto
                {
                    Timestamp = point.Timestamp,
                    FailedCount = (int)Math.Round(point.Value),
                    TotalCount = closestTotal
                });
            }
        }

        ServiceHistoryResponseDto response = new()
        {
            Points = points,
            Stats = new HistoryStatsDto
            {
                Min = aggregated.Stats.Min,
                Avg = aggregated.Stats.Avg,
                Max = aggregated.Stats.Max,
                P95 = aggregated.Stats.P95
            },
            BucketSeconds = aggregated.BucketSeconds,
            RawPointCount = aggregated.RawPointCount
        };

        await Send.OkAsync(ApiResponse<ServiceHistoryResponseDto>.Ok(response), cancellation: ct);
    }
}
