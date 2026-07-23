// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents a single ASP.NET Core Data Protection key ring entry: one XML-serialized key
/// (or key revocation) persisted so api-server and services-worker replicas share the same
/// ring across restarts and pod rollouts.
/// </summary>
[Table(Name = TableNames.DataProtectionKeys)]
public sealed class DataProtectionKey
{
    /// <summary>
    /// The unique identifier for the key ring entry.
    /// </summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public int Id { get; set; }

    /// <summary>
    /// The friendly name ASP.NET Core's key manager assigns to the entry, typically the key's
    /// GUID. May be null for some entry types (e.g. revocation elements).
    /// </summary>
    [Column("FriendlyName"), Nullable]
    public string? FriendlyName { get; set; }

    /// <summary>
    /// The XML-serialized key or revocation element, exactly as produced by the Data Protection
    /// key manager.
    /// </summary>
    [Column("Xml"), NotNull]
    public required string Xml { get; set; }

    /// <summary>
    /// When this entry was written.
    /// </summary>
    [Column("CreatedAt"), NotNull]
    public required DateTimeOffset CreatedAt { get; set; }
}
