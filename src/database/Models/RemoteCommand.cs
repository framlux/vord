// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents a signed remote command sent to a machine.
/// </summary>
[Table(Name = TableNames.RemoteCommands)]
public sealed class RemoteCommand
{
    /// <summary>
    /// The unique identifier for the remote command.
    /// </summary>
    [PrimaryKey, Identity]
    [Column(Name = "Id"), NotNull]
    public long Id { get; set; }

    /// <summary>
    /// Client-generated UUID for the command, used for deduplication.
    /// </summary>
    [Column(Name = "CommandId"), NotNull, MaxLength(36)]
    public required string CommandId { get; set; }

    /// <summary>
    /// The tenant this command belongs to.
    /// </summary>
    [Column(Name = "TenantId"), NotNull]
    public required int TenantId { get; set; }

    /// <summary>
    /// The associated tenant.
    /// </summary>
    [LinqToDB.Mapping.Association(ThisKey = nameof(TenantId), OtherKey = nameof(Tenant.Id))]
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// The target machine for this command.
    /// </summary>
    [Column(Name = "MachineId"), NotNull]
    public required long MachineId { get; set; }

    /// <summary>
    /// The associated machine.
    /// </summary>
    [LinqToDB.Mapping.Association(ThisKey = nameof(MachineId), OtherKey = nameof(Machine.Id))]
    public Machine? Machine { get; set; }

    /// <summary>
    /// The user who sent this command.
    /// </summary>
    [Column(Name = "UserId"), NotNull]
    public required int UserId { get; set; }

    /// <summary>
    /// The associated user.
    /// </summary>
    [LinqToDB.Mapping.Association(ThisKey = nameof(UserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? User { get; set; }

    /// <summary>
    /// The signing key used to sign this command.
    /// </summary>
    [Column(Name = "SigningKeyId"), NotNull]
    public required int SigningKeyId { get; set; }

    /// <summary>
    /// The associated signing key.
    /// </summary>
    [LinqToDB.Mapping.Association(ThisKey = nameof(SigningKeyId), OtherKey = nameof(UserSigningKey.Id))]
    public UserSigningKey? SigningKey { get; set; }

    /// <summary>
    /// The type of command (e.g., "reboot", "kill_process").
    /// </summary>
    [Column(Name = "CommandType"), NotNull, MaxLength(50)]
    public required string CommandType { get; set; }

    /// <summary>
    /// JSON-encoded command parameters.
    /// </summary>
    [Column(Name = "Params"), Nullable]
    public string? Params { get; set; }

    /// <summary>
    /// The unique nonce used for replay prevention.
    /// </summary>
    [Column(Name = "Nonce"), NotNull, MaxLength(32)]
    public required string Nonce { get; set; }

    /// <summary>
    /// The base64-encoded Ed25519 signature.
    /// </summary>
    [Column(Name = "Signature"), NotNull, MaxLength(128)]
    public required string Signature { get; set; }

    /// <summary>
    /// The exact canonical JSON payload that was signed.
    /// </summary>
    [Column(Name = "CanonicalPayload"), NotNull]
    public required string CanonicalPayload { get; set; }

    /// <summary>
    /// The timestamp included in the signed payload.
    /// </summary>
    [Column(Name = "Timestamp"), NotNull]
    public required DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// When this command expires and should no longer be executed.
    /// </summary>
    [Column(Name = "ExpiresAt"), NotNull]
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// The current status of the command.
    /// </summary>
    [Column(Name = "Status"), NotNull]
    public required RemoteCommandStatus Status { get; set; }

    /// <summary>
    /// When the command was created in the database.
    /// </summary>
    [Column(Name = "CreatedAt"), NotNull]
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the command was delivered to the agent.
    /// </summary>
    [Column(Name = "DeliveredAt"), Nullable]
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>
    /// When the command execution completed (or failed).
    /// </summary>
    [Column(Name = "CompletedAt"), Nullable]
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The exit code from command execution, if applicable.
    /// </summary>
    [Column(Name = "ExitCode"), Nullable]
    public int? ExitCode { get; set; }

    /// <summary>
    /// Standard output from command execution.
    /// </summary>
    [Column(Name = "Stdout"), Nullable, MaxLength(2064)]
    public string? Stdout { get; set; }

    /// <summary>
    /// Standard error from command execution.
    /// </summary>
    [Column(Name = "Stderr"), Nullable, MaxLength(2064)]
    public string? Stderr { get; set; }

    /// <summary>
    /// A human-readable result message from the agent.
    /// </summary>
    [Column(Name = "ResultMessage"), Nullable]
    public string? ResultMessage { get; set; }
}
