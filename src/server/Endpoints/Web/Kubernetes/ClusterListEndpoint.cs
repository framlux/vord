// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models;
using Framlux.FleetManagement.Services.Core.Models.Kubernetes;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Kubernetes;

/// <summary>
/// Lists the Kubernetes clusters of the current tenant.
/// </summary>
public sealed class ClusterListEndpoint : EndpointWithoutRequest<ApiResponse<List<KubernetesClusterSummaryDto>>>
{
    private readonly KubernetesClusterHandler _handler;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="ClusterListEndpoint"/> class.
    /// </summary>
    public ClusterListEndpoint(KubernetesClusterHandler handler, ITenantContext tenantContext)
    {
        _handler = handler;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/clusters");
        Policies(AuthorizationPolicies.ViewOnly);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = _tenantContext.TenantId;

        ServiceResult<List<KubernetesClusterSummaryDto>> result = await _handler.ListAsync(tenantId, ct);

        await Send.OkAsync(ApiResponse<List<KubernetesClusterSummaryDto>>.Ok(result.Data!), cancellation: ct);
    }
}
