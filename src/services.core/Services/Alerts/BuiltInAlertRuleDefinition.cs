// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// One built-in alert rule, as shipped. Metric is the identity: a tenant holds at most one built-in
/// rule per metric, which is what makes provisioning idempotent without a surrogate key.
/// </summary>
/// <param name="Name">The rule name as it appears in the user interface.</param>
/// <param name="Metric">The metric evaluated, and the rule's identity within a tenant.</param>
/// <param name="Operator">The comparison applied to the metric value.</param>
/// <param name="Threshold">The value compared against.</param>
/// <param name="DurationMinutes">How long the condition must hold before the rule fires.</param>
/// <param name="Severity">The severity carried by alerts this rule raises.</param>
public sealed record BuiltInAlertRuleDefinition(
    string Name,
    AlertMetric Metric,
    AlertOperator Operator,
    decimal Threshold,
    int DurationMinutes,
    AlertSeverity Severity);
