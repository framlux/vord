// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Kubernetes;
using Framlux.FleetManagement.Services.Core.Models.Kubernetes;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles reading a tenant's Kubernetes clusters and renaming them.
/// </summary>
public sealed class KubernetesClusterHandler
{
    private const int DisplayNameMaxLength = 100;

    private readonly IKubernetesInventoryRepository _kubernetesRepo;
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;

    /// <summary>
    /// Creates a new instance of the <see cref="KubernetesClusterHandler"/> class.
    /// </summary>
    public KubernetesClusterHandler(
        IKubernetesInventoryRepository kubernetesRepo,
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog)
    {
        ArgumentNullException.ThrowIfNull(kubernetesRepo);
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(auditLog);

        _kubernetesRepo = kubernetesRepo;
        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
    }

    /// <summary>
    /// Lists the tenant's Kubernetes clusters.
    /// </summary>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing a summary of each cluster.</returns>
    public async Task<ServiceResult<List<KubernetesClusterSummaryDto>>> ListAsync(int? tenantId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<List<KubernetesClusterSummaryDto>>.Ok([]);
        }

        IReadOnlyList<KubernetesCluster> clusters = await _kubernetesRepo.ListClustersAsync(tenantId.Value, ct);
        ILookup<long, KubernetesNodeWithMachine> nodesByCluster = await LoadNodesAsync(tenantId.Value, clusters, ct);

        List<KubernetesClusterSummaryDto> summaries = clusters
            .Select(c => KubernetesClusterMapper.ToSummary(c, nodesByCluster[c.Id].ToList()))
            .ToList();

        return ServiceResult<List<KubernetesClusterSummaryDto>>.Ok(summaries);
    }

    /// <summary>
    /// Returns one of the tenant's clusters with its nodes.
    /// </summary>
    /// <param name="clusterId">The cluster ID.</param>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the cluster, or not found when it is unknown or belongs to another tenant.</returns>
    public async Task<ServiceResult<KubernetesClusterDetailDto>> GetAsync(long clusterId, int? tenantId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<KubernetesClusterDetailDto>.NotFound();
        }

        KubernetesCluster? cluster = await _kubernetesRepo.GetClusterAsync(tenantId.Value, clusterId, ct);
        if (cluster is null)
        {
            return ServiceResult<KubernetesClusterDetailDto>.NotFound();
        }

        IReadOnlyList<KubernetesNodeWithMachine> nodes = await _kubernetesRepo.ListNodesAsync(tenantId.Value, [cluster.Id], ct);

        return ServiceResult<KubernetesClusterDetailDto>.Ok(KubernetesClusterMapper.ToDetail(cluster, nodes));
    }

    /// <summary>
    /// Renames a cluster, or resets it to its derived name, and audits the change in the same
    /// transaction.
    /// </summary>
    /// <param name="clusterId">The cluster to rename.</param>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="userId">The user performing the rename.</param>
    /// <param name="displayName">
    /// The new name. It is trimmed, and null, empty or whitespace resets the cluster to its derived name.
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the updated cluster summary.</returns>
    public async Task<ServiceResult<KubernetesClusterSummaryDto>> RenameAsync(
        long clusterId,
        int? tenantId,
        int userId,
        string? displayName,
        CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<KubernetesClusterSummaryDto>.NotFound();
        }

        string? name = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if ((name is not null) && (name.Length > DisplayNameMaxLength))
        {
            return ServiceResult<KubernetesClusterSummaryDto>.BadRequest(
                $"Display name must be {DisplayNameMaxLength} characters or fewer.");
        }

        using (IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct))
        {
            bool renamed = await _kubernetesRepo.RenameClusterAsync(tenantId.Value, clusterId, name, ct);
            if (renamed == false)
            {
                return ServiceResult<KubernetesClusterSummaryDto>.NotFound();
            }

            await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
                tenantId, userId, null,
                AuditAction.KubernetesClusterRenamed, AuditResourceType.KubernetesCluster,
                clusterId.ToString(), new { DisplayName = name }, null), ct);

            await transaction.CommitAsync(ct);
        }

        KubernetesCluster? cluster = await _kubernetesRepo.GetClusterAsync(tenantId.Value, clusterId, ct);
        if (cluster is null)
        {
            return ServiceResult<KubernetesClusterSummaryDto>.NotFound();
        }

        IReadOnlyList<KubernetesNodeWithMachine> nodes = await _kubernetesRepo.ListNodesAsync(tenantId.Value, [cluster.Id], ct);

        return ServiceResult<KubernetesClusterSummaryDto>.Ok(KubernetesClusterMapper.ToSummary(cluster, nodes));
    }

    private async Task<ILookup<long, KubernetesNodeWithMachine>> LoadNodesAsync(
        int tenantId, IReadOnlyList<KubernetesCluster> clusters, CancellationToken ct)
    {
        if (clusters.Count == 0)
        {
            return Enumerable.Empty<KubernetesNodeWithMachine>().ToLookup(n => n.Node.ClusterId);
        }

        IReadOnlyList<KubernetesNodeWithMachine> nodes = await _kubernetesRepo.ListNodesAsync(
            tenantId, clusters.Select(c => c.Id).ToList(), ct);

        return nodes.ToLookup(n => n.Node.ClusterId);
    }
}
