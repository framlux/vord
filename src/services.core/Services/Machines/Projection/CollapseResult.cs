// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>The result of collapsing a telemetry batch: one patch per machine plus skipped rows.</summary>
/// <param name="Patches">One collapsed patch per machine present in the batch.</param>
/// <param name="Skipped">The rows whose payloads could not be parsed and were skipped.</param>
internal sealed record CollapseResult(
    IReadOnlyList<MachineStatePatch> Patches,
    IReadOnlyList<SkippedTelemetryRow> Skipped);
