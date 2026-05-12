// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Services.Core.Models.Tenants;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Returns a list of tenants.
/// </summary>
public sealed class TenantListEndpoint : EndpointWithoutRequest<ApiResponse<List<TenantDto>>>
{
    private readonly ITenantHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="TenantListEndpoint"/> class.
    /// </summary>
    public TenantListEndpoint(ITenantHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/tenants");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        // Global admins see all tenants
        string? iga = User.FindFirstValue("iga");
        bool isGlobalAdmin = string.Equals(iga, bool.TrueString, StringComparison.OrdinalIgnoreCase);

        List<int> tenantIds = User.FindAll(ClaimTypes.Role)
            .Select(c => c.Value.Split(':'))
            .Where(parts => parts.Length == 2 && int.TryParse(parts[0], out _))
            .Select(parts => int.Parse(parts[0]))
            .Distinct()
            .ToList();

        ServiceResult<List<TenantDto>> result = await _handler.ListForUserAsync(isGlobalAdmin, tenantIds, ct);

        await Send.OkAsync(ApiResponse<List<TenantDto>>.Ok(result.Data ?? []), cancellation: ct);
    }
}
