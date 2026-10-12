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

    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);

    private readonly IKubernetesInventoryRepository _repository = Substitute.For<IKubernetesInventoryRepository>();
    private readonly FakeTimeProvider _time = new(Now);

    private KubernetesCommandEligibility CreateEligibility()
    {
        return new KubernetesCommandEligibility(_repository, _time);
    }

    private static Machine BuildMachine()
    {
        Machine machine = TestDataBuilder.BuildMachine(TenantId);
        machine.Id = MachineId;

        return machine;
    }

    private static string PayloadJson(
        bool controlPlane = true,
        bool adminCredentialPresent = true,
        bool reporterEnrolled = false,
        string distro = "k3s")
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

    private void GivenCandidate(bool isCandidate)
    {
        KubernetesReporterCandidate? candidate = isCandidate
            ? new KubernetesReporterCandidate { TenantId = TenantId, ClusterId = 3, MachineId = MachineId, LastReportAt = Now }
            : null;

        _repository.GetCandidateForMachineAsync(TenantId, MachineId, Arg.Any<CancellationToken>()).Returns(candidate);
    }

    private async Task<ServiceResult<bool>> CheckAsync(string commandType, string? commandParams = null)
    {
        return await CreateEligibility().CheckAsync(TenantId, BuildMachine(), commandType, commandParams, CancellationToken.None);
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
    public async Task Check_ReadsOnlyTheTargetMachinesLatestLocalReportFromTheLastSevenDays(string commandType)
    {
        // Intent: the report comes from the single-type query for this one machine in this tenant,
        // never the all-types read, and only a report received in the last seven days counts.
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

    [Test]
    public async Task Check_NullMachine_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await CreateEligibility().CheckAsync(TenantId, null!, Enrol, null, CancellationToken.None));
    }

    [Test]
    public async Task Check_NullCommandType_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await CreateEligibility().CheckAsync(TenantId, BuildMachine(), null!, null, CancellationToken.None));
    }
}
