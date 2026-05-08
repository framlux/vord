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
/// Returns historical CPU utilization data for a machine.
/// </summary>
public sealed class CpuHistoryEndpoint : EndpointWithoutRequest
{
    private readonly IMachineStateRepository _stateRepo;
    private readonly HistoryRequestValidator _validator;

    /// <summary>
    /// Creates a new instance of the <see cref="CpuHistoryEndpoint"/> class.
    /// </summary>
    public CpuHistoryEndpoint(
        IMachineStateRepository stateRepo,
        HistoryRequestValidator validator)
    {
        _stateRepo = stateRepo;
        _validator = validator;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/{id}/history/cpu");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long machineId = Route<long>("id");
        string? range = Query<string?>("range", isRequired: false) ?? "24h";

        HistoryRequestContext? context = await _validator.ValidateAsync(
            machineId, range, HttpContext,
            ct => Send.ForbiddenAsync(ct),
            ct => Send.NotFoundAsync(ct),
            ct);

        if (context is null)
        {
            return;
        }

        List<MachineTelemetry> rows = await _stateRepo.GetTelemetryHistoryAsync(
            context.MachineId, TelemetryTypeIds.CpuUsage, context.RangeStart, context.RangeEnd, ct);

        List<TimestampedValue> values = [];
        foreach (MachineTelemetry row in rows)
        {
            CpuUsagePayload? payload = JsonSerializer.Deserialize<CpuUsagePayload>(row.Payload, JsonDefaults.SnakeCase);
            if (payload is not null)
            {
                values.Add(new TimestampedValue
                {
                    Timestamp = row.ReceivedAt,
                    Value = payload.CpuUsagePercent
                });
            }
        }

        AggregatedSeries series = TelemetryAggregator.Aggregate(values, context.RangeStart, context.RangeEnd);

        HistoryResponseDto response = new()
        {
            Points = series.Points.Select(p => new HistoryPointDto
            {
                Timestamp = p.Timestamp,
                Value = p.Value
            }).ToList(),
            Stats = new HistoryStatsDto
            {
                Min = series.Stats.Min,
                Avg = series.Stats.Avg,
                Max = series.Stats.Max,
                P95 = series.Stats.P95
            },
            BucketSeconds = series.BucketSeconds,
            RawPointCount = series.RawPointCount
        };

        await Send.OkAsync(ApiResponse<HistoryResponseDto>.Ok(response), cancellation: ct);
    }
}
