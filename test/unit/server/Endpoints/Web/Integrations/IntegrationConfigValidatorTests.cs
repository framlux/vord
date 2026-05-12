// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Services.Core.Models.Integrations;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Integrations;

/// <summary>
/// Unit tests for <see cref="IntegrationConfigValidator"/>.
/// </summary>
public sealed class IntegrationConfigValidatorTests
{
    // ================================================================
    // Slack provider tests
    // ================================================================

    [Test]
    public async Task Slack_ValidWebhookUrl_ReturnsNull()
    {
        Dictionary<string, string> config = new()
        {
            ["webhookUrl"] = "https://hooks.slack.com/services/T00000/B00000/XXXXX"
        };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.Slack, config);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Slack_MissingWebhookUrl_ReturnsError()
    {
        Dictionary<string, string> config = new();

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.Slack, config);

        await Assert.That(result).IsEqualTo("Slack integration requires a webhookUrl configuration value");
    }

    [Test]
    public async Task Slack_EmptyWebhookUrl_ReturnsError()
    {
        Dictionary<string, string> config = new() { ["webhookUrl"] = "" };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.Slack, config);

        await Assert.That(result).IsEqualTo("Slack integration requires a webhookUrl configuration value");
    }

    [Test]
    public async Task Slack_WhitespaceWebhookUrl_ReturnsError()
    {
        Dictionary<string, string> config = new() { ["webhookUrl"] = "   " };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.Slack, config);

        await Assert.That(result).IsEqualTo("Slack integration requires a webhookUrl configuration value");
    }

    [Test]
    public async Task Slack_UrlMissingSlackDomain_ReturnsError()
    {
        Dictionary<string, string> config = new()
        {
            ["webhookUrl"] = "https://example.com/webhook"
        };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.Slack, config);

        await Assert.That(result).IsEqualTo("Slack webhook URL must contain 'hooks.slack.com/services/'");
    }

    // ================================================================
    // Microsoft Teams provider tests
    // ================================================================

    [Test]
    public async Task MicrosoftTeams_ValidWebhookUrl_ReturnsNull()
    {
        Dictionary<string, string> config = new()
        {
            ["webhookUrl"] = "https://example.webhook.office.com/webhookb2/abc123"
        };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.MicrosoftTeams, config);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task MicrosoftTeams_MissingWebhookUrl_ReturnsError()
    {
        Dictionary<string, string> config = new();

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.MicrosoftTeams, config);

        await Assert.That(result).IsEqualTo("Microsoft Teams integration requires a webhookUrl configuration value");
    }

    [Test]
    public async Task MicrosoftTeams_UrlMissingTeamsDomain_ReturnsError()
    {
        Dictionary<string, string> config = new()
        {
            ["webhookUrl"] = "https://example.com/webhook"
        };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.MicrosoftTeams, config);

        await Assert.That(result).IsEqualTo("Microsoft Teams webhook URL must contain 'webhook.office.com/'");
    }

    // ================================================================
    // Discord provider tests
    // ================================================================

    [Test]
    public async Task Discord_ValidWebhookUrl_ReturnsNull()
    {
        Dictionary<string, string> config = new()
        {
            ["webhookUrl"] = "https://discord.com/api/webhooks/123456789/abcdef"
        };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.Discord, config);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Discord_MissingWebhookUrl_ReturnsError()
    {
        Dictionary<string, string> config = new();

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.Discord, config);

        await Assert.That(result).IsEqualTo("Discord integration requires a webhookUrl configuration value");
    }

    [Test]
    public async Task Discord_UrlMissingDiscordDomain_ReturnsError()
    {
        Dictionary<string, string> config = new()
        {
            ["webhookUrl"] = "https://example.com/webhook"
        };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.Discord, config);

        await Assert.That(result).IsEqualTo("Discord webhook URL must contain 'discord.com/api/webhooks/'");
    }

    // ================================================================
    // PagerDuty provider tests
    // ================================================================

    [Test]
    public async Task PagerDuty_Valid32CharHexRoutingKey_ReturnsNull()
    {
        Dictionary<string, string> config = new()
        {
            ["routingKey"] = "0123456789abcdef0123456789abcdef"
        };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.PagerDuty, config);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task PagerDuty_MissingRoutingKey_ReturnsError()
    {
        Dictionary<string, string> config = new();

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.PagerDuty, config);

        await Assert.That(result).IsEqualTo("PagerDuty integration requires a routingKey configuration value");
    }

    [Test]
    public async Task PagerDuty_EmptyRoutingKey_ReturnsError()
    {
        Dictionary<string, string> config = new() { ["routingKey"] = "" };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.PagerDuty, config);

        await Assert.That(result).IsEqualTo("PagerDuty integration requires a routingKey configuration value");
    }

    [Test]
    public async Task PagerDuty_RoutingKeyTooShort_ReturnsError()
    {
        Dictionary<string, string> config = new()
        {
            ["routingKey"] = "0123456789abcdef"
        };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.PagerDuty, config);

        await Assert.That(result).IsEqualTo("PagerDuty routing key must be a 32-character hexadecimal string");
    }

    [Test]
    public async Task PagerDuty_RoutingKeyTooLong_ReturnsError()
    {
        Dictionary<string, string> config = new()
        {
            ["routingKey"] = "0123456789abcdef0123456789abcdef0"
        };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.PagerDuty, config);

        await Assert.That(result).IsEqualTo("PagerDuty routing key must be a 32-character hexadecimal string");
    }

    [Test]
    public async Task PagerDuty_RoutingKeyNonHex_ReturnsError()
    {
        Dictionary<string, string> config = new()
        {
            ["routingKey"] = "0123456789abcdefGHIJKLMN01234567"
        };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.PagerDuty, config);

        await Assert.That(result).IsEqualTo("PagerDuty routing key must be a 32-character hexadecimal string");
    }

    [Test]
    public async Task PagerDuty_UppercaseHexRoutingKey_ReturnsNull()
    {
        Dictionary<string, string> config = new()
        {
            ["routingKey"] = "0123456789ABCDEF0123456789ABCDEF"
        };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.PagerDuty, config);

        await Assert.That(result).IsNull();
    }

    // ================================================================
    // Custom provider tests
    // ================================================================

    [Test]
    public async Task Custom_ValidHttpsUrl_ReturnsNull()
    {
        Dictionary<string, string> config = new()
        {
            ["url"] = "https://example.com/webhook"
        };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.Custom, config);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Custom_MissingUrl_ReturnsError()
    {
        Dictionary<string, string> config = new();

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.Custom, config);

        await Assert.That(result).IsEqualTo("Custom integration requires a url configuration value");
    }

    [Test]
    public async Task Custom_HttpUrl_ReturnsError()
    {
        Dictionary<string, string> config = new()
        {
            ["url"] = "http://example.com/webhook"
        };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.Custom, config);

        await Assert.That(result).IsEqualTo("Custom integration URL must start with 'https://'");
    }

    [Test]
    public async Task Custom_EmptyUrl_ReturnsError()
    {
        Dictionary<string, string> config = new() { ["url"] = "" };

        string? result = IntegrationConfigValidator.ValidateProviderConfiguration(
            IntegrationProvider.Custom, config);

        await Assert.That(result).IsEqualTo("Custom integration requires a url configuration value");
    }

    // ================================================================
    // IsHexString tests
    // ================================================================

    [Test]
    public async Task IsHexString_LowercaseHex_ReturnsTrue()
    {
        bool result = IntegrationConfigValidator.IsHexString("0123456789abcdef");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsHexString_UppercaseHex_ReturnsTrue()
    {
        bool result = IntegrationConfigValidator.IsHexString("0123456789ABCDEF");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsHexString_MixedCaseHex_ReturnsTrue()
    {
        bool result = IntegrationConfigValidator.IsHexString("aAbBcCdDeEfF");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsHexString_ContainsNonHexCharacter_ReturnsFalse()
    {
        bool result = IntegrationConfigValidator.IsHexString("abcdefgh");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsHexString_ContainsSpace_ReturnsFalse()
    {
        bool result = IntegrationConfigValidator.IsHexString("abcd ef01");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsHexString_EmptyString_ReturnsTrue()
    {
        // An empty string has no non-hex characters, so the loop completes without returning false
        bool result = IntegrationConfigValidator.IsHexString("");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsHexString_SingleDigit_ReturnsTrue()
    {
        bool result = IntegrationConfigValidator.IsHexString("f");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsHexString_SpecialCharacters_ReturnsFalse()
    {
        bool result = IntegrationConfigValidator.IsHexString("!@#$%^&*");

        await Assert.That(result).IsFalse();
    }
}
