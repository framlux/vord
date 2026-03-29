// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents an Ed25519 public signing key registered by a user for remote command authorization.
/// </summary>
[Table(Name = TableNames.UserSigningKeys)]
public sealed class UserSigningKey
{
    /// <summary>
    /// The unique identifier for the signing key.
    /// </summary>
    [PrimaryKey, Identity]
    [Column(Name = "Id"), NotNull]
    public int Id { get; set; }

    /// <summary>
    /// The ID of the user who owns this signing key.
    /// </summary>
    [Column(Name = "UserId"), NotNull]
    public required int UserId { get; set; }

    /// <summary>
    /// The user who owns this signing key.
    /// </summary>
    [LinqToDB.Mapping.Association(ThisKey = nameof(UserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? User { get; set; }

    /// <summary>
    /// The tenant this signing key is scoped to.
    /// </summary>
    [Column(Name = "TenantId"), NotNull]
    public required int TenantId { get; set; }

    /// <summary>
    /// The associated tenant.
    /// </summary>
    [LinqToDB.Mapping.Association(ThisKey = nameof(TenantId), OtherKey = nameof(Tenant.Id))]
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// A user-chosen label for the key (e.g., "Work MacBook").
    /// </summary>
    [Column(Name = "Label"), NotNull, MaxLength(250)]
    public required string Label { get; set; }

    /// <summary>
    /// The base64-encoded 32-byte Ed25519 public key.
    /// </summary>
    [Column(Name = "PublicKey"), NotNull, MaxLength(64)]
    public required string PublicKey { get; set; }

    /// <summary>
    /// SHA-256 fingerprint of the public key for quick lookup.
    /// </summary>
    [Column(Name = "PublicKeyFingerprint"), NotNull, MaxLength(64)]
    public required string PublicKeyFingerprint { get; set; }

    /// <summary>
    /// The timestamp when the key was registered.
    /// </summary>
    [Column(Name = "CreatedAt"), NotNull]
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The timestamp when the key was revoked, if applicable.
    /// </summary>
    [Column(Name = "RevokedAt"), Nullable]
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// The ID of the user who revoked this key, if applicable.
    /// </summary>
    [Column(Name = "RevokedByUserId"), Nullable]
    public int? RevokedByUserId { get; set; }

    /// <summary>
    /// The user who revoked this key, if applicable.
    /// </summary>
    [LinqToDB.Mapping.Association(ThisKey = nameof(RevokedByUserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? RevokedByUser { get; set; }
}
