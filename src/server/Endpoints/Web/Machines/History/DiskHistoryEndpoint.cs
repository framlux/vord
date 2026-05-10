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
/// Returns historical disk utilization data for a machine, with one series per mount point.
/// </summary>
public sealed class DiskHistoryEndpoint : EndpointWithoutRequest
{
    private readonly IMachineStateRepository _stateRepo;
    private readonly HistoryRequestValidator _validator;

    /// <summary>
    /// Creates a new instance of the <see cref="DiskHistoryEndpoint"/> class.
    /// </summary>
    public DiskHistoryEndpoint(
        IMachineStateRepository stateRepo,
        HistoryRequestValidator validator)
    {
        _stateRepo = stateRepo;
        _validator = validator;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/{id}/history/disk");
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
            context.MachineId, TelemetryTypeIds.DiskUsage, context.RangeStart, context.RangeEnd, ct);

        // Group values by device path for multi-series output
        Dictionary<string, List<(DateTimeOffset Timestamp, int UsagePercent, string MountPoint)>> deviceData = new();
        int rawPointCount = 0;

        foreach (MachineTelemetry row in rows)
        {
            DiskUsagePayload? payload = JsonSerializer.Deserialize<DiskUsagePayload>(row.Payload, JsonDefaults.SnakeCase);
            if (payload is null)
            {
                continue;
            }

            foreach (DiskUsageEntryDto disk in payload.Disks)
            {
                if (deviceData.ContainsKey(disk.Device) == false)
                {
                    deviceData[disk.Device] = [];
                }

                deviceData[disk.Device].Add((row.ReceivedAt, disk.UsagePercent, disk.Path));
                rawPointCount++;
            }
        }

        List<DiskSeriesDto> series = [];
        int bucketSeconds = 0;

        foreach (KeyValuePair<string, List<(DateTimeOffset Timestamp, int UsagePercent, string MountPoint)>> kvp in deviceData)
        {
            List<TimestampedValue> values = kvp.Value
                .Select(d => new TimestampedValue { Timestamp = d.Timestamp, Value = d.UsagePercent })
                .ToList();

            AggregatedSeries aggregated = TelemetryAggregator.Aggregate(values, context.RangeStart, context.RangeEnd);
            bucketSeconds = aggregated.BucketSeconds;

            string mountPoint = kvp.Value.Count > 0 ? kvp.Value[0].MountPoint : "";

            series.Add(new DiskSeriesDto
            {
                Device = kvp.Key,
                MountPoint = mountPoint,
                Points = aggregated.Points.Select(p => new HistoryPointDto
                {
                    Timestamp = p.Timestamp,
                    Value = p.Value
                }).ToList(),
                Stats = new HistoryStatsDto
                {
                    Min = aggregated.Stats.Min,
                    Avg = aggregated.Stats.Avg,
                    Max = aggregated.Stats.Max,
                    P95 = aggregated.Stats.P95
                }
            });
        }

        DiskHistoryResponseDto response = new()
        {
            Series = series,
            BucketSeconds = bucketSeconds,
            RawPointCount = rawPointCount
        };

        await Send.OkAsync(ApiResponse<DiskHistoryResponseDto>.Ok(response), cancellation: ct);
    }
}
