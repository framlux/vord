// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Admin;

/// <summary>
/// Settings DTO for admin panel.
/// </summary>
public sealed class ServerSettingsDto
{
    /// <summary>
    /// The settings entries.
    /// </summary>
    public List<SettingEntry> Settings { get; set; } = new();
}
