// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Admin;

/// <summary>
/// A single configuration setting.
/// </summary>
public sealed class SettingEntry
{
    /// <summary>
    /// The setting key.
    /// </summary>
    public int Key { get; set; }

    /// <summary>
    /// The human-readable name of the setting key.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// A description of what this setting controls.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The setting value.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// The minimum allowed value, if this setting has a defined lower bound.
    /// </summary>
    public int? Min { get; set; }

    /// <summary>
    /// The maximum allowed value, if this setting has a defined upper bound.
    /// </summary>
    public int? Max { get; set; }
}
