// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// The result of replacing the set of machines a rule watches.
/// </summary>
/// <remarks>
/// The applied set is reported back rather than assumed to equal the request, because machines the
/// caller was never offered are carried through untouched. A response or an audit entry that echoed
/// the request would then record a removal that never happened.
/// </remarks>
public sealed class RuleMachineAssignmentResult
{
    /// <summary>What the request resolved to.</summary>
    public AlertRuleAssignmentOutcome Outcome { get; init; }

    /// <summary>The machine IDs actually written, empty unless <see cref="Outcome"/> is Applied.</summary>
    public IReadOnlyList<long> AppliedMachineIds { get; init; } = [];
}
