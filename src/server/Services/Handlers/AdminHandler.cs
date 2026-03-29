// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Endpoints.Web.Admin;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Users;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using LinqToDB.Async;
using LinqToDB;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles admin panel operations.
/// </summary>
public sealed class AdminHandler : IAdminHandler
{
    private readonly DatabaseContext _db;

    /// <summary>
    /// Creates a new instance of the <see cref="AdminHandler"/> class.
    /// </summary>
    public AdminHandler(DatabaseContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        _db = db;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<List<SettingEntry>>> GetSettingsAsync(CancellationToken ct)
    {
        List<ServerConfigurationSettings> settings = await _db.ServerConfigurationSettings
            .ToListAsync(ct);

        List<SettingEntry> entries = settings.Select(s => new SettingEntry
        {
            Key = (int)s.Key,
            Value = s.Value,
        }).ToList();

        return ServiceResult<List<SettingEntry>>.Ok(entries);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<List<UserAccountDto>>> GetAllUsersAsync(CancellationToken ct)
    {
        List<UserAccount> users = await _db.UserAccounts
            .OrderBy(u => u.Username)
            .ToListAsync(ct);

        List<UserTenantRole> allRoles = await _db.UserTenantRoles
            .LoadWith(r => r.AssignedTenant)
            .Where(r => r.IsActive)
            .ToListAsync(ct);

        Dictionary<int, List<UserTenantRole>> rolesByUser = allRoles
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

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
