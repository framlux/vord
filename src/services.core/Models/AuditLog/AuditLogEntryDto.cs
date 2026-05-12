// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.AuditLog;

/// <summary>
/// Represents an audit log entry for the web UI.
/// </summary>
public sealed class AuditLogEntryDto
{
    /// <summary>The unique identifier.</summary>
    public long Id { get; set; }

    /// <summary>The user who performed the action, if known.</summary>
    public string? UserEmail { get; set; }

    /// <summary>The user ID, if known.</summary>
    public int? UserId { get; set; }

    /// <summary>The machine ID, if applicable.</summary>
    public long? MachineId { get; set; }

    /// <summary>The action performed.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>The type of resource affected.</summary>
    public string ResourceType { get; set; } = string.Empty;

    /// <summary>The identifier of the affected resource.</summary>
    public string? ResourceId { get; set; }

    /// <summary>Additional details as JSON.</summary>
    public string? Details { get; set; }

    /// <summary>The client IP address.</summary>
    public string? IpAddress { get; set; }

    /// <summary>When the action was performed.</summary>
    public DateTimeOffset Timestamp { get; set; }
}
