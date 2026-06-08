// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Admin;
using Framlux.FleetManagement.Services.Core.Models.Users;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles admin panel operations.
/// </summary>
public sealed class AdminHandler : IAdminHandler
{
    /// <summary>
    /// Human-readable descriptions for each server configuration setting key.
    /// </summary>
    public static readonly Dictionary<ServerConfigurationSettingKeys, string> SettingDescriptions = new()
    {
        [ServerConfigurationSettingKeys.AgentHeartbeatSeconds] = "How often agents send a heartbeat to the server, in seconds (10-600).",
        [ServerConfigurationSettingKeys.AgentConfigRefreshSeconds] = "How often agents refresh their configuration from the server, in seconds (60-86400).",
        [ServerConfigurationSettingKeys.OnlineThresholdSeconds] = "Maximum seconds since last heartbeat before a machine is considered offline.",
        [ServerConfigurationSettingKeys.DeduplicationTtlSeconds] = "Time-to-live in seconds for telemetry event deduplication.",
        [ServerConfigurationSettingKeys.AgentCommandPollSeconds] = "How often agents poll the server for pending commands, in seconds (10-300).",
        [ServerConfigurationSettingKeys.AllowUserSignup] = "Whether new users are allowed to self-register via social login.",
        [ServerConfigurationSettingKeys.TelemetryCollectFastSeconds] = "How often agents sample CPU, memory, and disk usage, in seconds (10-300).",
        [ServerConfigurationSettingKeys.TelemetryCollectSlowSeconds] = "How often agents collect static system information, in seconds (60-3600).",
        [ServerConfigurationSettingKeys.TelemetrySendFastSeconds] = "How often agents transmit high-frequency metrics to the server, in seconds (5-120).",
        [ServerConfigurationSettingKeys.TelemetrySendSlowSeconds] = "How often agents transmit low-frequency data to the server, in seconds (30-1800).",
        [ServerConfigurationSettingKeys.ServiceStatusSeconds] = "How often agents collect systemd service status, in seconds (60-86400).",
    };

    /// <summary>
    /// Valid min/max bounds for each numeric server configuration setting key.
    /// </summary>
    public static readonly Dictionary<ServerConfigurationSettingKeys, (int Min, int Max)> SettingBounds = new()
    {
        [ServerConfigurationSettingKeys.AgentHeartbeatSeconds] = (10, 600),
        [ServerConfigurationSettingKeys.AgentConfigRefreshSeconds] = (60, 86400),
        [ServerConfigurationSettingKeys.AgentCommandPollSeconds] = (10, 300),
        [ServerConfigurationSettingKeys.TelemetryCollectFastSeconds] = (10, 300),
        [ServerConfigurationSettingKeys.TelemetryCollectSlowSeconds] = (60, 3600),
        [ServerConfigurationSettingKeys.TelemetrySendFastSeconds] = (5, 120),
        [ServerConfigurationSettingKeys.TelemetrySendSlowSeconds] = (30, 1800),
        [ServerConfigurationSettingKeys.ServiceStatusSeconds] = (60, 86400),
    };

    private readonly IServerConfigurationRepository _configRepo;
    private readonly IUserRepository _userRepo;
    private readonly IServerSettingsCache _settingsCache;
    private readonly IConnectionMultiplexer _redis;

    /// <summary>
    /// Creates a new instance of the <see cref="AdminHandler"/> class.
    /// </summary>
    public AdminHandler(
        IServerConfigurationRepository configRepo,
        IUserRepository userRepo,
        IServerSettingsCache settingsCache,
        IConnectionMultiplexer redis)
    {
        ArgumentNullException.ThrowIfNull(configRepo);
        ArgumentNullException.ThrowIfNull(userRepo);
        ArgumentNullException.ThrowIfNull(settingsCache);
        ArgumentNullException.ThrowIfNull(redis);

        _configRepo = configRepo;
        _userRepo = userRepo;
        _settingsCache = settingsCache;
        _redis = redis;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<List<SettingEntry>>> GetSettingsAsync(CancellationToken ct)
    {
        List<ServerConfigurationSettings> settings = await _configRepo.ListAllSettingsAsync(ct);

        List<SettingEntry> entries = settings.Select(s =>
        {
            SettingBounds.TryGetValue(s.Key, out (int Min, int Max) bounds);

            return new SettingEntry
            {
                Key = (int)s.Key,
                Name = Enum.GetName(s.Key) ?? s.Key.ToString(),
                Description = SettingDescriptions.GetValueOrDefault(s.Key, string.Empty),
                Value = s.Value,
                Min = bounds.Min > 0 ? bounds.Min : null,
                Max = bounds.Max > 0 ? bounds.Max : null,
            };
        }).ToList();

        return ServiceResult<List<SettingEntry>>.Ok(entries);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<List<SettingEntry>>> UpdateSettingsAsync(
        List<SettingUpdateEntry> updates, CancellationToken ct)
    {
        foreach (SettingUpdateEntry update in updates)
        {
            ServerConfigurationSettingKeys keyEnum = (ServerConfigurationSettingKeys)update.Key;
            if (Enum.IsDefined(keyEnum) == false ||
                keyEnum == ServerConfigurationSettingKeys.None)
            {
                return ServiceResult<List<SettingEntry>>.BadRequest($"Invalid setting key: {update.Key}");
            }

            if (string.IsNullOrWhiteSpace(update.Value))
            {
                return ServiceResult<List<SettingEntry>>.BadRequest($"Value must not be empty for key: {update.Key}");
            }

            if (keyEnum == ServerConfigurationSettingKeys.AllowUserSignup)
            {
                if (string.Equals(update.Value, "true", StringComparison.OrdinalIgnoreCase) == false &&
                    string.Equals(update.Value, "false", StringComparison.OrdinalIgnoreCase) == false)
                {
                    return ServiceResult<List<SettingEntry>>.BadRequest("AllowUserSignup must be 'true' or 'false'.");
                }
            }
            else
            {
                string name = Enum.GetName(keyEnum) ?? keyEnum.ToString();

                if (int.TryParse(update.Value, out int parsed) == false || parsed <= 0)
                {
                    return ServiceResult<List<SettingEntry>>.BadRequest($"{name} must be a positive integer.");
                }

                if (SettingBounds.TryGetValue(keyEnum, out (int Min, int Max) bounds))
                {
                    if ((parsed < bounds.Min) || (parsed > bounds.Max))
                    {
                        return ServiceResult<List<SettingEntry>>.BadRequest(
                            $"{name} must be between {bounds.Min} and {bounds.Max}.");
                    }
                }
            }
        }

        IDatabase redisDb = _redis.GetDatabase();

        foreach (SettingUpdateEntry update in updates)
        {
            ServerConfigurationSettingKeys key = (ServerConfigurationSettingKeys)update.Key;

            await _configRepo.UpsertSettingAsync(key, update.Value, ct);

            string redisKey = $"config:{key}";
            await redisDb.KeyDeleteAsync(redisKey);
        }

        _settingsCache.InvalidateCache();

        return await GetSettingsAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<List<UserAccountDto>>> GetAllUsersAsync(CancellationToken ct)
    {
        (List<UserAccount> users, Dictionary<int, List<UserTenantRole>> rolesByUser) =
            await _userRepo.GetAllUsersWithRolesAsync(ct);

        List<UserAccountDto> dtos = users.Select(u =>
        {
            List<UserTenantDto> tenants = new();
            if (rolesByUser.TryGetValue(u.Id, out List<UserTenantRole>? roles))
            {
                tenants = roles.Select(r => new UserTenantDto
                {
                    TenantId = r.AssignedTenantId,
                    TenantName = r.AssignedTenant?.Name ?? "Unknown",
                    Role = ((int)r.Role).ToString(),
                }).ToList();
            }

            return new UserAccountDto
            {
                Id = u.Id,
                Username = u.Username,
                IsActive = u.IsActive,
                IsGlobalAdmin = u.IsGlobalAdmin,
                CreatedAt = u.CreatedAt,
                Tenants = tenants,
            };
        }).ToList();

        return ServiceResult<List<UserAccountDto>>.Ok(dtos);
    }
}
