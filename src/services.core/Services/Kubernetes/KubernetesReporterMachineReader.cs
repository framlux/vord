// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Models.Kubernetes;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using Framlux.FleetManagement.Services.Core.Observability;

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// Describes the machines around a cluster's reporting: which are enrolled as reporter candidates, which
/// could be enrolled, and for each what the cluster page needs to know to offer a command.
/// </summary>
/// <remarks>
/// <para>
/// A machine is eligible when its latest local report from the last seven days says it runs a control plane
/// and holds a cluster-admin credential, which is the rule the command check applies, and it either belongs
/// to the cluster by a node or a candidate row or reports the cluster's CA fingerprint. The fingerprint is
/// what finds a k0s controller, or a k3s server run with its agent disabled, before it has enrolled. Clusters
/// of one tenant can share a CA, so a machine that matches by fingerprint alone is left out once it is known
/// to be a node or candidate of a different cluster.
/// </para>
/// <para>
/// Every read is one query for however many machines there are: the other-cluster links, the reports, the
/// online state, the capabilities and the authorized keys are each fetched for the whole set at once. The
/// capabilities live in Redis, which nothing else on the page needs, so a failed read costs only that one
/// field: it is reported as unknown (null) and counted.
/// </para>
/// </remarks>
public sealed class KubernetesReporterMachineReader
{
    private readonly IKubernetesInventoryRepository _kubernetesRepo;
    private readonly IMachineRepository _machineRepo;
    private readonly IMachineStateRepository _machineStateRepo;
    private readonly ISigningKeyRepository _signingKeyRepo;
    private readonly IMachinePingService _pingService;
    private readonly IQuotaPauseService _quotaPause;
    private readonly QuotaPauseMetrics _quotaPauseMetrics;
    private readonly KubernetesInventoryMetrics _kubernetesMetrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KubernetesReporterMachineReader> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="KubernetesReporterMachineReader"/> class.
    /// </summary>
    public KubernetesReporterMachineReader(
        IKubernetesInventoryRepository kubernetesRepo,
        IMachineRepository machineRepo,
        IMachineStateRepository machineStateRepo,
        ISigningKeyRepository signingKeyRepo,
        IMachinePingService pingService,
        IQuotaPauseService quotaPause,
        QuotaPauseMetrics quotaPauseMetrics,
        KubernetesInventoryMetrics kubernetesMetrics,
        TimeProvider timeProvider,
        ILogger<KubernetesReporterMachineReader> logger)
    {
        ArgumentNullException.ThrowIfNull(kubernetesRepo);
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(machineStateRepo);
        ArgumentNullException.ThrowIfNull(signingKeyRepo);
        ArgumentNullException.ThrowIfNull(pingService);
        ArgumentNullException.ThrowIfNull(quotaPause);
        ArgumentNullException.ThrowIfNull(quotaPauseMetrics);
        ArgumentNullException.ThrowIfNull(kubernetesMetrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _kubernetesRepo = kubernetesRepo;
        _machineRepo = machineRepo;
        _machineStateRepo = machineStateRepo;
        _signingKeyRepo = signingKeyRepo;
        _pingService = pingService;
        _quotaPause = quotaPause;
        _quotaPauseMetrics = quotaPauseMetrics;
        _kubernetesMetrics = kubernetesMetrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Describes a cluster's candidates and finds the machines that could be enrolled for it.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the cluster. Only its machines are considered.</param>
    /// <param name="cluster">The cluster.</param>
    /// <param name="nodes">The cluster's nodes, whose linked machines are members of the cluster.</param>
    /// <param name="candidates">The cluster's reporter candidates.</param>
    /// <param name="candidateNames">The candidates' machine names by machine id.</param>
    /// <param name="now">
    /// The time a snapshot's age is measured from when marking the active reporter. The caller passes the instant it
    /// derives the cluster's reporting state at, so the two cannot disagree.
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The cluster's candidates and eligible machines, each ordered by machine name.</returns>
    public async Task<KubernetesReporterMachines> LoadAsync(
        int tenantId,
        KubernetesCluster cluster,
        IReadOnlyCollection<KubernetesNodeWithMachine> nodes,
        IReadOnlyList<KubernetesReporterCandidate> candidates,
        IReadOnlyDictionary<long, string> candidateNames,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(candidateNames);

        Dictionary<long, KubernetesNodePayload> eligibleReports = await FindEligibleReportsAsync(tenantId, cluster, nodes, candidates, ct);

        Dictionary<long, string> names = new(candidateNames);
        List<long> missingNames = eligibleReports.Keys.Where(id => names.ContainsKey(id) == false).ToList();
        if (missingNames.Count > 0)
        {
            foreach (KeyValuePair<long, string> name in await _kubernetesRepo.GetMachineNamesInTenantAsync(tenantId, missingNames, ct))
            {
                names[name.Key] = name.Value;
            }
        }

        List<long> everyMachineId = candidates.Select(c => c.MachineId).Concat(eligibleReports.Keys).Distinct().ToList();
        HashSet<long> online = await GetOnlineAsync(everyMachineId, ct);

        List<KubernetesCandidateDto> candidateDtos = candidates
            .Select(c => new KubernetesCandidateDto
            {
                MachineId = c.MachineId,
                MachineName = names.GetValueOrDefault(c.MachineId) ?? string.Empty,
                IsActiveReporter = KubernetesReporting.IsActiveReporter(cluster, c.MachineId, now),
                ReporterError = c.ReporterError,
                LastReportAt = c.LastReportAt,
                Online = online.Contains(c.MachineId),
            })
            .OrderBy(c => c.MachineName, StringComparer.Ordinal)
            .ThenBy(c => c.MachineId)
            .ToList();

        List<KubernetesEligibleMachineDto> eligibleDtos = await DescribeEligibleMachinesAsync(
            tenantId, eligibleReports, names, online, ct);

        return new KubernetesReporterMachines(candidateDtos, eligibleDtos);
    }

    /// <summary>
    /// Reads the latest local report of the tenant's active machines that could belong to the cluster, in one
    /// query, and keeps those of machines that can enrol and belong to it. Only active machines are asked
    /// about, which is what keeps a deleted machine out whatever rows still name it, and a machine that is a
    /// node or candidate only of other clusters is not asked about either, so the read never fetches its rows.
    /// </summary>
    private async Task<Dictionary<long, KubernetesNodePayload>> FindEligibleReportsAsync(
        int tenantId,
        KubernetesCluster cluster,
        IReadOnlyCollection<KubernetesNodeWithMachine> nodes,
        IReadOnlyList<KubernetesReporterCandidate> candidates,
        CancellationToken ct)
    {
        List<long> activeMachineIds = await _machineRepo.GetActiveMachineIdsForTenantAsync(tenantId, ct);
        Dictionary<long, KubernetesNodePayload> eligible = new();
        if (activeMachineIds.Count == 0)
        {
            return eligible;
        }

        HashSet<long> members = nodes
            .Where(n => n.Node.MachineId is not null)
            .Select(n => n.Node.MachineId!.Value)
            .Concat(candidates.Select(c => c.MachineId))
            .ToHashSet();

        // Clusters of one tenant can share a CA, so a fingerprint says only that a machine might belong
        // here. One already known to be a node or candidate of a different cluster does not, unless it is
        // a member of this cluster too: a node or candidate of this cluster is always listed.
        IReadOnlyCollection<long> linkedElsewhere = await _kubernetesRepo.GetMachineIdsLinkedToOtherClustersAsync(
            tenantId, cluster.Id, ct);
        HashSet<long> otherClustersOnly = linkedElsewhere.Where(id => members.Contains(id) == false).ToHashSet();
        List<long> candidateMachineIds = activeMachineIds.Where(id => otherClustersOnly.Contains(id) == false).ToList();
        if (candidateMachineIds.Count == 0)
        {
            return eligible;
        }

        DateTimeOffset serverReceivedAfter = _timeProvider.GetUtcNow() - KubernetesCommandEligibility.ReportWindow;
        IReadOnlyDictionary<long, string> payloads = await _kubernetesRepo.GetLatestKubernetesNodePayloadsAsync(
            tenantId, candidateMachineIds, serverReceivedAfter, ct);

        foreach (KeyValuePair<long, string> stored in payloads)
        {
            KubernetesNodePayload? report = KubernetesNodePayloadNormaliser.NormaliseJson(stored.Value);
            if (KubernetesCommandEligibility.CanEnrol(report) == false)
            {
                continue;
            }

            // An unusable fingerprint is normalised to empty, and the cluster's own is never empty, so
            // only a report that really carries this cluster's CA matches by fingerprint.
            bool belongsToCluster = members.Contains(stored.Key) || string.Equals(report!.CaFingerprint, cluster.CaFingerprint, StringComparison.Ordinal);
            if (belongsToCluster)
            {
                eligible[stored.Key] = report!;
            }
        }

        return eligible;
    }

    private async Task<List<KubernetesEligibleMachineDto>> DescribeEligibleMachinesAsync(
        int tenantId,
        Dictionary<long, KubernetesNodePayload> reports,
        Dictionary<long, string> names,
        HashSet<long> online,
        CancellationToken ct)
    {
        if (reports.Count == 0)
        {
            return [];
        }

        List<long> ids = reports.Keys.ToList();
        QuotaCutoff cutoff = await QuotaCutoffDisplay.GetOrNoneAsync(_quotaPause, _quotaPauseMetrics, _logger, tenantId, ct);
        Dictionary<long, ulong>? capabilities = await TryReadCapabilitiesAsync(tenantId, ids);
        IReadOnlyDictionary<long, IReadOnlyList<int>> keys = await _signingKeyRepo.GetActiveSigningKeyIdsForMachinesAsync(tenantId, ids, ct);

        return ids
            .Select(id => new KubernetesEligibleMachineDto
            {
                MachineId = id,
                MachineName = names.GetValueOrDefault(id) ?? string.Empty,
                Online = online.Contains(id),
                IsPaused = cutoff.IsPaused(id),
                RemoteCommandsEnabled = (capabilities is null) ? null : AgentCapabilities.HasRemoteCommands(capabilities.GetValueOrDefault(id)),
                Enrolled = reports[id].ReporterEnrolled,
                ReporterError = reports[id].ReporterError,
                AuthorizedSigningKeyIds = keys.TryGetValue(id, out IReadOnlyList<int>? keyIds) ? keyIds.ToList() : [],
            })
            .OrderBy(m => m.MachineName, StringComparer.Ordinal)
            .ThenBy(m => m.MachineId)
            .ToList();
    }

    /// <summary>
    /// Reads the machines' capability masks, or returns null when the store cannot be read. The masks only
    /// say whether remote commands are enabled, so a failure is logged and counted and the page goes on
    /// without them, rather than failing for a fact the rest of it does not depend on. Redis raises several
    /// kinds of exception, so any is absorbed except a cancellation, which still cancels the request.
    /// </summary>
    private async Task<Dictionary<long, ulong>?> TryReadCapabilitiesAsync(int tenantId, List<long> machineIds)
    {
        try
        {
            return await _pingService.GetAgentCapabilitiesBatchAsync(machineIds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _kubernetesMetrics.RecordCapabilityReadFailure();
            _logger.LogWarning(
                ex, "Could not read agent capabilities for {MachineCount} machines of tenant {TenantId}; reporting remote commands as unknown",
                machineIds.Count, tenantId);

            return null;
        }
    }

    /// <summary>
    /// Finds which of the machines the server has heard from lately, by the same rule as every other read
    /// path. A machine with no summary row has never been heard from.
    /// </summary>
    private async Task<HashSet<long>> GetOnlineAsync(List<long> machineIds, CancellationToken ct)
    {
        if (machineIds.Count == 0)
        {
            return [];
        }

        List<MachineStateSummary> summaries = await _machineStateRepo.GetSummaryListByMachineIdsAsync(machineIds, ct);

        return summaries.Where(s => MachineLiveness.IsOnline(s)).Select(s => s.MachineId).ToHashSet();
    }
}
