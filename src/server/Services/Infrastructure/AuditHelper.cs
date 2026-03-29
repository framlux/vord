// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using System.Text.Json;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Helper for constructing audit log entries.
/// </summary>
public static class AuditHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Creates a new audit log entry.
    /// </summary>
    /// <param name="tenantId">The tenant ID, or null for system-level actions.</param>
    /// <param name="userId">The user ID, or null for system actions.</param>
    /// <param name="machineId">The machine ID, if applicable.</param>
    /// <param name="action">The action that was performed.</param>
    /// <param name="resourceType">The type of resource affected.</param>
    /// <param name="resourceId">The identifier of the affected resource.</param>
    /// <param name="details">Additional details to serialize as JSON.</param>
    /// <param name="ipAddress">The client IP address.</param>
    public static AuditLogEntry Create(
        int? tenantId,
        int? userId,
        long? machineId,
        AuditAction action,
        AuditResourceType resourceType,
        string? resourceId,
        object? details,
        string? ipAddress)
    {
        return new AuditLogEntry
        {
            TenantId = tenantId,
            UserId = userId,
            MachineId = machineId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Details = details is not null ? JsonSerializer.Serialize(details, JsonOptions) : null,
            IpAddress = ipAddress,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }
}
