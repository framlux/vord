// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>DTO for alert rules.</summary>
public sealed class AlertRuleDto
{
    /// <summary>The rule ID.</summary>
    public int Id { get; set; }

    /// <summary>The rule name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The rule description.</summary>
    public string? Description { get; set; }

    /// <summary>The metric being evaluated.</summary>
    public string Metric { get; set; } = string.Empty;

    /// <summary>The comparison operator.</summary>
    public string Operator { get; set; } = string.Empty;

    /// <summary>The threshold value.</summary>
    public decimal Threshold { get; set; }

    /// <summary>Duration in minutes before firing.</summary>
    public int DurationMinutes { get; set; }

    /// <summary>The severity level.</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>Whether the rule is enabled.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Whether email notifications are enabled.</summary>
    public bool NotifyEmail { get; set; }

    /// <summary>Whether webhook notifications are enabled.</summary>
    public bool NotifyWebhook { get; set; }

    /// <summary>Whether this is a custom rule.</summary>
    public bool IsCustom { get; set; }
}
