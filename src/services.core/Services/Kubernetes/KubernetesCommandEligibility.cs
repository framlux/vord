// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using System.Text.Json;

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// Decides whether a machine may be sent one of the cluster reporter commands, from its latest local
/// Kubernetes report (telemetry type 14) and its reporter candidate row.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Enrol and uninstall need a control-plane host that reports an admin credential. Uninstall is
/// idempotent and label-checked, so it deliberately asks no more than enrol: it can clean up an
/// orphaned install whose server-side state has already been collected.</item>
/// <item>Unenrol needs a machine that is a reporter candidate or whose latest report says it is enrolled.</item>
/// <item>Uninstall on a host whose agent has remote commands switched off needs that same evidence of
/// enrolment. The command skips the capability check so a host that enrolled can still be cleaned up, and
/// this keeps it from reaching a control plane whose owner switched commands off without ever enrolling.
/// A record copied from another host (<c>state_copied</c>) is evidence for unenrol but not for this: that
/// host never enrolled, and its agent refuses an uninstall it has no record of its own for.</item>
/// <item>All three take no parameters.</item>
/// </list>
/// </remarks>
public sealed class KubernetesCommandEligibility : IKubernetesCommandEligibility
{
    private const string InvalidCommandTypeMessage = "Invalid command type";
    private const string NoParametersMessage = "This command takes no parameters";
    private const string CannotEnrolMessage = "Only control-plane machines with an admin credential can enrol";
    private const string NotEnrolledMessage = "This machine is not an enrolled reporter";
    private const string UninstallNeedsEnrolmentMessage = "This host has remote commands disabled and never enrolled; enable remote commands to uninstall from it";
    private const string UninstallCopiedStateMessage = "This host has remote commands disabled and its reporter state was copied from another host; unenrol it, or enable remote commands to uninstall from it";
    private const string StateCopiedError = "state_copied";

    /// <summary>
    /// How old a local report may be and still describe the machine. It is the window the machine page
    /// reads its latest report from.
    /// </summary>
    internal static readonly TimeSpan ReportWindow = TimeSpan.FromDays(7);

    private static readonly string[] KubernetesTypes =
    [
        KubernetesCommandTypes.Enrol,
        KubernetesCommandTypes.Unenrol,
        KubernetesCommandTypes.Uninstall,
    ];

    private readonly IKubernetesInventoryRepository _repository;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="KubernetesCommandEligibility"/> class.
    /// </summary>
    /// <param name="repository">The Kubernetes inventory repository</param>
    /// <param name="timeProvider">The clock the report window is measured from</param>
    public KubernetesCommandEligibility(IKubernetesInventoryRepository repository, TimeProvider timeProvider)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<bool>> CheckAsync(
        int tenantId, Machine machine, string commandType, string? commandParams, bool remoteCommandsEnabled, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(commandType);

        // Matching uses the allowlist's own comparer, so a spelling the allowlist accepts as one of these
        // types can never be one this class fails to recognise.
        string? kubernetesType = KubernetesTypes.FirstOrDefault(t => string.Equals(t, commandType, StringComparison.OrdinalIgnoreCase));
        if (kubernetesType is null)
        {
            return ServiceResult<bool>.Ok(true);
        }

        // The agent matches the type exactly, so only the lowercase spelling is a command it will run.
        // Refusing every other casing here means the checks below cannot be skipped by changing it.
        if (string.Equals(kubernetesType, commandType, StringComparison.Ordinal) == false)
        {
            return ServiceResult<bool>.BadRequest(InvalidCommandTypeMessage);
        }

        if (ParametersAreEmpty(commandParams) == false)
        {
            return ServiceResult<bool>.BadRequest(NoParametersMessage);
        }

        bool isUnenrol = string.Equals(kubernetesType, KubernetesCommandTypes.Unenrol, StringComparison.Ordinal);

        // The caller loads the machine for the command's tenant, so a machine of another tenant should never
        // arrive. If one does, nothing about it is read and it is refused as a machine that has no report and
        // no reporter state.
        if (machine.TenantId != tenantId)
        {
            return ServiceResult<bool>.BadRequest(isUnenrol ? NotEnrolledMessage : CannotEnrolMessage);
        }

        KubernetesNodePayload? report = await GetLatestReportAsync(tenantId, machine.Id, ct);

        if (isUnenrol)
        {
            return await CheckUnenrolAsync(tenantId, machine.Id, report, ct);
        }

        if (CanEnrol(report) == false)
        {
            return ServiceResult<bool>.BadRequest(CannotEnrolMessage);
        }

        // The capability check does not apply to uninstall, so this is the only place a host whose remote
        // commands are off is asked whether it has anything of ours to remove.
        bool isUninstall = string.Equals(kubernetesType, KubernetesCommandTypes.Uninstall, StringComparison.Ordinal);
        if (isUninstall && (remoteCommandsEnabled == false))
        {
            // Only a record this host wrote itself makes it a host that enrolled. The agent accepts the
            // cleanup with remote commands off on that basis alone, and refuses an uninstall on a copy.
            bool copied = IsCopiedState(report?.ReporterError);
            if (copied)
            {
                return ServiceResult<bool>.BadRequest(UninstallCopiedStateMessage);
            }

            bool enrolled = await HasEnrolmentEvidenceAsync(tenantId, machine.Id, report, ignoreCopiedState: true, ct);
            if (enrolled == false)
            {
                return ServiceResult<bool>.BadRequest(UninstallNeedsEnrolmentMessage);
            }
        }

        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>
    /// Reports whether a machine's latest report makes it eligible to enrol (and so to uninstall): it runs a
    /// control plane and holds a cluster-admin credential. The cluster page lists machines by the same rule.
    /// </summary>
    /// <param name="report">The machine's normalised latest report, or null when it has none.</param>
    internal static bool CanEnrol(KubernetesNodePayload? report)
    {
        return (report is not null) && report.ControlPlane && report.AdminCredentialPresent;
    }

    private async Task<ServiceResult<bool>> CheckUnenrolAsync(
        int tenantId, long machineId, KubernetesNodePayload? report, CancellationToken ct)
    {
        if (await HasEnrolmentEvidenceAsync(tenantId, machineId, report, ignoreCopiedState: false, ct))
        {
            return ServiceResult<bool>.Ok(true);
        }

        return ServiceResult<bool>.BadRequest(NotEnrolledMessage);
    }

    /// <summary>
    /// Reports whether the machine ever enrolled as a reporter: its latest report says it is enrolled, or it
    /// has a candidate row. The report is tried first because it costs no further read. A host that holds a
    /// record copied from another host reports itself enrolled and keeps a candidate row, so
    /// <paramref name="ignoreCopiedState"/> says whether those two count: unenrol removes the copy and
    /// accepts them, while an uninstall must not treat the copy as the host's own enrolment.
    /// </summary>
    private async Task<bool> HasEnrolmentEvidenceAsync(
        int tenantId, long machineId, KubernetesNodePayload? report, bool ignoreCopiedState, CancellationToken ct)
    {
        if ((report is not null) && report.ReporterEnrolled && CountsAsEvidence(report.ReporterError, ignoreCopiedState))
        {
            return true;
        }

        KubernetesReporterCandidate? candidate = await _repository.GetCandidateForMachineAsync(tenantId, machineId, ct);

        return (candidate is not null) && CountsAsEvidence(candidate.ReporterError, ignoreCopiedState);
    }

    private static bool CountsAsEvidence(string? reporterError, bool ignoreCopiedState)
    {
        return (ignoreCopiedState == false) || (IsCopiedState(reporterError) == false);
    }

    private static bool IsCopiedState(string? reporterError)
    {
        return string.Equals(reporterError, StateCopiedError, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the machine's latest local report that the server accepted in the last seven days through the
    /// single-type query, and normalises it the way the inventory projection does. A missing report, one
    /// that cannot be read and one the projection would refuse all count as no report.
    /// </summary>
    private async Task<KubernetesNodePayload?> GetLatestReportAsync(int tenantId, long machineId, CancellationToken ct)
    {
        DateTimeOffset serverReceivedAfter = _timeProvider.GetUtcNow() - ReportWindow;
        IReadOnlyDictionary<long, string> payloads = await _repository.GetLatestKubernetesNodePayloadsAsync(
            tenantId, [machineId], serverReceivedAfter, ct);

        if (payloads.TryGetValue(machineId, out string? json) == false)
        {
            return null;
        }

        return KubernetesNodePayloadNormaliser.NormaliseJson(json);
    }

    /// <summary>
    /// Reports whether the parameters are absent, blank or an empty JSON object. Anything else, including
    /// text that is not JSON, carries parameters.
    /// </summary>
    private static bool ParametersAreEmpty(string? commandParams)
    {
        if (string.IsNullOrWhiteSpace(commandParams))
        {
            return true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(commandParams);

            return (document.RootElement.ValueKind == JsonValueKind.Object) && (document.RootElement.GetPropertyCount() == 0);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
