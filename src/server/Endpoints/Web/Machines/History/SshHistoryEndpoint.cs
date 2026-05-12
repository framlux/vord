// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Globalization;
using System.Text.Json;
using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Models.History;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Telemetry;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines.History;

/// <summary>
/// Returns historical SSH session events for a machine.
/// </summary>
public sealed class SshHistoryEndpoint : EndpointWithoutRequest
{
    /// <summary>
    /// Maximum number of SSH events returned per request.
    /// </summary>
    public const int MaxEvents = 500;

    private readonly IMachineStateRepository _stateRepo;
    private readonly HistoryRequestValidator _validator;

    /// <summary>
    /// Creates a new instance of the <see cref="SshHistoryEndpoint"/> class.
    /// </summary>
    public SshHistoryEndpoint(
        IMachineStateRepository stateRepo,
        HistoryRequestValidator validator)
    {
        _stateRepo = stateRepo;
        _validator = validator;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/{id}/history/ssh");
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
            context.MachineId, TelemetryTypeIds.SshSessions, context.RangeStart, context.RangeEnd, ct);

        List<SshEventDto> events = [];
        foreach (MachineTelemetry row in rows)
        {
            SshSessionPayload? payload = JsonSerializer.Deserialize<SshSessionPayload>(row.Payload, JsonDefaults.SnakeCase);
            if (payload is null)
            {
                continue;
            }

            DateTimeOffset eventTimestamp = row.ReceivedAt;
            if (DateTimeOffset.TryParse(payload.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsedTimestamp))
            {
                eventTimestamp = parsedTimestamp;
            }

            events.Add(new SshEventDto
            {
                Timestamp = eventTimestamp,
                User = payload.User,
                SourceIp = payload.SourceIp,
                SourcePort = payload.SourcePort,
                Action = payload.Action,
                AuthMethod = payload.AuthMethod
            });
        }

        int totalEvents = events.Count;

        // Order newest first and cap at MaxEvents
        events = events
            .OrderByDescending(e => e.Timestamp)
            .Take(MaxEvents)
            .ToList();

        SshHistoryResponseDto response = new()
        {
            Events = events,
            TotalEvents = totalEvents
        };

        await Send.OkAsync(ApiResponse<SshHistoryResponseDto>.Ok(response), cancellation: ct);
    }
}
