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
    /// Returns all server configuration settings.
    /// </summary>
    Task<List<ServerConfigurationSettings>> ListAllSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts a server configuration setting. Inserts a new row if the key does not exist,
    /// or updates the existing row's value and increments the version.
    /// </summary>
    Task UpsertSettingAsync(ServerConfigurationSettingKeys key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing server configuration setting by key.
    /// Increments the version number atomically.
    /// </summary>
    /// <param name="key">The setting key to update.</param>
    /// <param name="value">The new value.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows updated.</returns>
    Task<int> UpdateSettingAsync(ServerConfigurationSettingKeys key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all server configuration settings ordered by key.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<ServerConfigurationSettings>> GetAllSettingsAsync(CancellationToken cancellationToken = default);
}
