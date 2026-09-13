// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Services.Core.ServerConfiguration;

/// <summary>
/// Single source of truth for server-configuration-setting validation, shared by the REST admin path
/// and the gRPC fleet-admin path so both reject the same values with the same messages.
/// </summary>
public static class ServerSettingValidation
{
    /// <summary>
    /// Multiple of the heartbeat interval the online threshold must clear. The agent jitters its
    /// heartbeat by ±15%, so a threshold at or near the interval marks a healthy machine offline on
    /// roughly half its cycles; doubling leaves margin that jitter cannot cross.
    /// </summary>
    private const int HeartbeatSafetyFactor = 2;

    /// <summary>
    /// Valid min/max bounds for each numeric server configuration setting key.
    /// </summary>
    public static readonly Dictionary<ServerConfigurationSettingKeys, (int Min, int Max)> Bounds = new()
    {
        [ServerConfigurationSettingKeys.AgentHeartbeatSeconds] = (10, 600),
        [ServerConfigurationSettingKeys.OnlineThresholdSeconds] = (60, 3600),
        [ServerConfigurationSettingKeys.AgentConfigRefreshSeconds] = (60, 86400),
        [ServerConfigurationSettingKeys.AgentCommandPollSeconds] = (10, 300),
        [ServerConfigurationSettingKeys.TelemetryCollectFastSeconds] = (10, 300),
        [ServerConfigurationSettingKeys.TelemetryCollectSlowSeconds] = (60, 3600),
        [ServerConfigurationSettingKeys.TelemetrySendFastSeconds] = (5, 120),
        [ServerConfigurationSettingKeys.TelemetrySendSlowSeconds] = (30, 1800),
        [ServerConfigurationSettingKeys.ServiceStatusSeconds] = (60, 86400),
        [ServerConfigurationSettingKeys.StripeCanaryIntervalSeconds] = (30, 3600),
        [ServerConfigurationSettingKeys.StripeCanaryWebhookTimeoutSeconds] = (5, 300),
        [ServerConfigurationSettingKeys.StripeCanaryConsecutiveFailuresToAlert] = (1, 100),
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

        if ((key == ServerConfigurationSettingKeys.AllowUserSignup) ||
            (key == ServerConfigurationSettingKeys.StripeCanaryEnabled))
        {
            if ((string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) == false) &&
                (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) == false))
            {
                return $"{Enum.GetName(key)} must be 'true' or 'false'.";
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

    /// <summary>
    /// Validates the constraints that hold between settings rather than within one. The caller
    /// passes the state the save would produce — the current values overlaid with every proposed
    /// value — because the admin panel submits every setting on every save, so checking one
    /// proposed value against the stored others would reject a batch that moves both and would lock
    /// an already-violating deployment out of the batch that repairs it.
    /// </summary>
    /// <param name="resulting">
    /// The resulting values of every setting, keyed by setting. Keys absent from the dictionary fall
    /// back to their shipped defaults, which is what a deployment with no row for them behaves as.
    /// </param>
    /// <returns>An error message, or <c>null</c> when the resulting state is valid.</returns>
    public static string? ValidateRelationships(IReadOnlyDictionary<ServerConfigurationSettingKeys, string> resulting)
    {
        ArgumentNullException.ThrowIfNull(resulting);

        int heartbeat = ReadInt(
            resulting,
            ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            ServerSettingDefaults.AgentHeartbeatSeconds);
        int threshold = ReadInt(
            resulting,
            ServerConfigurationSettingKeys.OnlineThresholdSeconds,
            ServerSettingDefaults.OnlineThresholdSeconds);

        if (threshold < (heartbeat * HeartbeatSafetyFactor))
        {
            return $"OnlineThresholdSeconds ({threshold}) must be at least {HeartbeatSafetyFactor}x " +
                $"AgentHeartbeatSeconds ({heartbeat}). Raise the threshold to " +
                $"{heartbeat * HeartbeatSafetyFactor} or lower the heartbeat to " +
                $"{threshold / HeartbeatSafetyFactor}.";
        }

        return null;
    }

    private static int ReadInt(
        IReadOnlyDictionary<ServerConfigurationSettingKeys, string> values,
        ServerConfigurationSettingKeys key,
        int fallback)
    {
        if (values.TryGetValue(key, out string? raw) && int.TryParse(raw, out int parsed))
        {
            return parsed;
        }

        return fallback;
    }
}
