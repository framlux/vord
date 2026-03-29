// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using FastEndpoints;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;
using LinqToDB;
using LinqToDB.Async;

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
    private readonly DatabaseContext _db;

    /// <summary>
    /// Creates a new instance of the <see cref="SshSessionsFleetEndpoint"/> class.
    /// </summary>
    public SshSessionsFleetEndpoint(DatabaseContext db)
    {
        _db = db;
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

        // Get machines with SSH session data
        List<MachineSessionRow> machinesWithSessions = await (
            from m in _db.Machines
            join s in _db.MachineStates on m.Id equals s.MachineId
            where m.TenantId == tenantId.Value &&
                  m.IsDeleted == false &&
                  s.SshSessions != null
            select new MachineSessionRow { Id = m.Id, Name = m.Name, SshSessions = s.SshSessions }
        ).ToListAsync(ct);

        JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };

        List<FleetSshSessionDto> allSessions = [];

        foreach (MachineSessionRow machine in machinesWithSessions)
        {
            if (string.IsNullOrEmpty(machine.SshSessions))
            {
                continue;
            }

            try
            {
                List<SshSessionPayload>? sessions = JsonSerializer.Deserialize<List<SshSessionPayload>>(machine.SshSessions, jsonOptions);
                if (sessions is null)
                {
                    continue;
                }

                foreach (SshSessionPayload session in sessions)
                {
                    allSessions.Add(new FleetSshSessionDto
                    {
                        MachineId = machine.Id,
                        MachineName = machine.Name,
                        User = session.User,
                        SourceIp = session.SourceIp,
                        Action = session.Action,
                        AuthMethod = session.AuthMethod,
                        Timestamp = session.Timestamp,
                    });
                }
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

/// <summary>
/// Lightweight projection for machine SSH session queries.
/// </summary>
file sealed class MachineSessionRow
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? SshSessions { get; init; }
}
