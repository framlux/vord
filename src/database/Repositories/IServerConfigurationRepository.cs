// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for server configuration settings operations.
/// </summary>
public interface IServerConfigurationRepository
{
    /// <summary>
    /// Returns all server configuration settings, ordered by key.
    /// </summary>
    Task<List<ServerConfigurationSettings>> ListAllSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts a server configuration setting. Inserts a new row if the key does not exist,
    /// or updates the existing row's value and increments the version.
    /// </summary>
    Task UpsertSettingAsync(ServerConfigurationSettingKeys key, string value, CancellationToken cancellationToken = default);
}
