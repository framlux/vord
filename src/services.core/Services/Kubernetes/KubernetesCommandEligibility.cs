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
/// <item>All three take no parameters.</item>
/// </list>
/// </remarks>
public sealed class KubernetesCommandEligibility : IKubernetesCommandEligibility
{
    private const string InvalidCommandTypeMessage = "Invalid command type";
    private const string NoParametersMessage = "This command takes no parameters";
    private const string CannotEnrolMessage = "Only control-plane machines with an admin credential can enrol";
    private const string NotEnrolledMessage = "This machine is not an enrolled reporter";

    /// <summary>
    /// How old a local report may be and still describe the machine. It is the window the machine page
    /// reads its latest report from.
    /// </summary>
    private static readonly TimeSpan ReportWindow = TimeSpan.FromDays(7);

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
        int tenantId, Machine machine, string commandType, string? commandParams, CancellationToken ct)
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

        KubernetesNodePayload? report = await GetLatestReportAsync(tenantId, machine.Id, ct);

        if (string.Equals(kubernetesType, KubernetesCommandTypes.Unenrol, StringComparison.Ordinal))
        {
            return await CheckUnenrolAsync(tenantId, machine.Id, report, ct);
        }

        if ((report is not null) && report.ControlPlane && report.AdminCredentialPresent)
        {
            return ServiceResult<bool>.Ok(true);
        }

        return ServiceResult<bool>.BadRequest(CannotEnrolMessage);
    }

    private async Task<ServiceResult<bool>> CheckUnenrolAsync(
        int tenantId, long machineId, KubernetesNodePayload? report, CancellationToken ct)
    {
        if ((report is not null) && report.ReporterEnrolled)
        {
            return ServiceResult<bool>.Ok(true);
        }

        KubernetesReporterCandidate? candidate = await _repository.GetCandidateForMachineAsync(tenantId, machineId, ct);
        if (candidate is not null)
        {
            return ServiceResult<bool>.Ok(true);
        }

        return ServiceResult<bool>.BadRequest(NotEnrolledMessage);
    }

    /// <summary>
    /// Reads the machine's latest local report from the last seven days through the single-type query, and
    /// normalises it the way the inventory projection does. A missing report, one that cannot be read and
    /// one the projection would refuse all count as no report.
    /// </summary>
    private async Task<KubernetesNodePayload?> GetLatestReportAsync(int tenantId, long machineId, CancellationToken ct)
    {
        DateTimeOffset receivedAfter = _timeProvider.GetUtcNow() - ReportWindow;
        IReadOnlyDictionary<long, string> payloads = await _repository.GetLatestKubernetesNodePayloadsAsync(
            tenantId, [machineId], receivedAfter, ct);

        if (payloads.TryGetValue(machineId, out string? json) == false)
        {
            return null;
        }

        KubernetesNodePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<KubernetesNodePayload>(json, JsonDefaults.SnakeCase);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null)
        {
            return null;
        }

        return KubernetesNodePayloadNormaliser.Normalise(payload);
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
