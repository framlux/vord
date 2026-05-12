// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Alerts;

/// <summary>DTO for alert events.</summary>
public sealed class AlertEventDto
{
    /// <summary>The event ID.</summary>
    public long Id { get; set; }

    /// <summary>The alert rule name.</summary>
    public string RuleName { get; set; } = string.Empty;

    /// <summary>The machine ID.</summary>
    public long MachineId { get; set; }

    /// <summary>The machine name.</summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>The severity level.</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>The alert message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>The event status.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>When the alert was triggered.</summary>
    public DateTimeOffset TriggeredAt { get; set; }

    /// <summary>When the alert was acknowledged.</summary>
    public DateTimeOffset? AcknowledgedAt { get; set; }

    /// <summary>The user who acknowledged this event.</summary>
    public int? AcknowledgedByUserId { get; set; }

    /// <summary>When the alert was resolved.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
}
