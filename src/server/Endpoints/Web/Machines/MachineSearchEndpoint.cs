// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Machines;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Searches machines with advanced filter criteria including telemetry-based ranges.
/// </summary>
public sealed class MachineSearchEndpoint : EndpointWithoutRequest<ApiResponse<PaginatedResponse<FleetMachineDto>>>
{
    private readonly IMachineSearchService _searchService;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineSearchEndpoint"/> class.
    /// </summary>
    public MachineSearchEndpoint(IMachineSearchService searchService)
    {
        _searchService = searchService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/search");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        MachineSearchCriteria criteria = new()
        {
            Page = Math.Max(1, Query<int?>("page", isRequired: false) ?? 1),
            PageSize = Math.Clamp(Query<int?>("pageSize", isRequired: false) ?? 25, 1, 100),
            Search = Query<string?>("search", isRequired: false),
            HealthStatus = Query<string?>("healthStatus", isRequired: false),
            Os = Query<string?>("os", isRequired: false),
            Type = Query<string?>("type", isRequired: false),
            CpuMin = Query<int?>("cpuMin", isRequired: false),
            CpuMax = Query<int?>("cpuMax", isRequired: false),
            MemoryMin = Query<int?>("memoryMin", isRequired: false),
            MemoryMax = Query<int?>("memoryMax", isRequired: false),
            DiskMin = Query<int?>("diskMin", isRequired: false),
            DiskMax = Query<int?>("diskMax", isRequired: false),
            PendingUpdatesMin = Query<int?>("pendingUpdatesMin", isRequired: false),
            SecurityUpdatesMin = Query<int?>("securityUpdatesMin", isRequired: false),
            FailedServicesMin = Query<int?>("failedServicesMin", isRequired: false),
            HasDiskHealthIssue = Query<bool?>("hasDiskHealthIssue", isRequired: false),
            HasHardwareIssue = Query<bool?>("hasHardwareIssue", isRequired: false),
            SortBy = Query<string?>("sortBy", isRequired: false) ?? "name",
            SortDir = Query<string?>("sortDir", isRequired: false) ?? "asc",
        };

        // Parse date filters separately since they need DateTimeOffset parsing.
        string? lastSeenAfterRaw = Query<string?>("lastSeenAfter", isRequired: false);
        if (string.IsNullOrWhiteSpace(lastSeenAfterRaw) == false &&
            DateTimeOffset.TryParse(lastSeenAfterRaw, out DateTimeOffset lastSeenAfter))
        {
            criteria.LastSeenAfter = lastSeenAfter;
        }

        string? lastSeenBeforeRaw = Query<string?>("lastSeenBefore", isRequired: false);
        if (string.IsNullOrWhiteSpace(lastSeenBeforeRaw) == false &&
            DateTimeOffset.TryParse(lastSeenBeforeRaw, out DateTimeOffset lastSeenBefore))
        {
            criteria.LastSeenBefore = lastSeenBefore;
        }

        PaginatedResponse<FleetMachineDto> result = await _searchService.SearchAsync(criteria, tenantId, ct);

        await Send.OkAsync(ApiResponse<PaginatedResponse<FleetMachineDto>>.Ok(result), cancellation: ct);
    }
}
