// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Reads server configuration settings from the database. Caching of these values is owned
/// entirely by the shared Redis read-through in the services layer, so this reader is always
/// authoritative and never serves a remembered value.
/// </summary>
public interface IServerSettingsReader
{
    /// <summary>
    /// Reads a setting directly from the database. The shared Redis read-through repopulates from
    /// here so a just-invalidated Redis key is never re-seeded from a stale value.
    /// </summary>
    /// <param name="key">The configuration setting key to retrieve.</param>
    /// <param name="cancellationToken">Token used to cancel async calls.</param>
    /// <returns>The current value from the database, or null when unset.</returns>
    Task<string?> GetSettingFromDatabaseAsync(ServerConfigurationSettingKeys key, CancellationToken cancellationToken = default);
}
