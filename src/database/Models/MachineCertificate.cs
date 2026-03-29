// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents a tracked client certificate issued to a machine.
/// </summary>
[Table(TableNames.MachineCertificates)]
public sealed class MachineCertificate
{
    /// <summary>
    /// Unique identifier for the certificate record.
    /// </summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public long Id { get; set; }

    /// <summary>
    /// The machine this certificate was issued to.
    /// </summary>
    [Column("MachineId"), NotNull]
    public required long MachineId { get; set; }

    /// <summary>
    /// The SHA-256 thumbprint of the certificate.
    /// </summary>
    [Column("Thumbprint"), NotNull, MaxLength(128)]
    public required string Thumbprint { get; set; }

    /// <summary>
    /// When the certificate was issued.
    /// </summary>
    [Column("IssuedAt"), NotNull]
    public required DateTimeOffset IssuedAt { get; set; }

    /// <summary>
    /// When the certificate expires.
    /// </summary>
    [Column("ExpiresAt"), NotNull]
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// When the certificate was revoked, or null if still active.
    /// </summary>
    [Column("RevokedAt")]
    public DateTimeOffset? RevokedAt { get; set; }
}
