// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Services.Core.ServerConfiguration;

/// <summary>
/// Single source of truth for server-configuration-setting validation, shared by the REST admin path
/// and the gRPC fleet-admin path so both reject the same values with the same messages.
/// </summary>
public static class ServerSettingValidation
{
    /// <summary>
    /// Valid min/max bounds for each numeric server configuration setting key.
    /// </summary>
    public static readonly Dictionary<ServerConfigurationSettingKeys, (int Min, int Max)> Bounds = new()
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

    /// <summary>
    /// Validates a single setting key/value pair. Returns <c>null</c> when valid, or a human-readable
    /// error message describing the first constraint violated.
    /// </summary>
    /// <param name="key">The setting key being updated.</param>
    /// <param name="value">The proposed value.</param>
    /// <returns>An error message, or <c>null</c> when the update is valid.</returns>
    public static string? Validate(ServerConfigurationSettingKeys key, string? value)
    {
        if ((Enum.IsDefined(key) == false) || (key == ServerConfigurationSettingKeys.None))
        {
            return $"Invalid setting key: {(int)key}";
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return $"Value must not be empty for key: {(int)key}";
        }

        if (key == ServerConfigurationSettingKeys.AllowUserSignup)
        {
            if ((string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) == false) &&
                (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) == false))
            {
                return "AllowUserSignup must be 'true' or 'false'.";
            }

            return null;
        }

        string name = Enum.GetName(key) ?? key.ToString();

        if ((int.TryParse(value, out int parsed) == false) || (parsed <= 0))
        {
            return $"{name} must be a positive integer.";
        }

        if (Bounds.TryGetValue(key, out (int Min, int Max) bounds))
        {
            if ((parsed < bounds.Min) || (parsed > bounds.Max))
            {
                return $"{name} must be between {bounds.Min} and {bounds.Max}.";
            }
        }

        return null;
    }
}
