// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Admin;

/// <summary>
/// A single setting update with key and new value.
/// </summary>
public sealed class SettingUpdateEntry
{
    /// <summary>
    /// The setting key to update.
    /// </summary>
    public int Key { get; set; }

    /// <summary>
    /// The new value for the setting.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}
