// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Reads server configuration settings from the database, creating a scoped DatabaseContext per
/// read via <see cref="IServiceScopeFactory"/> so it can be registered as a singleton.
/// </summary>
public sealed class ServerSettingsReader : IServerSettingsReader
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    /// <summary>
    /// Creates a new instance of the <see cref="ServerSettingsReader"/> class.
    /// </summary>
    /// <param name="serviceScopeFactory">Factory used to create DI scopes for database access</param>
    public ServerSettingsReader(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
    }

    /// <inheritdoc/>
    public async Task<string?> GetSettingFromDatabaseAsync(ServerConfigurationSettingKeys key, CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        DatabaseContext dbContext = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        ServerConfigurationSettings? configSetting = await dbContext.ServerConfigurationSettings
            .Where(s => s.Key == key)
            .OrderByDescending(s => s.Version)
            .FirstOrDefaultAsync(cancellationToken);

        return string.IsNullOrEmpty(configSetting?.Value) ? null : configSetting.Value;
    }
}
