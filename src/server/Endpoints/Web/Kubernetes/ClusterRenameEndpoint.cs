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
/// Renames a Kubernetes cluster of the current tenant, or resets it to its derived name.
/// </summary>
public sealed class ClusterRenameEndpoint : Endpoint<RenameClusterRequest, ApiResponse<KubernetesClusterSummaryDto>>
{
    private readonly KubernetesClusterHandler _handler;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="ClusterRenameEndpoint"/> class.
    /// </summary>
    public ClusterRenameEndpoint(KubernetesClusterHandler handler, ITenantContext tenantContext)
    {
        _handler = handler;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Verbs(Http.PATCH);
        Routes("/clusters/{id}");
        Policies(AuthorizationPolicies.MachineAdmin);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(RenameClusterRequest req, CancellationToken ct)
    {
        long clusterId = Route<long>("id");
        int? tenantId = _tenantContext.TenantId;

        int? userId = _tenantContext.UserId;
        if (userId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unable to identify user", ct);

            return;
        }

        ServiceResult<KubernetesClusterSummaryDto> result = await _handler.RenameAsync(
            clusterId, tenantId, userId.Value, req.DisplayName, ct);

        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Cluster not found", ct);

            return;
        }

        if (result.StatusCode == 400)
        {
            await HttpContext.SendApiErrorAsync(400, result.ErrorMessage ?? "Invalid request", ct);

            return;
        }

        await Send.OkAsync(ApiResponse<KubernetesClusterSummaryDto>.Ok(result.Data!), cancellation: ct);
    }
}
