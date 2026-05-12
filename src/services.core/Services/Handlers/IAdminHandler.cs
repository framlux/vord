// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models.Admin;
using Framlux.FleetManagement.Services.Core.Models.Users;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles admin panel operations.
/// </summary>
public interface IAdminHandler
{
    /// <summary>
    /// Returns all server configuration settings.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the list of settings.</returns>
    Task<ServiceResult<List<SettingEntry>>> GetSettingsAsync(CancellationToken ct);

    /// <summary>
    /// Updates one or more server configuration settings.
    /// </summary>
    /// <param name="updates">The settings to update.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the full updated list of settings.</returns>
    Task<ServiceResult<List<SettingEntry>>> UpdateSettingsAsync(List<SettingUpdateEntry> updates, CancellationToken ct);

    /// <summary>
    /// Returns all user accounts with their tenant roles.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the list of user account DTOs.</returns>
    Task<ServiceResult<List<UserAccountDto>>> GetAllUsersAsync(CancellationToken ct);
}
