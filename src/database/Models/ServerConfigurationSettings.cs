// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents the server configuration settings.
/// </summary>
[Table(Name = TableNames.ServerConfigurationSettings)]
public sealed class ServerConfigurationSettings
{
    /// <summary>
    /// The unique identifier for the server configuration settings.
    /// </summary>
    [PrimaryKey, Identity]
    [Column(Name = "Id"), NotNull]
    public int Id { get; set; }

    /// <summary>
    /// The key for the server configuration setting.
    /// </summary>
    [Column(Name = "Key"), NotNull]
    public required ServerConfigurationSettingKeys Key { get; set; }

    /// <summary>
    /// The value for the server configuration setting.
    /// </summary>
    [Column(Name = "Value"), NotNull]
    public required string Value { get; set; }

    /// <summary>
    /// The version of the server configuration setting.
    /// </summary>
    [Column(Name = "Version"), NotNull]
    public required int Version { get; set; }
}
