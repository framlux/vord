// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents a user signing key that has been authorized to issue remote commands to a specific machine.
/// </summary>
[Table(Name = TableNames.MachineAuthorizedKeys)]
public sealed class MachineAuthorizedKey
{
    /// <summary>
    /// The unique identifier for the authorized key record.
    /// </summary>
    [PrimaryKey, Identity]
    [Column(Name = "Id"), NotNull]
    public int Id { get; set; }

    /// <summary>
    /// The unique identifier of the machine this authorization applies to.
    /// </summary>
    [Column(Name = "MachineId"), NotNull]
    public required long MachineId { get; set; }

    /// <summary>
    /// The machine this authorization applies to.
    /// </summary>
    [Association(ThisKey = nameof(MachineId), OtherKey = nameof(Machine.Id))]
    public Machine? Machine { get; set; }

    /// <summary>
    /// The unique identifier of the signing key being authorized.
    /// </summary>
    [Column(Name = "SigningKeyId"), NotNull]
    public required int SigningKeyId { get; set; }

    /// <summary>
    /// The signing key being authorized.
    /// </summary>
    [Association(ThisKey = nameof(SigningKeyId), OtherKey = nameof(UserSigningKey.Id))]
    public UserSigningKey? SigningKey { get; set; }

    /// <summary>
    /// The tenant that owns both the machine and the signing key. Denormalized for query performance.
    /// </summary>
    [Column(Name = "TenantId"), NotNull]
    public required int TenantId { get; set; }

    /// <summary>
    /// The tenant that owns this authorization.
    /// </summary>
    [Association(ThisKey = nameof(TenantId), OtherKey = nameof(Tenant.Id))]
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// The timestamp when this authorization was granted.
    /// </summary>
    [Column(Name = "AuthorizedAt"), NotNull]
    public required DateTimeOffset AuthorizedAt { get; set; }

    /// <summary>
    /// The unique identifier of the user who granted this authorization.
    /// </summary>
    [Column(Name = "AuthorizedByUserId"), NotNull]
    public required int AuthorizedByUserId { get; set; }

    /// <summary>
    /// The user who granted this authorization.
    /// </summary>
    [Association(ThisKey = nameof(AuthorizedByUserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? AuthorizedByUser { get; set; }

    /// <summary>
    /// The timestamp when this authorization was revoked, if applicable.
    /// </summary>
    [Column(Name = "RevokedAt"), Nullable]
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// The unique identifier of the user who revoked this authorization, if applicable.
    /// </summary>
    [Column(Name = "RevokedByUserId"), Nullable]
    public int? RevokedByUserId { get; set; }

    /// <summary>
    /// The user who revoked this authorization, if applicable.
    /// </summary>
    [Association(ThisKey = nameof(RevokedByUserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? RevokedByUser { get; set; }
}
