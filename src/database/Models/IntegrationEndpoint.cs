// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents an integration endpoint for delivering alert notifications to external services.
/// </summary>
[Table(Name = TableNames.IntegrationEndpoints)]
public sealed class IntegrationEndpoint
{
    /// <summary>The unique identifier.</summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public int Id { get; set; }

    /// <summary>The tenant this integration belongs to.</summary>
    [Column("TenantId"), NotNull]
    public int TenantId { get; set; }

    /// <summary>The associated tenant.</summary>
    [Association(ThisKey = nameof(TenantId), OtherKey = nameof(Tenant.Id))]
    public Tenant? Tenant { get; set; }

    /// <summary>The integration provider type.</summary>
    [Column("Provider"), NotNull]
    public IntegrationProvider Provider { get; set; }

    /// <summary>A user-facing name for this integration.</summary>
    [Column("Name"), NotNull]
    public required string Name { get; set; }

    /// <summary>Provider-specific configuration stored as JSON.</summary>
    [Column("Configuration", DataType = LinqToDB.DataType.BinaryJson), NotNull]
    public required string Configuration { get; set; }

    /// <summary>Whether this integration is enabled for delivery.</summary>
    [Column("IsEnabled"), NotNull]
    public bool IsEnabled { get; set; }

    /// <summary>The user who created this integration.</summary>
    [Column("CreatedByUserId"), NotNull]
    public int CreatedByUserId { get; set; }

    /// <summary>When the integration was created.</summary>
    [Column("CreatedAt"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the integration was last updated.</summary>
    [Column("UpdatedAt"), Nullable]
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>When the integration was soft-deleted.</summary>
    [Column("DeletedAt"), Nullable]
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>The user who deleted this integration.</summary>
    [Column("DeletedByUserId"), Nullable]
    public int? DeletedByUserId { get; set; }
}
