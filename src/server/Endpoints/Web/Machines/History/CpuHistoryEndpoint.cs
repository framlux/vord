// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Models.History;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using Framlux.FleetManagement.Services.Core.Telemetry;

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

        HistoryResponseDto? response = await ScalarHistoryHandler.HandleScalarHistoryAsync<CpuUsagePayload>(
            machineId, range, TelemetryTypeIds.CpuUsage, payload => payload.CpuUsagePercent,
            _validator, _stateRepo, HttpContext, ct);

        if (response is null)
        {
            return;
        }

        await Send.OkAsync(ApiResponse<HistoryResponseDto>.Ok(response), cancellation: ct);
    }
}
