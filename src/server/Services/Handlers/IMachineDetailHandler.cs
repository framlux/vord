// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles machine detail operations.
/// </summary>
public interface IMachineDetailHandler
{
    /// <summary>
    /// Gets the basic detail for a machine.
    /// </summary>
    Task<ServiceResult<MachineDto>> GetDetailAsync(long machineId, int? tenantId, CancellationToken ct);

    /// <summary>
    /// Gets the full detail for a machine (delegates to IMachineStateService).
    /// </summary>
    Task<ServiceResult<MachineDetailDto>> GetFullDetailAsync(long machineId, int? tenantId, CancellationToken ct);

    /// <summary>
    /// Gets the online/offline status for a machine.
    /// </summary>
    Task<ServiceResult<MachineStatusDto>> GetStatusAsync(long machineId, int? tenantId, CancellationToken ct);

    /// <summary>
    /// Gets paginated telemetry records for a machine.
    /// </summary>
    Task<ServiceResult<PaginatedResponse<MachineTelemetryDto>>> GetTelemetryAsync(long machineId, int? tenantId, int page, int pageSize, short? typeFilter, CancellationToken ct);

    /// <summary>
    /// Gets the latest telemetry record per type for a machine.
    /// </summary>
    Task<ServiceResult<List<MachineTelemetryDto>>> GetLatestTelemetryAsync(long machineId, int? tenantId, CancellationToken ct);

    /// <summary>
    /// Gets paginated certificates for a machine.
    /// </summary>
    Task<ServiceResult<PaginatedResponse<MachineCertificateDto>>> GetCertificatesAsync(long machineId, int? tenantId, int page, int pageSize, CancellationToken ct);
}
