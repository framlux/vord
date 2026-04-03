// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;
using System.ComponentModel.DataAnnotations;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents a registration token that agents use to register with a tenant.
/// </summary>
[Table(TableNames.RegistrationTokens)]
public sealed class RegistrationToken
{
    /// <summary>
    /// The unique identifier for the registration token.
    /// </summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public long Id { get; set; }

    /// <summary>
    /// The tenant that this token belongs to.
    /// </summary>
    [Column("TenantId"), NotNull]
    public required int TenantId { get; set; }

    /// <summary>
    /// The associated tenant.
    /// </summary>
    [LinqToDB.Mapping.Association(ThisKey = nameof(TenantId), OtherKey = nameof(Tenant.Id))]
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// The SHA-256 hash of the token value (lowercase hex, 64 chars).
    /// </summary>
    [Column("TokenHash"), NotNull, MaxLength(64)]
    public required string TokenHash { get; set; }

    /// <summary>
    /// A friendly name for the token.
    /// </summary>
    [Column("Name"), NotNull, MaxLength(250)]
    public required string Name { get; set; }

    /// <summary>
    /// The user who created this token.
    /// </summary>
    [Column("CreatedByUserId"), NotNull]
    public required int CreatedByUserId { get; set; }

    /// <summary>
    /// The associated user who created this token.
    /// </summary>
    [LinqToDB.Mapping.Association(ThisKey = nameof(CreatedByUserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? CreatedByUser { get; set; }

    /// <summary>
    /// The date and time when the token was created.
    /// </summary>
    [Column("CreatedAt"), NotNull]
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Whether the token has been revoked.
    /// </summary>
    [Column("IsRevoked"), NotNull]
    public required bool IsRevoked { get; set; }

    /// <summary>
    /// The date and time when the token was revoked.
    /// </summary>
    [Column("RevokedAt"), Nullable]
    public DateTimeOffset? RevokedAt { get; set; }
}
