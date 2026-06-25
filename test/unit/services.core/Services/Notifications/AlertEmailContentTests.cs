// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Notifications;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="AlertEmailContent"/>.
/// </summary>
public sealed class AlertEmailContentTests
{
    private static AlertRule BuildRule() => new AlertRule
    {
        Name = "High CPU",
        Metric = AlertMetric.CpuUsage,
        Operator = AlertOperator.GreaterThan,
        Threshold = 90m,
        Severity = AlertSeverity.Critical,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static AlertEvent BuildEvent() => new AlertEvent
    {
        AlertRuleId = 1,
        TenantId = 1,
        MachineId = 42L,
        Severity = AlertSeverity.Critical,
        Message = "CPU usage exceeded threshold",
        Status = AlertEventStatus.Triggered,
        TriggeredAt = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero),
    };

    [Test]
    public async Task Build_Subject_ContainsSeverityMachineAndMetric()
    {
        AlertRule rule = BuildRule();
        AlertEvent alertEvent = BuildEvent();

        AlertEmailContent content = AlertEmailContent.Build(alertEvent, rule, "https://app.vordfleet.com");

        await Assert.That(content.Subject).Contains("Critical");
        await Assert.That(content.Subject).Contains("42");
        await Assert.That(content.Subject).Contains("CpuUsage");
    }

    [Test]
    public async Task Build_Body_ContainsConditionValueThresholdIso8601AndHostLink()
    {
        AlertRule rule = BuildRule();
        AlertEvent alertEvent = BuildEvent();

        AlertEmailContent content = AlertEmailContent.Build(alertEvent, rule, "https://app.vordfleet.com");

        await Assert.That(content.HtmlBody).Contains("GreaterThan");
        await Assert.That(content.HtmlBody).Contains("90");
        await Assert.That(content.HtmlBody).Contains("2026-06-24T12:00:00.0000000+00:00");
        await Assert.That(content.HtmlBody).Contains("https://app.vordfleet.com/machines/42");
    }

    [Test]
    public async Task Build_Body_HtmlEncodesMaliciousContent()
    {
        AlertRule rule = new AlertRule
        {
            Name = "<script>alert('xss')</script>",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 90m,
            Severity = AlertSeverity.Warning,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        AlertEvent alertEvent = new AlertEvent
        {
            AlertRuleId = 1,
            TenantId = 1,
            MachineId = 99L,
            Severity = AlertSeverity.Warning,
            Message = "Test & verify <b>bold</b>",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.UtcNow,
        };

        AlertEmailContent content = AlertEmailContent.Build(alertEvent, rule, "https://app.vordfleet.com");

        await Assert.That(content.HtmlBody).Contains("&lt;script&gt;");
        await Assert.That(content.HtmlBody).Contains("&amp;");
        await Assert.That(content.HtmlBody.Contains("<script>alert")).IsFalse();
    }
}
