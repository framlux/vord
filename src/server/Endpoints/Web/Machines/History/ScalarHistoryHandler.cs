// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.History;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.History;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines.History;

/// <summary>
/// Shared validate-fetch-aggregate flow used by the single-value scalar history endpoints
/// (CPU, memory). Each caller supplies the telemetry type to fetch and a selector that pulls
/// the scalar value out of its own payload type.
/// </summary>
internal static class ScalarHistoryHandler
{
    /// <summary>
    /// Validates the request, fetches telemetry rows for the given type, deserializes and
    /// selects the scalar value from each row's payload, and aggregates the resulting series.
    /// Returns <c>null</c> if validation fails; the validator has already written the error
    /// response in that case, and the caller should return immediately without sending anything
    /// further. On success, the caller is responsible for sending the returned DTO.
    /// </summary>
    /// <typeparam name="TPayload">The telemetry payload type to deserialize each row into.</typeparam>
    /// <param name="machineId">The machine ID from the route.</param>
    /// <param name="range">The time range query parameter.</param>
    /// <param name="telemetryType">The telemetry type ID to fetch history for.</param>
    /// <param name="select">Selects the scalar value to chart from a deserialized payload.</param>
    /// <param name="validator">The shared history request validator.</param>
    /// <param name="stateRepo">The repository used to fetch telemetry history rows.</param>
    /// <param name="httpContext">The current HTTP context for claims and response writing.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The aggregated history response, or <c>null</c> if validation failed.</returns>
    public static async Task<HistoryResponseDto?> HandleScalarHistoryAsync<TPayload>(
        long machineId,
        string? range,
        short telemetryType,
        Func<TPayload, double> select,
        HistoryRequestValidator validator,
        IMachineStateRepository stateRepo,
        HttpContext httpContext,
        CancellationToken ct)
    {
        HistoryRequestContext? context = await validator.ValidateAsync(machineId, range, httpContext, ct);

        if (context is null)
        {
            return null;
        }

        List<MachineTelemetry> rows = await stateRepo.GetTelemetryHistoryAsync(
            context.MachineId, telemetryType, context.RangeStart, context.RangeEnd, ct);

        List<TimestampedValue> values = [];
        foreach (MachineTelemetry row in rows)
        {
            TPayload? payload = JsonSerializer.Deserialize<TPayload>(row.Payload, JsonDefaults.SnakeCase);
            if (payload is not null)
            {
                values.Add(new TimestampedValue
                {
                    Timestamp = row.ReceivedAt,
                    Value = select(payload)
                });
            }
        }

        AggregatedSeries series = TelemetryAggregator.Aggregate(values, context.RangeStart, context.RangeEnd);

        return new HistoryResponseDto
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
    }
}
