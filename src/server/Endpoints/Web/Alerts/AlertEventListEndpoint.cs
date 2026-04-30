// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Request model for listing alert events.
/// </summary>
public sealed class AlertEventListRequest
{
    /// <summary>Page number (1-based).</summary>
    [QueryParam]
    public int Page { get; set; } = 1;

    /// <summary>Items per page.</summary>
    [QueryParam]
    public int PageSize { get; set; } = 25;

    /// <summary>Filter by status name.</summary>
    [QueryParam]
    public string? Status { get; set; }

    /// <summary>Filter by severity name.</summary>
    [QueryParam]
    public string? Severity { get; set; }
}

/// <summary>
/// Returns paginated alert events for the current tenant.
/// Requires Pro+ subscription and ViewOnly role.
/// </summary>
public sealed class AlertEventListEndpoint : Endpoint<AlertEventListRequest, ApiResponse<PaginatedResponse<AlertEventDto>>>
{
    private readonly DatabaseContext _db;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertEventListEndpoint"/> class.
    /// </summary>
    public AlertEventListEndpoint(DatabaseContext db, ISubscriptionService subscriptionService)
    {
        _db = db;
        _subscriptionService = subscriptionService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/alert-events");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(AlertEventListRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<PaginatedResponse<AlertEventDto>>.Error("Unauthorized"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free) || (subscription.Status != SubscriptionStatus.Active))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<PaginatedResponse<AlertEventDto>>.Error("Alerting requires a Pro or Team subscription"), ct);

            return;
        }

        int page = req.Page < 1 ? 1 : req.Page;
        int pageSize = (req.PageSize < 1) || (req.PageSize > 100) ? 25 : req.PageSize;

        IQueryable<AlertEvent> query = _db.AlertEvents
            .LoadWith(e => e.AlertRule)
            .Where(e => e.TenantId == tenantId.Value);

        if ((string.IsNullOrEmpty(req.Status) == false) && Enum.TryParse<AlertEventStatus>(req.Status, true, out AlertEventStatus statusFilter))
        {
            query = query.Where(e => e.Status == statusFilter);
        }

        if ((string.IsNullOrEmpty(req.Severity) == false) && Enum.TryParse<AlertSeverity>(req.Severity, true, out AlertSeverity severityFilter))
        {
            query = query.Where(e => e.Severity == severityFilter);
        }

        int totalCount = await query.CountAsync(ct);

        List<AlertEvent> events = await query
            .OrderByDescending(e => e.TriggeredAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        List<long> machineIds = events.Select(e => e.MachineId).Distinct().ToList();
        Dictionary<long, string> machineNames = await _db.MachineStateSummaries
            .Where(s => machineIds.Contains(s.MachineId))
            .ToDictionaryAsync(s => s.MachineId, s => s.Name, ct);

        List<AlertEventDto> dtos = events.Select(e => new AlertEventDto
        {
            Id = e.Id,
            RuleName = e.AlertRule?.Name ?? string.Empty,
            MachineId = e.MachineId,
            MachineName = machineNames.GetValueOrDefault(e.MachineId, $"Machine {e.MachineId}"),
            Severity = e.Severity.ToString(),
            Message = e.Message,
            Status = e.Status.ToString(),
            TriggeredAt = e.TriggeredAt,
            AcknowledgedAt = e.AcknowledgedAt,
            AcknowledgedByUserId = e.AcknowledgedByUserId,
            ResolvedAt = e.ResolvedAt,
        }).ToList();

        PaginatedResponse<AlertEventDto> response = new()
        {
            Items = dtos,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
        };

        await Send.OkAsync(ApiResponse<PaginatedResponse<AlertEventDto>>.Ok(response), cancellation: ct);
    }
}
