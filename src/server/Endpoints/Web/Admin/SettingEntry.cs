// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Admin;

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
    /// The setting value.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}
