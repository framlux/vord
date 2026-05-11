// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Integrations;

/// <summary>
/// Shared validation logic for integration endpoint configuration.
/// </summary>
internal static class IntegrationConfigValidator
{
    /// <summary>
    /// Validates that the configuration dictionary contains valid values for the specified provider.
    /// Returns null if valid, or an error message if invalid.
    /// </summary>
    public static string? ValidateProviderConfiguration(IntegrationProvider provider, Dictionary<string, string> configuration)
    {
        switch (provider)
        {
            case IntegrationProvider.Slack:
                if (configuration.TryGetValue("webhookUrl", out string? slackUrl) == false ||
                    string.IsNullOrWhiteSpace(slackUrl))
                {
                    return "Slack integration requires a webhookUrl configuration value";
                }

                if (slackUrl.Contains("hooks.slack.com/services/") == false)
                {
                    return "Slack webhook URL must contain 'hooks.slack.com/services/'";
                }

                break;

            case IntegrationProvider.MicrosoftTeams:
                if (configuration.TryGetValue("webhookUrl", out string? teamsUrl) == false ||
                    string.IsNullOrWhiteSpace(teamsUrl))
                {
                    return "Microsoft Teams integration requires a webhookUrl configuration value";
                }

                if (teamsUrl.Contains("webhook.office.com/") == false)
                {
                    return "Microsoft Teams webhook URL must contain 'webhook.office.com/'";
                }

                break;

            case IntegrationProvider.Discord:
                if (configuration.TryGetValue("webhookUrl", out string? discordUrl) == false ||
                    string.IsNullOrWhiteSpace(discordUrl))
                {
                    return "Discord integration requires a webhookUrl configuration value";
                }

                if (discordUrl.Contains("discord.com/api/webhooks/") == false)
                {
                    return "Discord webhook URL must contain 'discord.com/api/webhooks/'";
                }

                break;

            case IntegrationProvider.PagerDuty:
                if (configuration.TryGetValue("routingKey", out string? routingKey) == false ||
                    string.IsNullOrWhiteSpace(routingKey))
                {
                    return "PagerDuty integration requires a routingKey configuration value";
                }

                if ((routingKey.Length != 32) || (IsHexString(routingKey) == false))
                {
                    return "PagerDuty routing key must be a 32-character hexadecimal string";
                }

                break;

            case IntegrationProvider.Custom:
                if (configuration.TryGetValue("url", out string? customUrl) == false ||
                    string.IsNullOrWhiteSpace(customUrl))
                {
                    return "Custom integration requires a url configuration value";
                }

                if (customUrl.StartsWith("https://") == false)
                {
                    return "Custom integration URL must start with 'https://'";
                }

                break;
        }

        return null;
    }

    /// <summary>
    /// Checks whether a string consists entirely of hexadecimal characters.
    /// </summary>
    public static bool IsHexString(string value)
    {
        foreach (char c in value)
        {
            bool isHex = ((c >= '0') && (c <= '9')) ||
                         ((c >= 'a') && (c <= 'f')) ||
                         ((c >= 'A') && (c <= 'F'));
            if (isHex == false)
            {
                return false;
            }
        }

        return true;
    }
}
