// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using Framlux.FleetManagement.Services.Core.Telemetry;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// DTO for a fleet-wide SSH session entry with machine context.
/// </summary>
public sealed class FleetSshSessionDto
{
    /// <summary>Machine ID.</summary>
    public long MachineId { get; set; }

    /// <summary>Machine name.</summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>Username.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Source IP address.</summary>
    public string SourceIp { get; set; } = string.Empty;

    /// <summary>Action (connect/disconnect/failed).</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Authentication method.</summary>
    public string AuthMethod { get; set; } = string.Empty;

    /// <summary>Event timestamp.</summary>
    public string Timestamp { get; set; } = string.Empty;
}

/// <summary>
/// Request model for listing fleet-wide SSH sessions.
/// </summary>
public sealed class FleetSshSessionsRequest
{
    /// <summary>Page number (1-based).</summary>
    [QueryParam]
    public int Page { get; set; } = 1;

    /// <summary>Items per page.</summary>
    [QueryParam]
    public int PageSize { get; set; } = 50;

    /// <summary>Search by machine name or user.</summary>
    [QueryParam]
    public string? Search { get; set; }
}

/// <summary>
/// Returns paginated SSH sessions aggregated across all tenant machines.
/// Sorted by timestamp descending.
/// </summary>
public sealed class SshSessionsFleetEndpoint : Endpoint<FleetSshSessionsRequest, ApiResponse<PaginatedResponse<FleetSshSessionDto>>>
{
    private readonly IMachineRepository _machineRepo;
    private readonly IMachineStateRepository _machineStateRepo;

    /// <summary>
    /// Creates a new instance of the <see cref="SshSessionsFleetEndpoint"/> class.
    /// </summary>
    public SshSessionsFleetEndpoint(IMachineRepository machineRepo, IMachineStateRepository machineStateRepo)
    {
        _machineRepo = machineRepo;
        _machineStateRepo = machineStateRepo;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/ssh-sessions");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(FleetSshSessionsRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<PaginatedResponse<FleetSshSessionDto>>.Error("Unauthorized"), ct);

            return;
        }

        int page = req.Page < 1 ? 1 : req.Page;
        int pageSize = (req.PageSize < 1) || (req.PageSize > 100) ? 50 : req.PageSize;

        // Build a lookup of machine names for tenant machines.
        Dictionary<long, string> machineNames = await _machineRepo.GetMachineNameMapForTenantAsync(tenantId.Value, ct);

        if (machineNames.Count == 0)
        {
            await Send.OkAsync(ApiResponse<PaginatedResponse<FleetSshSessionDto>>.Ok(new PaginatedResponse<FleetSshSessionDto>
            {
                Items = [],
                Page = page,
                PageSize = pageSize,
                TotalCount = 0,
            }), cancellation: ct);

            return;
        }

        // Query SSH session telemetry rows for all tenant machines.
        List<long> machineIds = machineNames.Keys.ToList();
        List<MachineTelemetry> telemetryRows = await _machineStateRepo.GetTelemetryByMachineIdsAndTypeAsync(
            machineIds, TelemetryTypeIds.SshSessions, ct);

        JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };

        List<FleetSshSessionDto> allSessions = [];

        foreach (MachineTelemetry row in telemetryRows)
        {
            string machineName = machineNames.GetValueOrDefault(row.MachineId, string.Empty);

            try
            {
                SshSessionPayload? session = JsonSerializer.Deserialize<SshSessionPayload>(row.Payload, jsonOptions);
                if (session is null)
                {
                    continue;
                }

                allSessions.Add(new FleetSshSessionDto
                {
                    MachineId = row.MachineId,
                    MachineName = machineName,
                    User = session.User,
                    SourceIp = session.SourceIp,
                    Action = session.Action,
                    AuthMethod = session.AuthMethod,
                    Timestamp = session.Timestamp,
                });
            }
            catch
            {
                // Skip malformed JSON
            }
        }

        // Apply search filter
        if (string.IsNullOrEmpty(req.Search) == false)
        {
            string search = req.Search.ToLowerInvariant();
            allSessions = allSessions
                .Where(s => s.MachineName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            s.User.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Sort by timestamp descending
        allSessions = allSessions
            .OrderByDescending(s => s.Timestamp)
            .ToList();

        int totalCount = allSessions.Count;

        List<FleetSshSessionDto> paged = allSessions
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        PaginatedResponse<FleetSshSessionDto> response = new()
        {
            Items = paged,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
        };

        await Send.OkAsync(ApiResponse<PaginatedResponse<FleetSshSessionDto>>.Ok(response), cancellation: ct);
    }
}
