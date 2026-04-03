// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// A tenant in the system
/// </summary>
[Table(Name = TableNames.Tenants)]
public sealed class Tenant
{
    /// <summary>
    /// The unique identifier for the tenant
    /// </summary>
    [PrimaryKey, Identity]
    [Column(Name = "Id"), NotNull]
    public int Id { get; set; }

    /// <summary>
    /// The external ID from the identity provider
    /// </summary>
    [Column(Name = "ExternalId"), NotNull]
    public required string ExternalId { get; set; }

    /// <summary>
    /// The name of the tenant
    /// </summary>
    [Column(Name = "Name"), NotNull, System.ComponentModel.DataAnnotations.MaxLength(100)]
    public required string Name { get; set; }

    /// <summary>
    /// The timestamp when the tenant was created
    /// </summary>
    [Column(Name = "CreatedAt"), NotNull]
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The ID of the user who created this tenant
    /// </summary>
    [Column(Name = "CreatedByUserId"), NotNull]
    public required int CreatedByUserId { get; set; }

    /// <summary>
    /// The user who created this tenant
    /// </summary>
    [Association(ThisKey = nameof(CreatedByUserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? CreatedByUser { get; set; }

    /// <summary>
    /// Flag indicating whether the tenant is active
    /// </summary>
    [Column(Name = "IsActive"), NotNull]
    public required bool IsActive { get; set; }

    /// <summary>
    /// The timestamp when the tenant was disabled, if applicable
    /// </summary>
    [Column(Name = "DisabledAt"), Nullable]
    public DateTimeOffset? DisabledAt { get; set; }

    /// <summary>
    /// The ID of the user who disabled this tenant, if applicable
    /// </summary>
    [Column(Name = "DisabledByUserId"), Nullable]
    public int? DisabledByUserId { get; set; }

    /// <summary>
    /// The user who disabled this tenant, if applicable
    /// </summary>
    [Association(ThisKey = nameof(DisabledByUserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? DisabledByUser { get; set; }

    /// <summary>
    /// The URL of the tenant's logo
    /// </summary>
    [Column(Name = "LogoUrl"), NotNull]
    public required string LogoUrl { get; set; }
}
