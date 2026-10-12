// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Kubernetes;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Models.Kubernetes;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using System.Text.Json;

namespace Framlux.FleetManagement.Test.Services.Kubernetes;

/// <summary>
/// Tests for <see cref="KubernetesReporterMachineReader"/>: which machines of a tenant a cluster can
/// enrol as reporters, what is known about each, and how a cluster's candidates are described.
/// </summary>
public sealed class KubernetesReporterMachineReaderTests
{
    private const int TenantId = 5;
    private const long ClusterId = 11;

    private const string Fingerprint = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";
    private const string OtherFingerprint = "0f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a6978";

    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);

    private readonly IKubernetesInventoryRepository _kubernetesRepo = Substitute.For<IKubernetesInventoryRepository>();
    private readonly IMachineRepository _machineRepo = Substitute.For<IMachineRepository>();
    private readonly IMachineStateRepository _machineStateRepo = Substitute.For<IMachineStateRepository>();
    private readonly ISigningKeyRepository _signingKeyRepo = Substitute.For<ISigningKeyRepository>();
    private readonly IMachinePingService _pingService = Substitute.For<IMachinePingService>();
    private readonly FakeTimeProvider _time = new(Now);

    private Dictionary<long, string> _payloads = new();
    private Dictionary<long, string> _names = new();
    private Dictionary<long, ulong> _capabilities = new();
    private Dictionary<long, IReadOnlyList<int>> _keys = new();
    private List<MachineStateSummary> _summaries = [];
    private List<long> _activeMachineIds = [];

    private KubernetesReporterMachineReader CreateReader(IQuotaPauseService? quotaPause = null)
    {
        _machineRepo.GetActiveMachineIdsForTenantAsync(TenantId, Arg.Any<CancellationToken>()).Returns(_ => _activeMachineIds);
        _kubernetesRepo.GetLatestKubernetesNodePayloadsAsync(
                TenantId, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                IReadOnlyCollection<long> asked = call.ArgAt<IReadOnlyCollection<long>>(1);

                return (IReadOnlyDictionary<long, string>)_payloads.Where(p => asked.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            });
        _kubernetesRepo.GetMachineNamesInTenantAsync(TenantId, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                IReadOnlyCollection<long> asked = call.ArgAt<IReadOnlyCollection<long>>(1);

                return (IReadOnlyDictionary<long, string>)_names.Where(n => asked.Contains(n.Key)).ToDictionary(n => n.Key, n => n.Value);
            });
        _machineStateRepo.GetSummaryListByMachineIdsAsync(Arg.Any<List<long>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                List<long> asked = call.ArgAt<List<long>>(0);

                return _summaries.Where(s => asked.Contains(s.MachineId)).ToList();
            });
        _pingService.GetAgentCapabilitiesBatchAsync(Arg.Any<IEnumerable<long>>())
            .Returns(call =>
            {
                IEnumerable<long> asked = call.ArgAt<IEnumerable<long>>(0);

                return asked.ToDictionary(id => id, id => _capabilities.GetValueOrDefault(id));
            });
        _signingKeyRepo.GetActiveSigningKeyIdsForMachinesAsync(TenantId, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                IReadOnlyCollection<long> asked = call.ArgAt<IReadOnlyCollection<long>>(1);

                return (IReadOnlyDictionary<long, IReadOnlyList<int>>)_keys.Where(k => asked.Contains(k.Key)).ToDictionary(k => k.Key, k => k.Value);
            });

        return new KubernetesReporterMachineReader(
            _kubernetesRepo,
            _machineRepo,
            _machineStateRepo,
            _signingKeyRepo,
            _pingService,
            quotaPause ?? TestQuotaPause.Nothing(),
            TestMetricsFactory.CreateQuotaPauseMetrics(),
            _time,
            NullLogger<KubernetesReporterMachineReader>.Instance);
    }

    private static KubernetesCluster Cluster(long? holder = null)
    {
        return new KubernetesCluster
        {
            Id = ClusterId,
            TenantId = TenantId,
            CaFingerprint = Fingerprint,
            Distro = "k3s",
            FirstSeenAt = Now.AddDays(-2),
            LastSeenAt = Now,
            ActiveReporterMachineId = holder,
        };
    }

    private static string PayloadJson(
        string fingerprint = Fingerprint,
        bool controlPlane = true,
        bool adminCredentialPresent = true,
        bool reporterEnrolled = false,
        string distro = "k3s",
        string nodeName = "cp-1",
        bool hasKubelet = true)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["distro"] = distro,
            ["kubelet_version"] = "v1.30.4+k3s1",
            ["node_name"] = nodeName,
            ["node_name_source"] = "certificate",
            ["has_kubelet"] = hasKubelet,
            ["control_plane"] = controlPlane,
            ["ca_fingerprint"] = fingerprint,
            ["admin_credential_present"] = adminCredentialPresent,
            ["reporter_enrolled"] = reporterEnrolled,
        });
    }

    private static KubernetesNodeWithMachine NodeOf(long? machineId)
    {
        return new KubernetesNodeWithMachine(
            new KubernetesNode { Id = 100 + (machineId ?? 0), TenantId = TenantId, ClusterId = ClusterId, NodeName = $"node-{machineId}", MachineId = machineId },
            null);
    }

    private static KubernetesReporterCandidate CandidateOf(long machineId, string? reporterError = null, DateTimeOffset? lastReportAt = null)
    {
        return new KubernetesReporterCandidate
        {
            TenantId = TenantId,
            ClusterId = ClusterId,
            MachineId = machineId,
            ReporterError = reporterError,
            LastReportAt = lastReportAt ?? Now.AddMinutes(-2),
        };
    }

    private void Machine(long id, string name, string? payload = null, bool active = true)
    {
        _names[id] = name;
        if (active)
        {
            _activeMachineIds.Add(id);
        }

        if (payload is not null)
        {
            _payloads[id] = payload;
        }
    }

    private void Summary(long machineId, short healthStatus)
    {
        _summaries.Add(TestDataBuilder.BuildMachineStateSummary(machineId: machineId, tenantId: TenantId, healthStatus: healthStatus));
    }

    // ========== Eligible machines: membership ==========

    [Test]
    public async Task LoadAsync_ControlPlaneNodeOfTheCluster_IsEligibleWithEveryGatingField()
    {
        Machine(21, "cp-host", PayloadJson(reporterEnrolled: true));
        Summary(21, 0);
        _capabilities[21] = 1;
        _keys[21] = [3, 9];
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [NodeOf(21)], [], new Dictionary<long, string>(), CancellationToken.None);

        await Assert.That(result.EligibleMachines.Count).IsEqualTo(1);
        KubernetesEligibleMachineDto machine = result.EligibleMachines[0];
        await Assert.That(machine.MachineId).IsEqualTo(21L);
        await Assert.That(machine.MachineName).IsEqualTo("cp-host");
        await Assert.That(machine.Online).IsTrue();
        await Assert.That(machine.IsPaused).IsFalse();
        await Assert.That(machine.RemoteCommandsEnabled).IsTrue();
        await Assert.That(machine.Enrolled).IsTrue();
        await Assert.That(machine.AuthorizedSigningKeyIds.ToArray()).IsEquivalentTo(new[] { 3, 9 });
    }

    [Test]
    public async Task LoadAsync_CandidateOfTheCluster_IsEligibleEvenWithNoNode()
    {
        Machine(22, "k0s-controller", PayloadJson(fingerprint: OtherFingerprint, hasKubelet: false, nodeName: ""));
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [], [CandidateOf(22)], new Dictionary<long, string> { [22] = "k0s-controller" }, CancellationToken.None);

        await Assert.That(result.EligibleMachines.Select(m => m.MachineId).ToArray()).IsEquivalentTo(new[] { 22L });
    }

    [Test]
    public async Task LoadAsync_MachineThatOnlyHoldsTheClustersCaFingerprint_IsEligible()
    {
        // Intent: a k0s controller or a k3s server run with its agent disabled is no node and no candidate
        // before its first enrol, but its CA identifies the cluster it can report for.
        Machine(23, "k0s-controller", PayloadJson(hasKubelet: false, nodeName: ""));
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [], [], new Dictionary<long, string>(), CancellationToken.None);

        await Assert.That(result.EligibleMachines.Select(m => m.MachineId).ToArray()).IsEquivalentTo(new[] { 23L });
        await Assert.That(result.EligibleMachines[0].Enrolled).IsFalse();
    }

    [Test]
    public async Task LoadAsync_MachineHoldingAnotherClustersFingerprintAndNoMembership_IsNotEligible()
    {
        Machine(24, "elsewhere", PayloadJson(fingerprint: OtherFingerprint));
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [], [], new Dictionary<long, string>(), CancellationToken.None);

        await Assert.That(result.EligibleMachines.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LoadAsync_NodeWhoseFingerprintDiffersFromTheClusters_StaysEligibleThroughItsNode()
    {
        // Intent: membership by node does not depend on the fingerprint, so a node in the middle of a CA
        // rotation can still be enrolled or repaired from the cluster page.
        Machine(25, "rotating", PayloadJson(fingerprint: OtherFingerprint));
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [NodeOf(25)], [], new Dictionary<long, string>(), CancellationToken.None);

        await Assert.That(result.EligibleMachines.Select(m => m.MachineId).ToArray()).IsEquivalentTo(new[] { 25L });
    }

    [Test]
    public async Task LoadAsync_MemberWithoutAControlPlaneOrAnAdminCredential_IsNotEligible()
    {
        Machine(26, "worker", PayloadJson(controlPlane: false));
        Machine(27, "no-credential", PayloadJson(adminCredentialPresent: false));
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [NodeOf(26), NodeOf(27)], [], new Dictionary<long, string>(), CancellationToken.None);

        await Assert.That(result.EligibleMachines.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LoadAsync_MemberWithNoRecentReportOrAnUnusableOne_IsNotEligible()
    {
        Machine(28, "silent");
        Machine(29, "garbled", "not json at all");
        Machine(30, "refused", PayloadJson(nodeName: "Not_A_Node"));
        Machine(31, "null-report", "null");
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [NodeOf(28), NodeOf(29), NodeOf(30), NodeOf(31)], [], new Dictionary<long, string>(), CancellationToken.None);

        await Assert.That(result.EligibleMachines.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LoadAsync_MemberThatIsNotAnActiveMachine_IsNotEligible()
    {
        // Intent: a machine that was deleted can still have telemetry and a node row, but cannot be
        // sent a command, so it must not be offered.
        Machine(32, "deleted-host", PayloadJson(), active: false);
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [NodeOf(32)], [CandidateOf(32)], new Dictionary<long, string>(), CancellationToken.None);

        await Assert.That(result.EligibleMachines.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LoadAsync_EligibleMachines_AreOrderedByNameThenId()
    {
        Machine(41, "zulu", PayloadJson());
        Machine(42, "alpha", PayloadJson());
        Machine(44, "same", PayloadJson());
        Machine(43, "same", PayloadJson());
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [], [], new Dictionary<long, string>(), CancellationToken.None);

        await Assert.That(result.EligibleMachines.Select(m => m.MachineId).ToArray()).IsEquivalentTo(new[] { 42L, 43L, 44L, 41L });
        await Assert.That(result.EligibleMachines[0].MachineId).IsEqualTo(42L);
        await Assert.That(result.EligibleMachines[1].MachineId).IsEqualTo(43L);
        await Assert.That(result.EligibleMachines[3].MachineId).IsEqualTo(41L);
    }

    // ========== Eligible machines: how they are read ==========

    [Test]
    public async Task LoadAsync_ReadsEveryReportInOneQueryWithinTheSevenDayServerWindow()
    {
        // Intent: eligibility must cost one payload read however many machines the tenant has, and the
        // window is measured on the server's clock exactly as the command check measures it.
        Machine(21, "a", PayloadJson());
        Machine(22, "b", PayloadJson());
        Machine(23, "c", PayloadJson());
        KubernetesReporterMachineReader reader = CreateReader();

        await reader.LoadAsync(TenantId, Cluster(), [], [], new Dictionary<long, string>(), CancellationToken.None);

        await _kubernetesRepo.Received(1).GetLatestKubernetesNodePayloadsAsync(
            TenantId,
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.OrderBy(i => i).SequenceEqual(new long[] { 21, 22, 23 })),
            Now - TimeSpan.FromDays(7),
            Arg.Any<CancellationToken>());
        await _machineStateRepo.Received(1).GetSummaryListByMachineIdsAsync(Arg.Any<List<long>>(), Arg.Any<CancellationToken>());
        await _pingService.Received(1).GetAgentCapabilitiesBatchAsync(Arg.Any<IEnumerable<long>>());
        await _signingKeyRepo.Received(1).GetActiveSigningKeyIdsForMachinesAsync(
            TenantId, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LoadAsync_TenantWithNoMachines_ReadsNothingFurther()
    {
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [], [], new Dictionary<long, string>(), CancellationToken.None);

        await Assert.That(result.EligibleMachines.Count).IsEqualTo(0);
        await Assert.That(result.Candidates.Count).IsEqualTo(0);
        await _pingService.DidNotReceive().GetAgentCapabilitiesBatchAsync(Arg.Any<IEnumerable<long>>());
        await _signingKeyRepo.DidNotReceive().GetActiveSigningKeyIdsForMachinesAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LoadAsync_RemoteCommandsEnabled_IsCapabilityBitZeroOnly()
    {
        Machine(21, "on", PayloadJson());
        Machine(22, "off", PayloadJson());
        Machine(23, "other-bits", PayloadJson());
        Machine(24, "all-bits", PayloadJson());
        _capabilities[21] = 1;
        _capabilities[23] = 2;
        _capabilities[24] = ulong.MaxValue;
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [], [], new Dictionary<long, string>(), CancellationToken.None);

        Dictionary<long, bool> enabled = result.EligibleMachines.ToDictionary(m => m.MachineId, m => m.RemoteCommandsEnabled);
        await Assert.That(enabled[21]).IsTrue();
        await Assert.That(enabled[22]).IsFalse();
        await Assert.That(enabled[23]).IsFalse();
        await Assert.That(enabled[24]).IsTrue();
    }

    [Test]
    public async Task LoadAsync_PausedHosts_AreFlaggedWhileStillHeartbeatOnline()
    {
        // Intent: a host past the plan limit still heartbeats, so it is online, but it gets a 409 from the
        // command endpoint, and the page needs both facts to explain why it cannot be enrolled.
        Machine(21, "active", PayloadJson());
        Machine(22, "paused", PayloadJson());
        Summary(21, 0);
        Summary(22, 0);
        KubernetesReporterMachineReader reader = CreateReader(TestQuotaPause.With(QuotaCutoff.After(21), 1));

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [], [], new Dictionary<long, string>(), CancellationToken.None);

        KubernetesEligibleMachineDto active = result.EligibleMachines.Single(m => m.MachineId == 21);
        KubernetesEligibleMachineDto paused = result.EligibleMachines.Single(m => m.MachineId == 22);
        await Assert.That(active.IsPaused).IsFalse();
        await Assert.That(paused.IsPaused).IsTrue();
        await Assert.That(paused.Online).IsTrue();
    }

    [Test]
    public async Task LoadAsync_PauseDecisionUnavailable_ShowsHostsAsActive()
    {
        Machine(21, "host", PayloadJson());
        KubernetesReporterMachineReader reader = CreateReader(TestQuotaPause.Unavailable());

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [], [], new Dictionary<long, string>(), CancellationToken.None);

        await Assert.That(result.EligibleMachines.Single().IsPaused).IsFalse();
    }

    [Test]
    public async Task LoadAsync_OnlineFollowsTheSweptHealthWithAMissingSummaryOffline()
    {
        Machine(21, "healthy", PayloadJson());
        Machine(22, "critical", PayloadJson());
        Machine(23, "offline", PayloadJson());
        Machine(24, "never-heard-from", PayloadJson());
        Summary(21, 0);
        Summary(22, 2);
        Summary(23, 3);
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [], [], new Dictionary<long, string>(), CancellationToken.None);

        Dictionary<long, bool> online = result.EligibleMachines.ToDictionary(m => m.MachineId, m => m.Online);
        await Assert.That(online[21]).IsTrue();
        await Assert.That(online[22]).IsTrue();
        await Assert.That(online[23]).IsFalse();
        await Assert.That(online[24]).IsFalse();
    }

    [Test]
    public async Task LoadAsync_MachineWithoutAuthorizedKeys_HasAnEmptyKeyList()
    {
        Machine(21, "host", PayloadJson());
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [], [], new Dictionary<long, string>(), CancellationToken.None);

        await Assert.That(result.EligibleMachines.Single().AuthorizedSigningKeyIds.Count).IsEqualTo(0);
    }

    // ========== Candidates ==========

    [Test]
    public async Task LoadAsync_Candidates_CarryHolderErrorReportTimeAndOnline()
    {
        Summary(21, 0);
        Summary(22, 3);
        Dictionary<long, string> names = new() { [21] = "cp-one", [22] = "cp-two" };
        DateTimeOffset reported = Now.AddMinutes(-7);
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId,
            Cluster(holder: 21),
            [],
            [CandidateOf(22, "credentials_rejected", reported), CandidateOf(21)],
            names,
            CancellationToken.None);

        await Assert.That(result.Candidates.Count).IsEqualTo(2);
        KubernetesCandidateDto holder = result.Candidates[0];
        await Assert.That(holder.MachineId).IsEqualTo(21L);
        await Assert.That(holder.MachineName).IsEqualTo("cp-one");
        await Assert.That(holder.IsHolder).IsTrue();
        await Assert.That(holder.ReporterError).IsNull();
        await Assert.That(holder.Online).IsTrue();
        KubernetesCandidateDto other = result.Candidates[1];
        await Assert.That(other.MachineId).IsEqualTo(22L);
        await Assert.That(other.IsHolder).IsFalse();
        await Assert.That(other.ReporterError).IsEqualTo("credentials_rejected");
        await Assert.That(other.LastReportAt).IsEqualTo(reported);
        await Assert.That(other.Online).IsFalse();
    }

    [Test]
    public async Task LoadAsync_Candidates_AreOrderedByMachineNameOrdinallyThenId()
    {
        Dictionary<long, string> names = new() { [1] = "beta", [2] = "Zulu", [3] = "alpha", [4] = "alpha" };
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(), [], [CandidateOf(1), CandidateOf(2), CandidateOf(4), CandidateOf(3)], names, CancellationToken.None);

        await Assert.That(result.Candidates.Select(c => c.MachineId).ToArray()).IsEquivalentTo(new[] { 2L, 3L, 4L, 1L });
        await Assert.That(result.Candidates[0].MachineId).IsEqualTo(2L);
        await Assert.That(result.Candidates[1].MachineId).IsEqualTo(3L);
        await Assert.That(result.Candidates[2].MachineId).IsEqualTo(4L);
    }

    [Test]
    public async Task LoadAsync_CandidateAlsoEligible_IsOnlineLookedUpOnce()
    {
        Machine(21, "cp-one", PayloadJson());
        Summary(21, 0);
        KubernetesReporterMachineReader reader = CreateReader();

        KubernetesReporterMachines result = await reader.LoadAsync(
            TenantId, Cluster(holder: 21), [], [CandidateOf(21)], new Dictionary<long, string> { [21] = "cp-one" }, CancellationToken.None);

        await Assert.That(result.Candidates.Single().Online).IsTrue();
        await Assert.That(result.EligibleMachines.Single().Online).IsTrue();
        await _machineStateRepo.Received(1).GetSummaryListByMachineIdsAsync(Arg.Any<List<long>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LoadAsync_NullArguments_Throw()
    {
        KubernetesReporterMachineReader reader = CreateReader();

        await Assert.That(async () => await reader.LoadAsync(TenantId, null!, [], [], new Dictionary<long, string>(), CancellationToken.None))
            .Throws<ArgumentNullException>();
        await Assert.That(async () => await reader.LoadAsync(TenantId, Cluster(), null!, [], new Dictionary<long, string>(), CancellationToken.None))
            .Throws<ArgumentNullException>();
        await Assert.That(async () => await reader.LoadAsync(TenantId, Cluster(), [], null!, new Dictionary<long, string>(), CancellationToken.None))
            .Throws<ArgumentNullException>();
        await Assert.That(async () => await reader.LoadAsync(TenantId, Cluster(), [], [], null!, CancellationToken.None))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullDependency_Throws()
    {
        await Assert.That(() => new KubernetesReporterMachineReader(
            null!, _machineRepo, _machineStateRepo, _signingKeyRepo, _pingService, TestQuotaPause.Nothing(),
            TestMetricsFactory.CreateQuotaPauseMetrics(), _time, NullLogger<KubernetesReporterMachineReader>.Instance))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new KubernetesReporterMachineReader(
            _kubernetesRepo, null!, _machineStateRepo, _signingKeyRepo, _pingService, TestQuotaPause.Nothing(),
            TestMetricsFactory.CreateQuotaPauseMetrics(), _time, NullLogger<KubernetesReporterMachineReader>.Instance))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new KubernetesReporterMachineReader(
            _kubernetesRepo, _machineRepo, null!, _signingKeyRepo, _pingService, TestQuotaPause.Nothing(),
            TestMetricsFactory.CreateQuotaPauseMetrics(), _time, NullLogger<KubernetesReporterMachineReader>.Instance))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new KubernetesReporterMachineReader(
            _kubernetesRepo, _machineRepo, _machineStateRepo, null!, _pingService, TestQuotaPause.Nothing(),
            TestMetricsFactory.CreateQuotaPauseMetrics(), _time, NullLogger<KubernetesReporterMachineReader>.Instance))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new KubernetesReporterMachineReader(
            _kubernetesRepo, _machineRepo, _machineStateRepo, _signingKeyRepo, null!, TestQuotaPause.Nothing(),
            TestMetricsFactory.CreateQuotaPauseMetrics(), _time, NullLogger<KubernetesReporterMachineReader>.Instance))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new KubernetesReporterMachineReader(
            _kubernetesRepo, _machineRepo, _machineStateRepo, _signingKeyRepo, _pingService, null!,
            TestMetricsFactory.CreateQuotaPauseMetrics(), _time, NullLogger<KubernetesReporterMachineReader>.Instance))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new KubernetesReporterMachineReader(
            _kubernetesRepo, _machineRepo, _machineStateRepo, _signingKeyRepo, _pingService, TestQuotaPause.Nothing(),
            null!, _time, NullLogger<KubernetesReporterMachineReader>.Instance))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new KubernetesReporterMachineReader(
            _kubernetesRepo, _machineRepo, _machineStateRepo, _signingKeyRepo, _pingService, TestQuotaPause.Nothing(),
            TestMetricsFactory.CreateQuotaPauseMetrics(), null!, NullLogger<KubernetesReporterMachineReader>.Instance))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new KubernetesReporterMachineReader(
            _kubernetesRepo, _machineRepo, _machineStateRepo, _signingKeyRepo, _pingService, TestQuotaPause.Nothing(),
            TestMetricsFactory.CreateQuotaPauseMetrics(), _time, null!))
            .Throws<ArgumentNullException>();
    }
}
