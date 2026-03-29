// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents an invitation to join a tenant.
/// </summary>
[Table(Name = TableNames.TenantInvitations)]
public sealed class TenantInvitation
{
    /// <summary>
    /// The unique identifier for the invitation.
    /// </summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public int Id { get; set; }

    /// <summary>
    /// The ID of the tenant this invitation belongs to.
    /// </summary>
    [Column("TenantId"), NotNull]
    public required int TenantId { get; set; }

    /// <summary>
    /// The associated tenant.
    /// </summary>
    [Association(ThisKey = nameof(TenantId), OtherKey = nameof(Tenant.Id))]
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// The invitee's email address (lowercase, trimmed).
    /// </summary>
    [Column("Email"), NotNull]
    public required string Email { get; set; }

    /// <summary>
    /// SHA-256 hash of the cryptographic random token for the accept link.
    /// </summary>
    [Column("TokenHash"), NotNull]
    public required string TokenHash { get; set; }

    /// <summary>
    /// The role to assign when the invitation is accepted.
    /// </summary>
    [Column("Role"), NotNull]
    public required UserAccountRoles Role { get; set; }

    /// <summary>
    /// The current status of the invitation.
    /// </summary>
    [Column("Status"), NotNull]
    public required InvitationStatus Status { get; set; }

    /// <summary>
    /// The ID of the user who created the invitation.
    /// </summary>
    [Column("InvitedByUserId"), NotNull]
    public required int InvitedByUserId { get; set; }

    /// <summary>
    /// The user who created the invitation.
    /// </summary>
    [Association(ThisKey = nameof(InvitedByUserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? InvitedByUser { get; set; }

    /// <summary>
    /// The ID of the user who accepted the invitation, if accepted.
    /// </summary>
    [Column("AcceptedByUserId"), Nullable]
    public int? AcceptedByUserId { get; set; }

    /// <summary>
    /// The user who accepted the invitation.
    /// </summary>
    [Association(ThisKey = nameof(AcceptedByUserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? AcceptedByUser { get; set; }

    /// <summary>
    /// When the invitation was created.
    /// </summary>
    [Column("CreatedAt"), NotNull]
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the invitation expires.
    /// </summary>
    [Column("ExpiresAt"), NotNull]
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// When the invitation was accepted, if applicable.
    /// </summary>
    [Column("AcceptedAt"), Nullable]
    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>
    /// When the invitation was revoked, if applicable.
    /// </summary>
    [Column("RevokedAt"), Nullable]
    public DateTimeOffset? RevokedAt { get; set; }
}
