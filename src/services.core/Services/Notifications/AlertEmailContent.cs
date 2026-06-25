// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Globalization;
using System.Net;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Services.Core.Notifications;

/// <summary>
/// Rendered subject and HTML body for an alert notification email. Rendering is pure (no I/O)
/// so it can be unit-tested independently of the Resend transport.
/// </summary>
public sealed record AlertEmailContent
{
    /// <summary>The email subject line.</summary>
    public required string Subject { get; init; }

    /// <summary>The rendered HTML body.</summary>
    public required string HtmlBody { get; init; }

    /// <summary>
    /// Builds the subject and body for an alert email from the event, rule, and application base URL.
    /// The subject carries severity, machine, and metric; the body carries the condition, value,
    /// threshold, ISO8601 trigger timestamp, and a link to the host.
    /// </summary>
    /// <param name="alertEvent">The alert event that fired.</param>
    /// <param name="rule">The rule that produced the event.</param>
    /// <param name="appBaseUrl">The application base URL used to build the host link.</param>
    /// <returns>The rendered email content.</returns>
    public static AlertEmailContent Build(AlertEvent alertEvent, AlertRule rule, string appBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);
        ArgumentNullException.ThrowIfNull(rule);

        string triggeredAtIso = alertEvent.TriggeredAt.ToString("O");
        string hostLink = $"{appBaseUrl?.TrimEnd('/')}/machines/{alertEvent.MachineId}";

        string subject = $"[{alertEvent.Severity}] {rule.Name} — machine {alertEvent.MachineId} ({rule.Metric})";

        string htmlBody = $"""
            <div style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 560px; margin: 0 auto; padding: 40px 20px;">
                <h2 style="color: #1a1a1a; margin-bottom: 8px;">{HtmlEncode(alertEvent.Severity.ToString())}: {HtmlEncode(rule.Name)}</h2>
                <p style="color: #666; font-size: 15px; line-height: 1.5;">{HtmlEncode(alertEvent.Message)}</p>
                <table style="border-collapse: collapse; margin: 16px 0; font-size: 14px; color: #333;">
                    <tr><td style="padding: 4px 12px 4px 0;"><strong>Machine</strong></td><td style="padding: 4px 0;">{alertEvent.MachineId}</td></tr>
                    <tr><td style="padding: 4px 12px 4px 0;"><strong>Metric</strong></td><td style="padding: 4px 0;">{HtmlEncode(rule.Metric.ToString())}</td></tr>
                    <tr><td style="padding: 4px 12px 4px 0;"><strong>Condition</strong></td><td style="padding: 4px 0;">{HtmlEncode(rule.Operator.ToString())} {rule.Threshold.ToString(CultureInfo.InvariantCulture)}</td></tr>
                    <tr><td style="padding: 4px 12px 4px 0;"><strong>Threshold</strong></td><td style="padding: 4px 0;">{rule.Threshold.ToString(CultureInfo.InvariantCulture)}</td></tr>
                    <tr><td style="padding: 4px 12px 4px 0;"><strong>Triggered</strong></td><td style="padding: 4px 0;">{HtmlEncode(triggeredAtIso)}</td></tr>
                </table>
                <div style="margin: 24px 0;">
                    <a href="{HtmlEncode(hostLink)}" style="display: inline-block; background-color: #6366f1; color: #ffffff; text-decoration: none; padding: 12px 32px; border-radius: 8px; font-weight: 600; font-size: 15px;">View Host</a>
                </div>
                <hr style="border: none; border-top: 1px solid #eee; margin: 32px 0;" />
                <p style="color: #bbb; font-size: 12px;">Framlux Vord &mdash; Fleet Monitoring</p>
            </div>
            """;

        return new AlertEmailContent { Subject = subject, HtmlBody = htmlBody };
    }

    private static string HtmlEncode(string value)
    {
        return WebUtility.HtmlEncode(value);
    }
}
