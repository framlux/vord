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
/// Returns one Kubernetes cluster of the current tenant with its nodes.
/// </summary>
public sealed class ClusterDetailEndpoint : EndpointWithoutRequest<ApiResponse<KubernetesClusterDetailDto>>
{
    private readonly KubernetesClusterHandler _handler;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="ClusterDetailEndpoint"/> class.
    /// </summary>
    public ClusterDetailEndpoint(KubernetesClusterHandler handler, ITenantContext tenantContext)
    {
        _handler = handler;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/clusters/{id}");
        Policies(AuthorizationPolicies.ViewOnly);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long clusterId = Route<long>("id");
        int? tenantId = _tenantContext.TenantId;

        ServiceResult<KubernetesClusterDetailDto> result = await _handler.GetAsync(clusterId, tenantId, ct);

        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Cluster not found", ct);

            return;
        }

        await Send.OkAsync(ApiResponse<KubernetesClusterDetailDto>.Ok(result.Data!), cancellation: ct);
    }
}
