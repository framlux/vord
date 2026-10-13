// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Kubernetes;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using System.Text.Json;

namespace Framlux.FleetManagement.Test.Services.Kubernetes;

/// <summary>
/// Tests for <see cref="KubernetesCommandEligibility"/>: which machines may be sent each of the three
/// cluster reporter commands, how the command type is matched, and which repository reads that costs.
/// </summary>
public sealed class KubernetesCommandEligibilityTests
{
    private const int TenantId = 5;
    private const long MachineId = 9;

    private const string Enrol = "k8s_reporter_enrol";
    private const string Unenrol = "k8s_reporter_unenrol";
    private const string Uninstall = "k8s_reporter_uninstall";

    private const string InvalidTypeMessage = "Invalid command type";
    private const string NoParametersMessage = "This command takes no parameters";
    private const string CannotEnrolMessage = "Only control-plane machines with an admin credential can enrol";
    private const string NotEnrolledMessage = "This machine is not an enrolled reporter";
    private const string UninstallNeedsEnrolmentMessage = "This host has remote commands disabled and never enrolled; enable remote commands to uninstall from it";
    private const string UninstallCopiedStateMessage = "This host has remote commands disabled and its reporter state was copied from another host; unenrol it, or enable remote commands to uninstall from it";

    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);

    private readonly IKubernetesInventoryRepository _repository = Substitute.For<IKubernetesInventoryRepository>();
    private readonly FakeTimeProvider _time = new(Now);

    private KubernetesCommandEligibility CreateEligibility()
    {
        return new KubernetesCommandEligibility(_repository, _time);
    }

    private static Machine BuildMachine(int tenantId = TenantId)
    {
        Machine machine = TestDataBuilder.BuildMachine(tenantId);
        machine.Id = MachineId;

        return machine;
    }

    private static string PayloadJson(
        bool controlPlane = true,
        bool adminCredentialPresent = true,
        bool reporterEnrolled = false,
        string distro = "k3s",
        string? reporterError = null)
    {
        KubernetesNodePayload payload = new()
        {
            Distro = distro,
            KubeletVersion = "v1.30.4+k3s1",
            NodeName = "cp-1",
            NodeNameSource = "certificate",
            HasKubelet = true,
            ControlPlane = controlPlane,
            CaFingerprint = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718",
            AdminCredentialPresent = adminCredentialPresent,
            ReporterEnrolled = reporterEnrolled,
            ReporterError = reporterError,
        };

        return JsonSerializer.Serialize(payload, JsonDefaults.SnakeCase);
    }

    private void GivenLatestPayload(string? payloadJson)
    {
        Dictionary<long, string> payloads = [];
        if (payloadJson != null)
        {
            payloads[MachineId] = payloadJson;
        }

        _repository.GetLatestKubernetesNodePayloadsAsync(
                TenantId, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(payloads);
    }

    private void GivenCandidate(bool isCandidate, string? reporterError = null)
    {
        KubernetesReporterCandidate? candidate = isCandidate
            ? new KubernetesReporterCandidate { TenantId = TenantId, ClusterId = 3, MachineId = MachineId, LastReportAt = Now, ReporterError = reporterError }
            : null;

        _repository.GetCandidateForMachineAsync(TenantId, MachineId, Arg.Any<CancellationToken>()).Returns(candidate);
    }

    private async Task<ServiceResult<bool>> CheckAsync(string commandType, string? commandParams = null, bool remoteCommandsEnabled = true)
    {
        return await CreateEligibility().CheckAsync(
            TenantId, BuildMachine(), commandType, commandParams, remoteCommandsEnabled, CancellationToken.None);
    }

    private async Task AssertRejectedAsync(ServiceResult<bool> result, string message)
    {
        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).IsEqualTo(message);
    }

    private async Task AssertNothingWasReadAsync()
    {
        await _repository.DidNotReceiveWithAnyArgs().GetLatestKubernetesNodePayloadsAsync(default, default!, default, default);
        await _repository.DidNotReceiveWithAnyArgs().GetCandidateForMachineAsync(default, default, default);
    }

    // ========== Constructor ==========

    [Test]
    public async Task Constructor_NullRepository_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            KubernetesCommandEligibility _ = new(null!, _time);

            return Task.CompletedTask;
        });

        await Assert.That(ex!.ParamName).IsEqualTo("repository");
    }

    [Test]
    public async Task Constructor_NullTimeProvider_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            KubernetesCommandEligibility _ = new(_repository, null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex!.ParamName).IsEqualTo("timeProvider");
    }

    // ========== Command types that are not Kubernetes commands ==========

    [Test]
    [Arguments("reboot")]
    [Arguments("kill_process")]
    [Arguments("kill_session")]
    [Arguments("check_updates")]
    [Arguments("install_updates")]
    [Arguments("REBOOT")]
    public async Task Check_CommandThatIsNotAKubernetesCommand_PassesWithoutAnyRead(string commandType)
    {
        // Intent: the existing commands are not this class's business. They pass untouched, including a
        // non-empty params string and any casing, and cost no database read.
        ServiceResult<bool> result = await CheckAsync(commandType, """{"pid":"1"}""");

        await Assert.That(result.IsSuccess).IsTrue();
        await AssertNothingWasReadAsync();
    }

    // ========== Type matching ==========

    [Test]
    [Arguments("K8S_REPORTER_ENROL")]
    [Arguments("k8s_reporter_Enrol")]
    [Arguments("K8s_Reporter_Unenrol")]
    [Arguments("k8s_reporter_UNINSTALL")]
    public async Task Check_NonLowercaseKubernetesType_IsRejectedAsAnInvalidTypeWithoutAnyRead(string commandType)
    {
        // Intent: the allowlist ignores case, so a mixed-case spelling of a Kubernetes command reaches
        // this check. It is refused rather than waved through as an unknown command, so the guard can
        // never be skipped by changing the case, even for a machine that would be eligible.
        GivenLatestPayload(PayloadJson());
        GivenCandidate(true);

        ServiceResult<bool> result = await CheckAsync(commandType);

        await AssertRejectedAsync(result, InvalidTypeMessage);
        await AssertNothingWasReadAsync();
    }

    // ========== Parameters ==========

    [Test]
    [Arguments(Enrol, null)]
    [Arguments(Enrol, "")]
    [Arguments(Enrol, "   ")]
    [Arguments(Enrol, "{}")]
    [Arguments(Enrol, " { } ")]
    [Arguments(Unenrol, null)]
    [Arguments(Unenrol, "{}")]
    [Arguments(Uninstall, null)]
    [Arguments(Uninstall, "{}")]
    public async Task Check_NullEmptyOrEmptyObjectParams_AreAccepted(string commandType, string? commandParams)
    {
        GivenLatestPayload(PayloadJson(reporterEnrolled: true));

        ServiceResult<bool> result = await CheckAsync(commandType, commandParams);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    [Arguments(Enrol, """{"node":"cp-1"}""")]
    [Arguments(Enrol, """{"a":null}""")]
    [Arguments(Enrol, "[]")]
    [Arguments(Enrol, "null")]
    [Arguments(Enrol, "\"x\"")]
    [Arguments(Enrol, "{")]
    [Arguments(Enrol, "not json")]
    [Arguments(Unenrol, """{"force":"true"}""")]
    [Arguments(Uninstall, """{"purge":"true"}""")]
    public async Task Check_AnyOtherParams_AreRejectedBeforeAnyRead(string commandType, string commandParams)
    {
        // Intent: the agent refuses a reporter command that carries parameters, so the server refuses
        // it first, and does so before spending a database read on an eligibility it would not use.
        GivenLatestPayload(PayloadJson(reporterEnrolled: true));
        GivenCandidate(true);

        ServiceResult<bool> result = await CheckAsync(commandType, commandParams);

        await AssertRejectedAsync(result, NoParametersMessage);
        await AssertNothingWasReadAsync();
    }

    // ========== Enrol ==========

    [Test]
    public async Task Enrol_ControlPlaneWithAdminCredential_IsEligible()
    {
        GivenLatestPayload(PayloadJson(controlPlane: true, adminCredentialPresent: true));

        ServiceResult<bool> result = await CheckAsync(Enrol);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(false, false)]
    public async Task Enrol_MissingControlPlaneOrAdminCredential_IsRejected(bool controlPlane, bool adminCredentialPresent)
    {
        GivenLatestPayload(PayloadJson(controlPlane: controlPlane, adminCredentialPresent: adminCredentialPresent));

        ServiceResult<bool> result = await CheckAsync(Enrol);

        await AssertRejectedAsync(result, CannotEnrolMessage);
    }

    [Test]
    public async Task Enrol_NoRecentLocalReport_IsRejected()
    {
        GivenLatestPayload(null);

        ServiceResult<bool> result = await CheckAsync(Enrol);

        await AssertRejectedAsync(result, CannotEnrolMessage);
    }

    [Test]
    [Arguments("not json at all")]
    [Arguments("null")]
    [Arguments("")]
    public async Task Enrol_UnreadableReport_IsRejectedAsIfThereWereNone(string payloadJson)
    {
        // Intent: a stored report that cannot be read must not throw out of the request, and must not
        // count as evidence that the machine is eligible.
        GivenLatestPayload(payloadJson);

        ServiceResult<bool> result = await CheckAsync(Enrol);

        await AssertRejectedAsync(result, CannotEnrolMessage);
    }

    [Test]
    public async Task Enrol_ReportThatNamesNoDistro_IsRejectedAsIfThereWereNone()
    {
        // Intent: a report the inventory projection would refuse is not trusted for a command either,
        // because the two share one definition of a usable report.
        GivenLatestPayload(PayloadJson(distro: ""));

        ServiceResult<bool> result = await CheckAsync(Enrol);

        await AssertRejectedAsync(result, CannotEnrolMessage);
    }

    [Test]
    public async Task Enrol_AlreadyEnrolledControlPlane_IsStillEligible()
    {
        // Intent: enrol is idempotent on the agent, so re-running it to repair an install is allowed.
        GivenLatestPayload(PayloadJson(reporterEnrolled: true));

        ServiceResult<bool> result = await CheckAsync(Enrol);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    // ========== Uninstall ==========

    [Test]
    public async Task Uninstall_ControlPlaneWithAdminCredentialAndNoCandidateRow_IsEligible()
    {
        // Intent: uninstall is idempotent and label-checked, so any eligible control-plane host may run
        // it, which lets it clean up an orphaned install whose server-side state was already collected.
        GivenLatestPayload(PayloadJson(reporterEnrolled: false));
        GivenCandidate(false);

        ServiceResult<bool> result = await CheckAsync(Uninstall);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task Uninstall_MissingControlPlaneOrAdminCredential_IsRejectedWithTheEnrolReason(bool controlPlane, bool adminCredentialPresent)
    {
        // Intent: uninstall needs the same admin credential enrol does, so it is refused for the same
        // reason even when the machine is a known candidate.
        GivenLatestPayload(PayloadJson(controlPlane: controlPlane, adminCredentialPresent: adminCredentialPresent, reporterEnrolled: true));
        GivenCandidate(true);

        ServiceResult<bool> result = await CheckAsync(Uninstall);

        await AssertRejectedAsync(result, CannotEnrolMessage);
    }

    [Test]
    public async Task Uninstall_NoRecentLocalReport_IsRejected()
    {
        GivenLatestPayload(null);
        GivenCandidate(true);

        ServiceResult<bool> result = await CheckAsync(Uninstall);

        await AssertRejectedAsync(result, CannotEnrolMessage);
    }

    // ========== Uninstall on a host with remote commands off ==========

    [Test]
    public async Task Uninstall_RemoteCommandsOffAndNeverEnrolled_IsRejectedWithTheRemoteCommandsReason()
    {
        // Intent: the capability check does not apply to a cleanup command, so a host with remote commands
        // off can still be cleaned up, but only when it ever enrolled. A control plane that never did has
        // nothing of ours to remove, and sending it the command would be an uninstall from a host whose
        // owner switched remote commands off.
        GivenLatestPayload(PayloadJson(reporterEnrolled: false));
        GivenCandidate(false);

        ServiceResult<bool> result = await CheckAsync(Uninstall, remoteCommandsEnabled: false);

        await AssertRejectedAsync(result, UninstallNeedsEnrolmentMessage);
    }

    [Test]
    public async Task Uninstall_RemoteCommandsOffAndACandidateRow_IsEligible()
    {
        // Intent: a candidate row is evidence the host enrolled even when its latest report does not say
        // so, for instance because the agent was reconfigured after enrolling.
        GivenLatestPayload(PayloadJson(reporterEnrolled: false));
        GivenCandidate(true);

        ServiceResult<bool> result = await CheckAsync(Uninstall, remoteCommandsEnabled: false);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Uninstall_RemoteCommandsOffAndTheReportSaysEnrolled_IsEligibleWithoutLookingForACandidateRow()
    {
        GivenLatestPayload(PayloadJson(reporterEnrolled: true));

        ServiceResult<bool> result = await CheckAsync(Uninstall, remoteCommandsEnabled: false);

        await Assert.That(result.IsSuccess).IsTrue();
        await _repository.DidNotReceiveWithAnyArgs().GetCandidateForMachineAsync(default, default, default);
    }

    [Test]
    public async Task Uninstall_RemoteCommandsOffAndTheReportSaysEnrolledOnACopiedRecord_IsRejectedWithTheCopiedStateReason()
    {
        // Intent: a disk clone of an enrolled control plane reports itself enrolled with state_copied, but
        // it never enrolled itself, and its agent refuses an uninstall it holds no record of its own for.
        // The server must not hand it one, or the user is told the cleanup was accepted when it was not.
        GivenLatestPayload(PayloadJson(reporterEnrolled: true, reporterError: "state_copied"));
        GivenCandidate(true, "state_copied");

        ServiceResult<bool> result = await CheckAsync(Uninstall, remoteCommandsEnabled: false);

        await AssertRejectedAsync(result, UninstallCopiedStateMessage);
    }

    [Test]
    public async Task Uninstall_RemoteCommandsOffAndTheReportSaysCopiedWithOtherwiseValidEvidence_IsRejectedWithoutLookingForACandidateRow()
    {
        // Intent: the latest report is the host's own word on its state, so a copied record in it settles
        // the question whatever a candidate row says.
        GivenLatestPayload(PayloadJson(reporterEnrolled: true, reporterError: "state_copied"));
        GivenCandidate(true);

        ServiceResult<bool> result = await CheckAsync(Uninstall, remoteCommandsEnabled: false);

        await AssertRejectedAsync(result, UninstallCopiedStateMessage);
        await _repository.DidNotReceiveWithAnyArgs().GetCandidateForMachineAsync(default, default, default);
    }

    [Test]
    public async Task Uninstall_RemoteCommandsOffAndOnlyTheCandidateRowCarriesACopiedRecord_IsRejectedAsNeverEnrolled()
    {
        // Intent: a candidate row that still carries state_copied while the latest report no longer says the
        // host is enrolled is the copy being cleaned up. It is not evidence the host enrolled.
        GivenLatestPayload(PayloadJson(reporterEnrolled: false));
        GivenCandidate(true, "state_copied");

        ServiceResult<bool> result = await CheckAsync(Uninstall, remoteCommandsEnabled: false);

        await AssertRejectedAsync(result, UninstallNeedsEnrolmentMessage);
    }

    [Test]
    [Arguments("credentials_rejected")]
    [Arguments("tls_ca_changed")]
    public async Task Uninstall_RemoteCommandsOffAndTheHostReportsAnotherReporterError_IsStillEligible(string reporterError)
    {
        // Intent: only a copied record says the host never enrolled. A rejected credential or a changed CA
        // is a host that did enrol and is exactly what the cleanup bypass exists for.
        GivenLatestPayload(PayloadJson(reporterEnrolled: true, reporterError: reporterError));
        GivenCandidate(true, reporterError);

        ServiceResult<bool> result = await CheckAsync(Uninstall, remoteCommandsEnabled: false);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Uninstall_RemoteCommandsOnAndACopiedRecord_IsStillEligible()
    {
        // Intent: the copied record only matters while the capability check is skipped. With remote commands
        // on, uninstall works by label across the whole cluster whatever this host holds.
        GivenLatestPayload(PayloadJson(reporterEnrolled: true, reporterError: "state_copied"));
        GivenCandidate(true, "state_copied");

        ServiceResult<bool> result = await CheckAsync(Uninstall, remoteCommandsEnabled: true);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Uninstall_RemoteCommandsOnAndNeverEnrolled_IsStillEligible()
    {
        // Intent: with remote commands on nothing changes. Uninstall stays no stricter than enrol so it can
        // clean up an orphaned install whose server-side state was already collected, and it does not even
        // look for a candidate row.
        GivenLatestPayload(PayloadJson(reporterEnrolled: false));
        GivenCandidate(false);

        ServiceResult<bool> result = await CheckAsync(Uninstall, remoteCommandsEnabled: true);

        await Assert.That(result.IsSuccess).IsTrue();
        await _repository.DidNotReceiveWithAnyArgs().GetCandidateForMachineAsync(default, default, default);
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task Uninstall_RemoteCommandsOffAndNotAnEligibleControlPlane_IsRejectedWithTheEnrolReason(bool controlPlane, bool adminCredentialPresent)
    {
        // Intent: the credential rule comes first, so an enrolled candidate that lacks an admin credential
        // still hears about the credential, not about remote commands.
        GivenLatestPayload(PayloadJson(controlPlane: controlPlane, adminCredentialPresent: adminCredentialPresent, reporterEnrolled: true));
        GivenCandidate(true);

        ServiceResult<bool> result = await CheckAsync(Uninstall, remoteCommandsEnabled: false);

        await AssertRejectedAsync(result, CannotEnrolMessage);
    }

    [Test]
    public async Task Enrol_RemoteCommandsOffAndNeverEnrolled_IsEligibleHere()
    {
        // Intent: the capability refusal for enrol belongs to the capability check, which names its own
        // reason. This check judges the machine's Kubernetes state only.
        GivenLatestPayload(PayloadJson(reporterEnrolled: false));
        GivenCandidate(false);

        ServiceResult<bool> result = await CheckAsync(Enrol, remoteCommandsEnabled: false);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Unenrol_RemoteCommandsOffAndNotEnrolled_IsRejected()
    {
        // Intent: unenrol always needed evidence of enrolment, whatever the host's remote commands switch.
        GivenLatestPayload(PayloadJson(reporterEnrolled: false));
        GivenCandidate(false);

        ServiceResult<bool> result = await CheckAsync(Unenrol, remoteCommandsEnabled: false);

        await AssertRejectedAsync(result, NotEnrolledMessage);
    }

    [Test]
    public async Task Unenrol_RemoteCommandsOffAndACandidateRow_IsEligible()
    {
        GivenLatestPayload(null);
        GivenCandidate(true);

        ServiceResult<bool> result = await CheckAsync(Unenrol, remoteCommandsEnabled: false);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    // ========== Unenrol ==========

    [Test]
    public async Task Unenrol_MachineWithACandidateRow_IsEligibleEvenWithoutAReport()
    {
        GivenLatestPayload(null);
        GivenCandidate(true);

        ServiceResult<bool> result = await CheckAsync(Unenrol);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Unenrol_ReportThatSaysEnrolled_IsEligibleWithoutACandidateRow()
    {
        GivenLatestPayload(PayloadJson(reporterEnrolled: true));
        GivenCandidate(false);

        ServiceResult<bool> result = await CheckAsync(Unenrol);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Unenrol_EnrolledReportOnAHostWithoutAnAdminCredential_IsEligible()
    {
        // Intent: unenrol only removes the host's own state, so it does not need the admin credential
        // that enrol and uninstall read.
        GivenLatestPayload(PayloadJson(controlPlane: false, adminCredentialPresent: false, reporterEnrolled: true));
        GivenCandidate(false);

        ServiceResult<bool> result = await CheckAsync(Unenrol);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Unenrol_RemoteCommandsOffAndACopiedRecord_IsEligible()
    {
        // Intent: removing the copy is the remedy for a cloned host, and the agent accepts the unenrol of a
        // copied record with remote commands off, so the server must send it.
        GivenLatestPayload(PayloadJson(reporterEnrolled: true, reporterError: "state_copied"));
        GivenCandidate(true, "state_copied");

        ServiceResult<bool> result = await CheckAsync(Unenrol, remoteCommandsEnabled: false);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Unenrol_CandidateRowThatCarriesACopiedRecordWithoutAReport_IsEligible()
    {
        GivenLatestPayload(null);
        GivenCandidate(true, "state_copied");

        ServiceResult<bool> result = await CheckAsync(Unenrol, remoteCommandsEnabled: false);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Unenrol_NeitherCandidateNorEnrolledReport_IsRejected()
    {
        GivenLatestPayload(PayloadJson(reporterEnrolled: false));
        GivenCandidate(false);

        ServiceResult<bool> result = await CheckAsync(Unenrol);

        await AssertRejectedAsync(result, NotEnrolledMessage);
    }

    [Test]
    public async Task Unenrol_NoReportAndNoCandidate_IsRejected()
    {
        GivenLatestPayload(null);
        GivenCandidate(false);

        ServiceResult<bool> result = await CheckAsync(Unenrol);

        await AssertRejectedAsync(result, NotEnrolledMessage);
    }

    [Test]
    public async Task Unenrol_ReportThatSaysEnrolled_DoesNotLookForACandidateRow()
    {
        GivenLatestPayload(PayloadJson(reporterEnrolled: true));

        await CheckAsync(Unenrol);

        await _repository.DidNotReceiveWithAnyArgs().GetCandidateForMachineAsync(default, default, default);
    }

    // ========== Reads ==========

    [Test]
    [Arguments(Enrol)]
    [Arguments(Unenrol)]
    [Arguments(Uninstall)]
    public async Task Check_ReadsOnlyTheTargetMachinesLatestLocalReportTheServerAcceptedInTheLastSevenDays(string commandType)
    {
        // Intent: the report comes from the single-type query for this one machine in this tenant,
        // never the all-types read, and only a report the server accepted in the last seven days counts.
        GivenLatestPayload(PayloadJson(reporterEnrolled: true));

        await CheckAsync(commandType);

        await _repository.Received(1).GetLatestKubernetesNodePayloadsAsync(
            TenantId,
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new[] { MachineId })),
            Now.AddDays(-7),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Check_WindowFollowsTheClock()
    {
        GivenLatestPayload(PayloadJson());
        _time.Advance(TimeSpan.FromDays(3));

        await CheckAsync(Enrol);

        await _repository.Received(1).GetLatestKubernetesNodePayloadsAsync(
            TenantId,
            Arg.Any<IReadOnlyCollection<long>>(),
            Now.AddDays(-4),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Check_CandidateIsLookedUpInTheCommandsTenantForTheTargetMachine()
    {
        GivenLatestPayload(null);
        GivenCandidate(true);

        await CheckAsync(Unenrol);

        await _repository.Received(1).GetCandidateForMachineAsync(TenantId, MachineId, Arg.Any<CancellationToken>());
    }

    // ========== Machine of another tenant ==========

    [Test]
    [Arguments(Enrol, CannotEnrolMessage)]
    [Arguments(Uninstall, CannotEnrolMessage)]
    [Arguments(Unenrol, NotEnrolledMessage)]
    public async Task Check_MachineOfAnotherTenant_IsRefusedWithoutAnyRead(string commandType, string message)
    {
        // Intent: the caller loads the machine for the command's tenant, so this should never happen, but
        // if it does nothing is read for the other tenant's machine, and even a report and a candidate row
        // that would make it eligible cannot let the command through.
        GivenLatestPayload(PayloadJson(reporterEnrolled: true));
        GivenCandidate(true);

        ServiceResult<bool> result = await CreateEligibility().CheckAsync(
            TenantId, BuildMachine(tenantId: TenantId + 1), commandType, null, true, CancellationToken.None);

        await AssertRejectedAsync(result, message);
        await AssertNothingWasReadAsync();
    }

    [Test]
    public async Task Check_MachineOfAnotherTenantAndACommandThatIsNotAKubernetesCommand_StillPasses()
    {
        // Intent: the tenant guard belongs to the Kubernetes commands only. The other commands are
        // not this class's business and keep their own checks in the service.
        ServiceResult<bool> result = await CreateEligibility().CheckAsync(
            TenantId, BuildMachine(tenantId: TenantId + 1), "reboot", null, true, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Check_NullMachine_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await CreateEligibility().CheckAsync(TenantId, null!, Enrol, null, true, CancellationToken.None));
    }

    [Test]
    public async Task Check_NullCommandType_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await CreateEligibility().CheckAsync(TenantId, BuildMachine(), null!, null, true, CancellationToken.None));
    }
}
