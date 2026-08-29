// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// The result of replacing the set of rules a machine is watched by.
/// </summary>
/// <remarks>
/// The applied set is reported back rather than assumed to equal the request, because below Team the
/// machine's frozen custom assignments are carried through untouched. An audit entry that echoed the
/// request would then record a removal that never happened.
/// </remarks>
public sealed class MachineRuleAssignmentResult
{
    /// <summary>What the request resolved to.</summary>
    public AlertRuleAssignmentOutcome Outcome { get; init; }

    /// <summary>The rule IDs actually written, empty unless <see cref="Outcome"/> is Applied.</summary>
    public IReadOnlyList<int> AppliedRuleIds { get; init; } = [];
}
