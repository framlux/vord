// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>A batch row that could not be parsed and was skipped during collapse.</summary>
/// <param name="MachineId">The machine the skipped row belonged to.</param>
/// <param name="TelemetryType">The telemetry type identifier of the skipped row.</param>
/// <param name="RowId">The raw telemetry row identifier that was skipped.</param>
internal readonly record struct SkippedTelemetryRow(long MachineId, short TelemetryType, long RowId);
