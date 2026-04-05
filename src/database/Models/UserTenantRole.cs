// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// A role assigned to a user within a tenant
/// </summary>
[Table(Name = TableNames.UserTenantRoles)]
public sealed class UserTenantRole
{
    /// <summary>
    /// Surrogate primary key for the role assignment.
    /// </summary>
    [PrimaryKey, Identity]
    [Column(Name = "Id"), NotNull]
    public int Id { get; set; }

    /// <summary>
    /// The unique identifier for the user
    /// </summary>
    [Column(Name = "UserId"), NotNull]
    public required int UserId { get; set; }

    /// <summary>
    /// The user associated with this role
    /// </summary>
    [Association(ThisKey = nameof(UserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? User { get; set; }

    /// <summary>
    /// The unique identifier for the tenant
    /// </summary>
    [Column(Name = "AssignedTenantId"), NotNull]
    public required int AssignedTenantId { get; set; }

    /// <summary>
    /// The tenant associated with this role
    /// </summary>
    [Association(ThisKey = nameof(AssignedTenantId), OtherKey = nameof(AssignedTenant.Id))]
    public Tenant? AssignedTenant { get; set; }

    /// <summary>
    /// The role assigned to the user within the tenant
    /// </summary>
    [Column(Name = "Role"), NotNull]
    public required UserAccountRoles Role { get; set; }

    /// <summary>
    /// The ID of the user who assigned this role
    /// </summary>
    [Column(Name = "AssignedByUserId"), NotNull]
    public required int AssignedByUserId { get; set; }

    /// <summary>
    /// The user who assigned this role
    /// </summary>
    [Association(ThisKey = nameof(AssignedByUserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? AssignedByUser { get; set; }

    /// <summary>
    /// The timestamp when the role was assigned
    /// </summary>
    [Column(Name = "AssignedAt"), NotNull]
    public required DateTimeOffset AssignedAt { get; set; }

    /// <summary>
    /// Flag indicating whether the role is active
    /// </summary>
    [Column(Name = "IsActive"), NotNull]
    public required bool IsActive { get; set; }

    /// <summary>
    /// The ID of the user who disabled this role, if applicable
    /// </summary>
    [Column(Name = "DisabledByUserId"), Nullable]
    public int? DisabledByUserId { get; set; }

    /// <summary>
    /// The user who disabled this role, if applicable
    /// </summary>
    [Association(ThisKey = nameof(DisabledByUserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? DisabledByUser { get; set; }

    /// <summary>
    /// The timestamp when the role was disabled, if applicable
    /// </summary>
    [Column(Name = "DisabledAt"), Nullable]
    public DateTimeOffset? DisabledAt { get; set; }
}
