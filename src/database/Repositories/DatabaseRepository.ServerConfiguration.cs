// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IServerConfigurationRepository
{
    /// <inheritdoc/>
    public async Task<List<ServerConfigurationSettings>> ListAllSettingsAsync(CancellationToken cancellationToken)
    {
        return await _db.ServerConfigurationSettings
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpsertSettingAsync(ServerConfigurationSettingKeys key, string value, CancellationToken cancellationToken)
    {
        ServerConfigurationSettings? existing = await _db.ServerConfigurationSettings
            .FirstOrDefaultAsync(s => s.Key == key, cancellationToken);

        if (existing is not null)
        {
            await _db.ServerConfigurationSettings
                .Where(s => s.Id == existing.Id)
                .Set(s => s.Value, value)
                .Set(s => s.Version, existing.Version + 1)
                .UpdateAsync(cancellationToken);
        }
        else
        {
            await _db.ServerConfigurationSettings
                .Value(s => s.Key, key)
                .Value(s => s.Value, value)
                .Value(s => s.Version, 1)
                .InsertAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task<int> UpdateSettingAsync(ServerConfigurationSettingKeys key, string value, CancellationToken cancellationToken)
    {
        int updated = await _db.ServerConfigurationSettings
            .Where(s => s.Key == key)
            .Set(s => s.Value, value)
            .Set(s => s.Version, s => s.Version + 1)
            .UpdateAsync(cancellationToken);

        return updated;
    }

    /// <inheritdoc/>
    public async Task<List<ServerConfigurationSettings>> GetAllSettingsAsync(CancellationToken cancellationToken)
    {
        List<ServerConfigurationSettings> settings = await _db.ServerConfigurationSettings
            .OrderBy(s => s.Key)
            .ToListAsync(cancellationToken);

        return settings;
    }
}
