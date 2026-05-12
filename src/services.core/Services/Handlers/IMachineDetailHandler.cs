// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;

namespace Framlux.FleetManagement.Services.Core.Handlers;

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
}
