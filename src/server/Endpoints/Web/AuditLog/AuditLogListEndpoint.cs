// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;

namespace Framlux.FleetManagement.Server.Endpoints.Web.AuditLog;

/// <summary>
/// Request model for listing audit log entries.
/// </summary>
public sealed class AuditLogListRequest
{
    /// <summary>Page number (1-based).</summary>
    [QueryParam]
    public int Page { get; set; } = 1;

    /// <summary>Items per page.</summary>
    [QueryParam]
    public int PageSize { get; set; } = 25;

    /// <summary>Filter by action type name.</summary>
    [QueryParam]
    public string? Action { get; set; }

    /// <summary>Filter by start date (inclusive).</summary>
    [QueryParam]
    public DateTimeOffset? From { get; set; }

    /// <summary>Filter by end date (inclusive).</summary>
    [QueryParam]
    public DateTimeOffset? To { get; set; }
}

/// <summary>
/// Returns paginated audit log entries for the current tenant.
/// Requires Team tier and TenantAdmin role.
/// </summary>
public sealed class AuditLogListEndpoint : Endpoint<AuditLogListRequest, ApiResponse<PaginatedResponse<AuditLogEntryDto>>>
{
    private readonly IAuditLogRepository _auditLogRepo;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="AuditLogListEndpoint"/> class.
    /// </summary>
    public AuditLogListEndpoint(IAuditLogRepository auditLogRepo, ISubscriptionService subscriptionService)
    {
        _auditLogRepo = auditLogRepo;
        _subscriptionService = subscriptionService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/audit-log");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(AuditLogListRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<PaginatedResponse<AuditLogEntryDto>>.Error("Unauthorized"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier != SubscriptionTier.Team))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<PaginatedResponse<AuditLogEntryDto>>.Error("Audit log requires a Team subscription"), ct);

            return;
        }

        int page = req.Page < 1 ? 1 : req.Page;
        int pageSize = (req.PageSize < 1) || (req.PageSize > 100) ? 25 : req.PageSize;

        AuditAction? actionFilter = null;
        if ((string.IsNullOrEmpty(req.Action) == false) && Enum.TryParse<AuditAction>(req.Action, true, out AuditAction parsed))
        {
            actionFilter = parsed;
        }

        int totalCount = await _auditLogRepo.CountAuditLogEntriesForTenantAsync(tenantId.Value, actionFilter, req.From, req.To, ct);

        List<AuditLogEntry> entries = await _auditLogRepo.GetAuditLogEntriesForTenantAsync(
            tenantId.Value, (page - 1) * pageSize, pageSize, actionFilter, req.From, req.To, ct);

        List<AuditLogEntryDto> dtos = entries.Select(e => new AuditLogEntryDto
        {
            Id = e.Id,
            UserEmail = e.User?.Username,
            UserId = e.UserId,
            MachineId = e.MachineId,
            Action = e.Action.ToString(),
            ResourceType = e.ResourceType.ToString(),
            ResourceId = e.ResourceId,
            Details = e.Details,
            IpAddress = e.IpAddress,
            Timestamp = e.Timestamp,
        }).ToList();

        PaginatedResponse<AuditLogEntryDto> response = new()
        {
            Items = dtos,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
        };

        await Send.OkAsync(ApiResponse<PaginatedResponse<AuditLogEntryDto>>.Ok(response), cancellation: ct);
    }
}
