// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;
using System.ComponentModel.DataAnnotations;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents a machine in the system.
/// </summary>
[Table(TableNames.Machines)]
public sealed class Machine
{
    /// <summary>
    /// Represents the unique identifier for the machine.
    /// </summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public long Id { get; set; }

    /// <summary>
    /// Represents the SHA-256 hash of the machine's API key (lowercase hex, 64 chars).
    /// </summary>
    [NotNull, Column("ApiKeyHash"), MaxLength(64)]
    public required string ApiKeyHash { get; set; }

    /// <summary>
    /// Represents the name of the machine.
    /// </summary>
    [NotNull, Column("Name"), MaxLength(250)]
    public required string Name { get; set; }

    /// <summary>
    /// Represents the description of the machine.
    /// </summary>
    [Column("Description")]
    public string? Description { get; set; }

    /// <summary>
    /// Represents the physical location of the machine.
    /// </summary>
    [Column("Location"), MaxLength(250)]
    public string? Location { get; set; }

    /// <summary>
    /// Represents the serial number of the machine.
    /// </summary>
    [NotNull, Column("SerialNumber"), MaxLength(64)]
    public required string SerialNumber { get; set; }

    /// <summary>
    /// Represents the system ID of the machine.
    /// </summary>
    [NotNull, Column("SystemId"), MaxLength(64)]
    public required string SystemId { get; set; }

    /// <summary>
    /// Represents the asset tag number of the machine.
    /// </summary>
    [Column("AssetTagNumber"), MaxLength(64)]
    public string? AssetTagNumber { get; set; }

    /// <summary>
    /// Represents the type of hardware the machine is running.
    /// </summary>
    [NotNull, Column("MachineType")]
    public required MachineTypes MachineType { get; set; }

    /// <summary>
    /// Represents the operating system of the machine.
    /// </summary>
    [NotNull, Column("OperatingSystem")]
    public required OperatingSystems OperatingSystem { get; set; }

    /// <summary>
    /// Represents the registration token that was used to register this machine.
    /// </summary>
    [NotNull, Column("RegistrationTokenId")]
    public required long RegistrationTokenId { get; set; }

    /// <summary>
    /// The associated registration token.
    /// </summary>
    [LinqToDB.Mapping.Association(ThisKey = nameof(RegistrationTokenId), OtherKey = nameof(RegistrationToken.Id))]
    public RegistrationToken? RegistrationToken { get; set; }

    /// <summary>
    /// Represents the date and time when the machine was registered.
    /// </summary>
    [NotNull, Column("RegisteredOn")]
    public required DateTimeOffset RegisteredOn { get; set; }

    /// <summary>
    /// Represents whether the machine is deleted.
    /// </summary>
    [NotNull, Column("IsDeleted")]
    public required bool IsDeleted { get; set; }

    /// <summary>
    /// Represents the date and time when the machine was deleted.
    /// </summary>
    [Column("DeletedOn")]
    public DateTimeOffset? DeletedOn { get; set; }

    /// <summary>
    /// Represents the ID of the user who deleted the machine.
    /// </summary>
    [Column("DeletedByUserId"), Nullable]
    public int? DeletedByUserId { get; set; }

    /// <summary>
    /// Represents the user who deleted the machine.
    /// </summary>
    [LinqToDB.Mapping.Association(ThisKey = nameof(DeletedByUserId), OtherKey = nameof(UserAccount.Id))]
    public UserAccount? DeletedByUser { get; set; }

    /// <summary>
    /// Represents the date and time when the API key was delivered to the agent.
    /// </summary>
    [Column("KeyDeliveredAt"), Nullable]
    public DateTimeOffset? KeyDeliveredAt { get; set; }

    /// <summary>
    /// Represents the tenant that owns this machine.
    /// </summary>
    [Column("TenantId"), NotNull]
    public required int TenantId { get; set; }

    /// <summary>
    /// The associated tenant.
    /// </summary>
    [LinqToDB.Mapping.Association(ThisKey = nameof(TenantId), OtherKey = nameof(Tenant.Id))]
    public Tenant? Tenant { get; set; }
}
