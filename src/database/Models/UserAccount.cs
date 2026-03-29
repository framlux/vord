// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// A user account in the system
/// </summary>
[Table(Name = TableNames.Users)]
public sealed class UserAccount
{
    /// <summary>
    /// The unique identifier for the user account
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
    /// The unique username for the user account
    /// </summary>
    [Column(Name = "Username"), NotNull]
    public required string Username { get; set; }

    /// <summary>
    /// The timestamp when the user account was created
    /// </summary>
    [Column(Name = "CreatedAt"), NotNull]
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The ID of the user who created this account
    /// </summary>
    [Column(Name = "CreatedByUserId"), NotNull]
    public required int CreatedByUserId { get; set; }

    /// <summary>
    /// The user who created this account
    /// </summary>
    [Association(ThisKey = nameof(CreatedByUserId), OtherKey = nameof(Id))]
    public UserAccount? CreatedByUser { get; set; }

    /// <summary>
    /// Flag indicating whether the user account is active
    /// </summary>
    [Column(Name = "IsActive"), NotNull]
    public required bool IsActive { get; set; }

    /// <summary>
    /// Flag indicating whether the user account is a system account
    /// </summary>
    [Column(Name = "IsSystem"), NotNull]
    public required bool IsSystem { get; set; }

    /// <summary>
    /// Flag indicating whether the user account has global admin privileges
    /// </summary>
    [Column(Name = "IsGlobalAdmin"), NotNull]
    public required bool IsGlobalAdmin { get; set; }

    /// <summary>
    /// The authentication provider used by this user account.
    /// </summary>
    [Column(Name = "AuthProvider"), NotNull]
    public AuthProviderType AuthProvider { get; set; }

    /// <summary>
    /// The timestamp when the user account was deactivated, if applicable
    /// </summary>
    [Column(Name = "DeletedOn")]
    public DateTimeOffset? DeletedOn { get; set; }

    /// <summary>
    /// The ID of the user who deactivated this account, if applicable
    /// </summary>
    [Column(Name = "DeletedByUserId"), Nullable]
    public int? DeletedByUserId { get; set; }

    /// <summary>
    /// The user who deactivated this account, if applicable
    /// </summary>
    [Association(ThisKey = nameof(DeletedByUserId), OtherKey = nameof(Id))]
    public UserAccount? DeletedByUser { get; set; }
}
