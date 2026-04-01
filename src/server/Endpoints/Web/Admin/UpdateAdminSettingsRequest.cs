// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Admin;

/// <summary>
/// Request body for updating server configuration settings.
/// </summary>
public sealed class UpdateAdminSettingsRequest
{
    /// <summary>
    /// The settings to update.
    /// </summary>
    public List<SettingUpdateEntry> Settings { get; set; } = new();
}
