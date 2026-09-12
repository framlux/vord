// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Models.Machines;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Returns the ids of the machines a filter matches, so a caller can build a selection without
/// paging the full search endpoint and discarding every field but the id.
/// </summary>
public sealed class MachineIdSelectionEndpoint : EndpointWithoutRequest<ApiResponse<MachineIdSelectionDto>>
{
    private readonly MachineSearchService _searchService;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineIdSelectionEndpoint"/> class.
    /// </summary>
    public MachineIdSelectionEndpoint(MachineSearchService searchService, ITenantContext tenantContext)
    {
        _searchService = searchService;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/ids");
        Policies(AuthorizationPolicies.ViewOnly);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        // The filter surface deliberately mirrors the subset of /machines/search that the
        // assignment picker exposes. Anything the picker cannot filter by has no business
        // widening a bulk selection.
        MachineSearchCriteria criteria = new()
        {
            Search = Query<string?>("search", isRequired: false),
            HealthStatus = Query<string?>("healthStatus", isRequired: false),
            Os = Query<string?>("os", isRequired: false),
            Type = Query<string?>("type", isRequired: false),
        };

        MachineIdSelectionDto selection = await _searchService.SearchIdsAsync(criteria, _tenantContext.TenantId, ct);

        await Send.OkAsync(ApiResponse<MachineIdSelectionDto>.Ok(selection), cancellation: ct);
    }
}
