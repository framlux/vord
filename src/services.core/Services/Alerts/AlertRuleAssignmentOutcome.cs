// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// What an alert-rule assignment request resolved to. The caller maps these onto HTTP status codes;
/// the decision itself is taken in the assignment service so both directions of the rule/machine
/// relationship reach the same answer.
/// </summary>
public enum AlertRuleAssignmentOutcome
{
    /// <summary>The assignment set was written.</summary>
    Applied,

    /// <summary>The rule or machine named by the request does not exist within the tenant.</summary>
    NotFound,

    /// <summary>One or more machine IDs in the request do not name an active machine of the tenant.</summary>
    InvalidMachineIds,

    /// <summary>
    /// The request would have put a custom rule into service for a tenant that is not on Team.
    /// </summary>
    CustomRuleRequiresTeam,
}
