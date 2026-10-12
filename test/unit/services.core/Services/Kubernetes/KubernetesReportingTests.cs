// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Kubernetes;

namespace Framlux.FleetManagement.Test.Services.Kubernetes;

/// <summary>
/// Tests for <see cref="KubernetesReporting"/>: the reporting state a cluster is shown in, its
/// staleness, and the reporter error it reports.
/// </summary>
public class KubernetesReportingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static KubernetesCluster Cluster(
        string? reporterError = null,
        bool roleOutdated = false,
        DateTimeOffset? lastSnapshotReceivedAt = null)
    {
        return new KubernetesCluster
        {
            Id = 7,
            TenantId = 1,
            CaFingerprint = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718",
            Distro = "k3s",
            FirstSeenAt = Now.AddDays(-1),
            LastSeenAt = Now,
            ReporterError = reporterError,
            RoleOutdated = roleOutdated,
            LastSnapshotReceivedAt = lastSnapshotReceivedAt,
        };
    }

    private static KubernetesReporterCandidate Candidate(long machineId, string? reporterError = null)
    {
        return new KubernetesReporterCandidate
        {
            TenantId = 1,
            ClusterId = 7,
            MachineId = machineId,
            ReporterError = reporterError,
            LastReportAt = Now,
        };
    }

    // ========== Derive ==========

    [Test]
    public async Task Derive_NoCandidates_IsOffWhateverElseIsRecorded()
    {
        // Intent: with nobody enrolled there is no reporter to be in error, outdated or stale, so the
        // leftovers of an earlier reporter never make a cluster look as if reporting were on.
        KubernetesCluster cluster = Cluster("credentials_rejected", roleOutdated: true, lastSnapshotReceivedAt: Now.AddDays(-3));

        ReportingState state = KubernetesReporting.Derive(cluster, [], Now);

        await Assert.That(state).IsEqualTo(ReportingState.Off);
    }

    [Test]
    public async Task Derive_ClusterReporterError_IsError()
    {
        KubernetesCluster cluster = Cluster("api_unreachable", lastSnapshotReceivedAt: Now);

        ReportingState state = KubernetesReporting.Derive(cluster, [Candidate(1)], Now);

        await Assert.That(state).IsEqualTo(ReportingState.Error);
    }

    [Test]
    public async Task Derive_AnyCandidateReporterError_IsError()
    {
        // Intent: a candidate that cannot use its credentials is reported by its own local report, not
        // by a snapshot, so the cluster is in error although the holder is healthy.
        KubernetesCluster cluster = Cluster(lastSnapshotReceivedAt: Now);

        ReportingState state = KubernetesReporting.Derive(cluster, [Candidate(1), Candidate(2, "tls_ca_changed")], Now);

        await Assert.That(state).IsEqualTo(ReportingState.Error);
    }

    [Test]
    public async Task Derive_BlankCandidateReporterError_IsNotAnError()
    {
        KubernetesCluster cluster = Cluster(lastSnapshotReceivedAt: Now);

        ReportingState state = KubernetesReporting.Derive(cluster, [Candidate(1, "")], Now);

        await Assert.That(state).IsEqualTo(ReportingState.Active);
    }

    [Test]
    public async Task Derive_ErrorAndRoleOutdatedAndStale_ErrorWins()
    {
        KubernetesCluster cluster = Cluster("credentials_rejected", roleOutdated: true, lastSnapshotReceivedAt: Now.AddHours(-5));

        ReportingState state = KubernetesReporting.Derive(cluster, [Candidate(1)], Now);

        await Assert.That(state).IsEqualTo(ReportingState.Error);
    }

    [Test]
    public async Task Derive_RoleOutdatedAndStale_RoleOutdatedWins()
    {
        KubernetesCluster cluster = Cluster(roleOutdated: true, lastSnapshotReceivedAt: Now.AddHours(-5));

        ReportingState state = KubernetesReporting.Derive(cluster, [Candidate(1)], Now);

        await Assert.That(state).IsEqualTo(ReportingState.RoleOutdated);
    }

    [Test]
    public async Task Derive_CandidatesButNoSnapshotYet_IsStale()
    {
        // Intent: an enrolled cluster that has never delivered a snapshot has nothing fresh to show.
        KubernetesCluster cluster = Cluster();

        ReportingState state = KubernetesReporting.Derive(cluster, [Candidate(1)], Now);

        await Assert.That(state).IsEqualTo(ReportingState.Stale);
    }

    [Test]
    public async Task Derive_SnapshotOlderThanTheThreshold_IsStale()
    {
        KubernetesCluster cluster = Cluster(lastSnapshotReceivedAt: Now - KubernetesReportingPolicy.StaleThreshold - TimeSpan.FromSeconds(1));

        ReportingState state = KubernetesReporting.Derive(cluster, [Candidate(1)], Now);

        await Assert.That(state).IsEqualTo(ReportingState.Stale);
    }

    [Test]
    public async Task Derive_SnapshotExactlyAtTheThreshold_IsStillActive()
    {
        // Intent: stale means older than the threshold, the same boundary garbage collection uses to
        // decide a cluster has stopped reporting, so the two never disagree about one instant.
        KubernetesCluster cluster = Cluster(lastSnapshotReceivedAt: Now - KubernetesReportingPolicy.StaleThreshold);

        ReportingState state = KubernetesReporting.Derive(cluster, [Candidate(1)], Now);

        await Assert.That(state).IsEqualTo(ReportingState.Active);
    }

    [Test]
    public async Task Derive_FreshSnapshotAndNoProblems_IsActive()
    {
        KubernetesCluster cluster = Cluster(lastSnapshotReceivedAt: Now.AddMinutes(-5));

        ReportingState state = KubernetesReporting.Derive(cluster, [Candidate(1), Candidate(2)], Now);

        await Assert.That(state).IsEqualTo(ReportingState.Active);
    }

    [Test]
    public async Task Derive_UsesTheSharedNinetyMinuteThreshold()
    {
        // Intent: the staleness threshold is defined once, in the database project, and is 90 minutes.
        await Assert.That(KubernetesReportingPolicy.StaleThreshold).IsEqualTo(TimeSpan.FromMinutes(90));

        KubernetesCluster justInside = Cluster(lastSnapshotReceivedAt: Now.AddMinutes(-89));
        KubernetesCluster justOutside = Cluster(lastSnapshotReceivedAt: Now.AddMinutes(-91));

        await Assert.That(KubernetesReporting.Derive(justInside, [Candidate(1)], Now)).IsEqualTo(ReportingState.Active);
        await Assert.That(KubernetesReporting.Derive(justOutside, [Candidate(1)], Now)).IsEqualTo(ReportingState.Stale);
    }

    [Test]
    public async Task Derive_NullArguments_Throw()
    {
        await Assert.That(() => KubernetesReporting.Derive(null!, [], Now)).Throws<ArgumentNullException>();
        await Assert.That(() => KubernetesReporting.Derive(Cluster(), null!, Now)).Throws<ArgumentNullException>();
    }

    // ========== ToWireValue ==========

    [Test]
    [Arguments(ReportingState.Off, "off")]
    [Arguments(ReportingState.Error, "error")]
    [Arguments(ReportingState.RoleOutdated, "role_outdated")]
    [Arguments(ReportingState.Stale, "stale")]
    [Arguments(ReportingState.Active, "active")]
    public async Task ToWireValue_MapsEachStateToItsContractValue(ReportingState state, string expected)
    {
        await Assert.That(KubernetesReporting.ToWireValue(state)).IsEqualTo(expected);
    }

    // ========== IsStale ==========

    [Test]
    public async Task IsStale_NoCandidates_IsFalseEvenWithAnOldSnapshot()
    {
        await Assert.That(KubernetesReporting.IsStale(Cluster(lastSnapshotReceivedAt: Now.AddDays(-2)), [], Now)).IsFalse();
    }

    [Test]
    public async Task IsStale_IsIndependentOfErrorsSoNodeValuesCanBeShownAsOfATime()
    {
        // Intent: an error or an outdated role takes the state's name, but the nodes' values are still
        // as old as the snapshot they came from, so staleness is judged on its own.
        KubernetesCluster cluster = Cluster("credentials_rejected", roleOutdated: true, lastSnapshotReceivedAt: Now.AddHours(-3));

        await Assert.That(KubernetesReporting.IsStale(cluster, [Candidate(1)], Now)).IsTrue();
    }

    [Test]
    public async Task IsStale_FreshSnapshot_IsFalse()
    {
        await Assert.That(KubernetesReporting.IsStale(Cluster(lastSnapshotReceivedAt: Now.AddMinutes(-1)), [Candidate(1)], Now)).IsFalse();
    }

    // ========== ReporterErrorOf ==========

    [Test]
    public async Task ReporterErrorOf_NoCandidates_IsNullEvenWhenTheClusterRecordsOne()
    {
        // Intent: the reported error is non-null exactly when the state is error, and nothing is in
        // error while nobody is enrolled.
        string? error = KubernetesReporting.ReporterErrorOf(Cluster("api_unreachable"), [], new Dictionary<long, string>());

        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task ReporterErrorOf_ClusterError_WinsOverCandidateErrors()
    {
        Dictionary<long, string> names = new() { [1] = "alpha", [2] = "beta" };

        string? error = KubernetesReporting.ReporterErrorOf(
            Cluster("namespace_terminating"), [Candidate(1, "credentials_rejected"), Candidate(2)], names);

        await Assert.That(error).IsEqualTo("namespace_terminating");
    }

    [Test]
    public async Task ReporterErrorOf_CandidateErrorsOnly_ReportsTheFirstByMachineName()
    {
        // Intent: with several failing candidates the answer must not depend on row order, so it is the
        // error of the machine whose name sorts first. Ids are in the opposite order to the names.
        Dictionary<long, string> names = new() { [10] = "zulu", [20] = "alpha", [30] = "mike" };

        string? error = KubernetesReporting.ReporterErrorOf(
            Cluster(), [Candidate(10, "tls_ca_changed"), Candidate(20, "credentials_rejected"), Candidate(30)], names);

        await Assert.That(error).IsEqualTo("credentials_rejected");
    }

    [Test]
    public async Task ReporterErrorOf_NamesComparedOrdinally()
    {
        // Intent: names compare ordinally, as the cluster name derivation does, so every replica picks
        // the same machine. In ordinal order "Zulu" sorts before "alpha".
        Dictionary<long, string> names = new() { [1] = "alpha", [2] = "Zulu" };

        string? error = KubernetesReporting.ReporterErrorOf(
            Cluster(), [Candidate(1, "credentials_rejected"), Candidate(2, "tls_ca_changed")], names);

        await Assert.That(error).IsEqualTo("tls_ca_changed");
    }

    [Test]
    public async Task ReporterErrorOf_SameNameOrMissingName_FallsBackToTheMachineId()
    {
        Dictionary<long, string> names = new() { [5] = "same", [6] = "same" };

        string? sameName = KubernetesReporting.ReporterErrorOf(
            Cluster(), [Candidate(6, "tls_ca_changed"), Candidate(5, "credentials_rejected")], names);
        string? noNames = KubernetesReporting.ReporterErrorOf(
            Cluster(), [Candidate(6, "tls_ca_changed"), Candidate(5, "credentials_rejected")], new Dictionary<long, string>());

        await Assert.That(sameName).IsEqualTo("credentials_rejected");
        await Assert.That(noNames).IsEqualTo("credentials_rejected");
    }

    [Test]
    public async Task ReporterErrorOf_NoErrorAnywhere_IsNull()
    {
        string? error = KubernetesReporting.ReporterErrorOf(Cluster(), [Candidate(1), Candidate(2, "")], new Dictionary<long, string>());

        await Assert.That(error).IsNull();
    }
}
