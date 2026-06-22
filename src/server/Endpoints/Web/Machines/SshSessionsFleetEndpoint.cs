// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using Framlux.FleetManagement.Services.Core.Telemetry;
using Microsoft.Extensions.Logging;

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
    private readonly ISubscriptionService _subscriptionService;
    private readonly ILogger<SshSessionsFleetEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="SshSessionsFleetEndpoint"/> class.
    /// </summary>
    public SshSessionsFleetEndpoint(
        IMachineRepository machineRepo,
        IMachineStateRepository machineStateRepo,
        ISubscriptionService subscriptionService,
        ILogger<SshSessionsFleetEndpoint> logger)
    {
        _machineRepo = machineRepo;
        _machineStateRepo = machineStateRepo;
        _subscriptionService = subscriptionService;
        _logger = logger;
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

        // Resolve any machine-name search to a concrete machine-id set BEFORE the telemetry query
        // so the filter is a SQL predicate rather than an in-memory pass over the whole history.
        List<long> machineIds = ResolveMachineIds(machineNames, req.Search);

        PaginatedResponse<FleetSshSessionDto> emptyResponse = new()
        {
            Items = [],
            Page = page,
            PageSize = pageSize,
            TotalCount = 0,
        };

        if (machineIds.Count == 0)
        {
            await Send.OkAsync(ApiResponse<PaginatedResponse<FleetSshSessionDto>>.Ok(emptyResponse), cancellation: ct);

            return;
        }

        // Bound the query to the tenant's retention window so we never scan unbounded history.
        int retentionDays = await _subscriptionService.GetRetentionDaysForTenantAsync(tenantId.Value, ct);
        DateTimeOffset receivedSince = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        // Total count and the requested page are both computed in SQL (Skip/Take/Count) so memory
        // use is bounded by the page size rather than the size of the SSH telemetry history.
        int totalCount = await _machineStateRepo.CountTelemetryByMachineIdsAndTypeAsync(
            machineIds, TelemetryTypeIds.SshSessions, receivedSince, ct);

        List<MachineTelemetry> telemetryRows = await _machineStateRepo.GetTelemetryPageByMachineIdsAndTypeAsync(
            machineIds, TelemetryTypeIds.SshSessions, receivedSince, (page - 1) * pageSize, pageSize, ct);

        int malformedCount = 0;
        List<FleetSshSessionDto> sessions = [];

        foreach (MachineTelemetry row in telemetryRows)
        {
            string machineName = machineNames.GetValueOrDefault(row.MachineId, string.Empty);

            SshSessionPayload? session;
            try
            {
                session = JsonSerializer.Deserialize<SshSessionPayload>(row.Payload, JsonDefaults.SnakeCase);
            }
            catch (JsonException ex)
            {
                malformedCount++;
                _logger.LogWarning(ex, "Skipping malformed SSH session telemetry row {RowId} for machine {MachineId}", row.Id, row.MachineId);

                continue;
            }

            if (session is null)
            {
                continue;
            }

            sessions.Add(new FleetSshSessionDto
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

        if (malformedCount > 0)
        {
            _logger.LogWarning("Skipped {MalformedCount} malformed SSH session telemetry rows for tenant {TenantId}", malformedCount, tenantId.Value);
        }

        PaginatedResponse<FleetSshSessionDto> response = new()
        {
            Items = sessions,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
        };

        await Send.OkAsync(ApiResponse<PaginatedResponse<FleetSshSessionDto>>.Ok(response), cancellation: ct);
    }

    /// <summary>
    /// Resolves the set of machine IDs to query. When a search term is supplied, the result is
    /// limited to tenant machines whose name contains the term (case-insensitive); otherwise all
    /// tenant machine IDs are returned. Extracted as an <c>internal static</c> method so the
    /// search-to-id-set resolution can be unit-tested without the endpoint pipeline.
    /// </summary>
    /// <param name="machineNames">Map of tenant machine ID to machine name.</param>
    /// <param name="search">Optional machine-name search term.</param>
    /// <returns>The machine IDs to include in the telemetry query.</returns>
    internal static List<long> ResolveMachineIds(Dictionary<long, string> machineNames, string? search)
    {
        ArgumentNullException.ThrowIfNull(machineNames);

        if (string.IsNullOrWhiteSpace(search))
        {
            return machineNames.Keys.ToList();
        }

        string term = search.Trim();

        return machineNames
            .Where(kvp => kvp.Value.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();
    }
}
