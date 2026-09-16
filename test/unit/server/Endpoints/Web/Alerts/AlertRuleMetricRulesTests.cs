// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Alerts;

/// <summary>
/// Unit tests for <see cref="AlertRuleMetricRules"/>, the shared metric constraint rules used by
/// the create validator, the update validator and the update endpoint's database-loaded
/// re-validation.
/// </summary>
public sealed class AlertRuleMetricRulesTests
{
    /// <summary>
    /// SshConnection is point-in-time, so a window is meaningless and only zero is accepted.
    /// </summary>
    [Test]
    public async Task ValidateDurationForMetric_SshConnection_AcceptsOnlyZero()
    {
        await Assert.That(AlertRuleMetricRules.ValidateDurationForMetric(AlertMetric.SshConnection, 0)).IsTrue();
        await Assert.That(AlertRuleMetricRules.ValidateDurationForMetric(AlertMetric.SshConnection, 1)).IsFalse();
    }

    /// <summary>
    /// FailedSshLogin is evaluated at ingest like SshConnection, but it counts attempts over a
    /// window — so the duration rule must key on the point-in-time notion, not on event-ness.
    /// </summary>
    [Test]
    [Arguments(5)]
    [Arguments(10)]
    [Arguments(1440)]
    public async Task ValidateDurationForMetric_FailedSshLogin_AcceptsWindowDurations(int duration)
    {
        bool result = AlertRuleMetricRules.ValidateDurationForMetric(AlertMetric.FailedSshLogin, duration);

        await Assert.That(result).IsTrue();
    }

    /// <summary>
    /// A window shorter than the evaluation cadence is refused: evaluations happen every five
    /// minutes, so such a rule would inspect only its own duration out of every five minutes of
    /// traffic and detect strictly less than the built-in it was meant to tighten.
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(1441)]
    public async Task ValidateDurationForMetric_FailedSshLogin_RejectsOutOfRangeDurations(int duration)
    {
        bool result = AlertRuleMetricRules.ValidateDurationForMetric(AlertMetric.FailedSshLogin, duration);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task GetDurationValidationMessage_FailedSshLogin_DescribesTheWindowBounds()
    {
        string message = AlertRuleMetricRules.GetDurationValidationMessage(AlertMetric.FailedSshLogin);

        await Assert.That(message).Contains("between 5 and 1440");
    }

    [Test]
    public async Task GetDurationValidationMessage_SshConnection_DemandsZero()
    {
        string message = AlertRuleMetricRules.GetDurationValidationMessage(AlertMetric.SshConnection);

        await Assert.That(message).Contains("zero");
    }

    /// <summary>
    /// EqualTo never fires when a burst takes the count from 4 straight to 7, and LessThan fires on
    /// any quiet window containing a single stray failure. Neither can express "a burst happened".
    /// </summary>
    [Test]
    [Arguments(AlertOperator.LessThan)]
    [Arguments(AlertOperator.EqualTo)]
    public async Task ValidateOperatorForMetric_FailedSshLogin_RefusesUnusableOperators(AlertOperator op)
    {
        bool result = AlertRuleMetricRules.ValidateOperatorForMetric(AlertMetric.FailedSshLogin, op);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ValidateOperatorForMetric_FailedSshLogin_AcceptsGreaterThan()
    {
        bool result = AlertRuleMetricRules.ValidateOperatorForMetric(AlertMetric.FailedSshLogin, AlertOperator.GreaterThan);

        await Assert.That(result).IsTrue();
    }

    [Test]
    [Arguments(AlertOperator.LessThan)]
    [Arguments(AlertOperator.EqualTo)]
    [Arguments(AlertOperator.GreaterThan)]
    public async Task ValidateOperatorForMetric_OtherMetrics_AcceptEveryOperator(AlertOperator op)
    {
        await Assert.That(AlertRuleMetricRules.ValidateOperatorForMetric(AlertMetric.CpuUsage, op)).IsTrue();
        await Assert.That(AlertRuleMetricRules.ValidateOperatorForMetric(AlertMetric.SshConnection, op)).IsTrue();
    }

    [Test]
    public async Task GetOperatorValidationMessage_FailedSshLogin_NamesTheAllowedOperator()
    {
        string message = AlertRuleMetricRules.GetOperatorValidationMessage(AlertMetric.FailedSshLogin);

        await Assert.That(message).Contains("more than");
    }

    /// <summary>
    /// The string overloads are what the request validators call, so they have to agree with the
    /// enum overloads rather than quietly passing everything through.
    /// </summary>
    [Test]
    public async Task ValidateOperatorForMetric_StringOverload_MatchesEnumOverload()
    {
        await Assert.That(AlertRuleMetricRules.ValidateOperatorForMetric("FailedSshLogin", "EqualTo")).IsFalse();
        await Assert.That(AlertRuleMetricRules.ValidateOperatorForMetric("FailedSshLogin", "LessThan")).IsFalse();
        await Assert.That(AlertRuleMetricRules.ValidateOperatorForMetric("FailedSshLogin", "GreaterThan")).IsTrue();
    }

    /// <summary>
    /// An unparseable metric or operator defers to the dedicated "invalid metric" / "invalid
    /// operator" rules so the caller sees the parse failure rather than a constraint message.
    /// </summary>
    [Test]
    public async Task ValidateOperatorForMetric_StringOverload_UnparseableInputDefers()
    {
        await Assert.That(AlertRuleMetricRules.ValidateOperatorForMetric("NotAMetric", "EqualTo")).IsTrue();
        await Assert.That(AlertRuleMetricRules.ValidateOperatorForMetric("FailedSshLogin", "NotAnOperator")).IsTrue();
    }

    [Test]
    public async Task ValidateDurationForMetric_StringOverload_FailedSshLoginRejectsZero()
    {
        await Assert.That(AlertRuleMetricRules.ValidateDurationForMetric("FailedSshLogin", 0)).IsFalse();
        await Assert.That(AlertRuleMetricRules.ValidateDurationForMetric("FailedSshLogin", 5)).IsTrue();
    }
}
