// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Singleton cache for server configuration settings.
/// </summary>
public interface IServerSettingsCache
{
    /// <summary>
    /// Retrieve a server configuration setting by key, using an in-memory cache with TTL.
    /// </summary>
    /// <param name="key">The configuration setting key to retrieve</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns the configuration value if found; otherwise, returns null</returns>
    Task<string?> GetSettingAsync(ServerConfigurationSettingKeys key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts a server configuration setting by key. Inserts a new row if no row exists
    /// for the key, or updates the existing row's value and increments the version.
    /// Also updates the in-memory cache.
    /// </summary>
    /// <param name="key">The configuration setting key to set.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="cancellationToken">Token used to cancel async calls.</param>
    Task SetSettingAsync(ServerConfigurationSettingKeys key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates the in-memory settings cache, forcing the next read to hit the database.
    /// </summary>
    void InvalidateCache();
}
