// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// One row per tenant deletion. Source of truth for the deletion lifecycle: work queue for the
/// purge job, permanent tombstone that survives the purge, and the admin panel's data source.
/// Holds no personal PII, so it persists forever as the "tenant N deleted on X by Y" record.
/// </summary>
[Table("TenantDeletions")]
public sealed class TenantDeletion
{
    /// <summary>Identity primary key.</summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public int Id { get; set; }

    /// <summary>The deleted tenant. The Tenants row is kept (disabled) even after purge.</summary>
    [Column("TenantId"), NotNull]
    public int TenantId { get; set; }

    /// <summary>Denormalized external id so the record is self-describing after purge.</summary>
    [Column("TenantExternalId"), NotNull]
    public required string TenantExternalId { get; set; }

    /// <summary>Org name (not personal data) — for the operator's record.</summary>
    [Column("TenantName"), NotNull]
    public required string TenantName { get; set; }

    /// <summary>Operator who triggered the deletion.</summary>
    [Column("RequestedByUserId"), NotNull]
    public int RequestedByUserId { get; set; }

    /// <summary>When Phase 1 ran.</summary>
    [Column("RequestedAt"), NotNull]
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>RequestedAt + 30 days. Phase 2 fires at/after this instant.</summary>
    [Column("ScheduledPurgeAt"), NotNull]
    public DateTimeOffset ScheduledPurgeAt { get; set; }

    /// <summary>Lifecycle state.</summary>
    [Column("Status"), NotNull]
    public TenantDeletionStatus Status { get; set; }

    /// <summary>Set when Phase 2 completes.</summary>
    [Column("PurgedAt"), Nullable]
    public DateTimeOffset? PurgedAt { get; set; }

    /// <summary>Free-text reason captured by the operator.</summary>
    [Column("Reason"), Nullable]
    public string? Reason { get; set; }
}
