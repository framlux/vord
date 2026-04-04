// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents an audit log entry recording a user or system action.
/// </summary>
[Table(Name = TableNames.AuditLog)]
public sealed class AuditLogEntry
{
    /// <summary>
    /// The unique identifier for the audit log entry.
    /// </summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public long Id { get; set; }

    /// <summary>
    /// The ID of the tenant this action was performed in, null for system-level actions.
    /// </summary>
    [Column("TenantId"), Nullable]
    public int? TenantId { get; set; }

    /// <summary>
    /// The associated tenant.
    /// </summary>
    [Association(ThisKey = nameof(TenantId), OtherKey = nameof(Tenant.Id))]
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// The ID of the user who performed the action, null for system actions.
    /// </summary>
    [Column("UserId"), Nullable]
    public int? UserId { get; set; }

    /// <summary>
    /// The associated user account.
    /// </summary>
    [Association(ThisKey = nameof(UserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? User { get; set; }

    /// <summary>
    /// The ID of the machine involved, if applicable.
    /// </summary>
    [Column("MachineId"), Nullable]
    public long? MachineId { get; set; }

    /// <summary>
    /// The type of action that was performed.
    /// </summary>
    [Column("Action"), NotNull]
    public required AuditAction Action { get; set; }

    /// <summary>
    /// The type of resource affected by the action.
    /// </summary>
    [Column("ResourceType"), NotNull]
    public required AuditResourceType ResourceType { get; set; }

    /// <summary>
    /// The identifier of the specific resource affected.
    /// </summary>
    [Column("ResourceId"), Nullable]
    public string? ResourceId { get; set; }

    /// <summary>
    /// Additional JSON details about the action.
    /// </summary>
    [Column("Details"), Nullable]
    public string? Details { get; set; }

    /// <summary>
    /// The IP address of the client that performed the action.
    /// </summary>
    [Column("IpAddress"), Nullable]
    public string? IpAddress { get; set; }

    /// <summary>
    /// When the action was performed.
    /// </summary>
    [Column("Timestamp"), NotNull]
    public required DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// When this record was soft-deleted for retention cleanup.
    /// </summary>
    [Column("DeletedAt"), Nullable]
    public DateTimeOffset? DeletedAt { get; set; }
}
