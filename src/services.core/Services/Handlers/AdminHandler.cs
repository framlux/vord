// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Admin;
using Framlux.FleetManagement.Services.Core.Models.Users;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles admin panel operations.
/// </summary>
public sealed class AdminHandler
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

    private readonly IServerConfigurationRepository _configRepo;
    private readonly IUserRepository _userRepo;
    private readonly IServerSettingsCache _settingsCache;
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;
    private readonly ILogger<AdminHandler> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="AdminHandler"/> class.
    /// </summary>
    public AdminHandler(
        IServerConfigurationRepository configRepo,
        IUserRepository userRepo,
        IServerSettingsCache settingsCache,
        IConnectionMultiplexer redis,
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog,
        ILogger<AdminHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(configRepo);
        ArgumentNullException.ThrowIfNull(userRepo);
        ArgumentNullException.ThrowIfNull(settingsCache);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(logger);

        _configRepo = configRepo;
        _userRepo = userRepo;
        _settingsCache = settingsCache;
        _redis = redis;
        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
        _logger = logger;
    }

    /// <summary>
    /// Returns all server configuration settings.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the list of settings.</returns>
    public async Task<ServiceResult<List<SettingEntry>>> GetSettingsAsync(CancellationToken ct)
    {
        List<ServerConfigurationSettings> settings = await _configRepo.ListAllSettingsAsync(ct);

        List<SettingEntry> entries = settings.Select(s =>
        {
            ServerSettingValidation.Bounds.TryGetValue(s.Key, out (int Min, int Max) bounds);

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

    /// <summary>
    /// Updates one or more server configuration settings.
    /// </summary>
    /// <param name="updates">The settings to update.</param>
    /// <param name="userId">The ID of the global admin performing the update.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the full updated list of settings.</returns>
    public async Task<ServiceResult<List<SettingEntry>>> UpdateSettingsAsync(
        List<SettingUpdateEntry> updates, int userId, CancellationToken ct)
    {
        foreach (SettingUpdateEntry update in updates)
        {
            ServerConfigurationSettingKeys keyEnum = (ServerConfigurationSettingKeys)update.Key;
            string? validationError = ServerSettingValidation.Validate(keyEnum, update.Value);
            if (validationError is not null)
            {
                return ServiceResult<List<SettingEntry>>.BadRequest(validationError);
            }
        }

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        foreach (SettingUpdateEntry update in updates)
        {
            ServerConfigurationSettingKeys key = (ServerConfigurationSettingKeys)update.Key;

            await _configRepo.UpsertSettingAsync(key, update.Value, ct);

            await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
                tenantId: null,
                userId,
                machineId: null,
                AuditAction.ServerConfigurationChanged,
                AuditResourceType.ServerConfiguration,
                key.ToString(),
                new { Key = key.ToString(), Value = update.Value },
                ipAddress: null), ct);
        }

        await transaction.CommitAsync(ct);

        // Invalidate the shared Redis read-through entry and fan out a per-key invalidation to every
        // replica's in-memory cache — only AFTER the commit, so a reader between the delete and the
        // commit cannot re-cache the old value.
        _settingsCache.InvalidateCache();
        foreach (SettingUpdateEntry update in updates)
        {
            ServerConfigurationSettingKeys key = (ServerConfigurationSettingKeys)update.Key;
            await ServerSettingsInvalidation.PublishAsync(_redis, key, _logger);
        }

        return await GetSettingsAsync(ct);
    }

    /// <summary>
    /// Returns all user accounts with their tenant roles.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the list of user account DTOs.</returns>
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
