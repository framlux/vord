// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="ServerSettingValidation"/> — the Stripe canary setting keys and the
/// heartbeat/online-threshold relationship.
/// </summary>
public class ServerSettingValidationTests
{
    [Test]
    public async Task Validate_StripeCanaryEnabled_RejectsNonBool()
    {
        string? error = ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryEnabled, "maybe");

        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Validate_StripeCanaryEnabled_AcceptsTrueAndFalse()
    {
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryEnabled, "true")).IsNull();
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryEnabled, "false")).IsNull();
    }

    [Test]
    public async Task Validate_StripeCanaryIntervalSeconds_EnforcesBounds()
    {
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryIntervalSeconds, "29")).IsNotNull();
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryIntervalSeconds, "3601")).IsNotNull();
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryIntervalSeconds, "60")).IsNull();
    }

    [Test]
    public async Task Validate_StripeCanaryWebhookTimeoutSeconds_EnforcesBounds()
    {
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryWebhookTimeoutSeconds, "4")).IsNotNull();
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryWebhookTimeoutSeconds, "301")).IsNotNull();
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryWebhookTimeoutSeconds, "40")).IsNull();
    }

    [Test]
    public async Task Validate_StripeCanaryConsecutiveFailuresToAlert_EnforcesBounds()
    {
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryConsecutiveFailuresToAlert, "0")).IsNotNull();
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryConsecutiveFailuresToAlert, "101")).IsNotNull();
        await Assert.That(ServerSettingValidation.Validate(ServerConfigurationSettingKeys.StripeCanaryConsecutiveFailuresToAlert, "3")).IsNull();
    }

    [Test]
    public async Task Validate_OnlineThresholdBelowItsBound_IsRejected()
    {
        // Unbounded until now: the panel accepted 1, which would mark an entire fleet offline.
        string? error = ServerSettingValidation.Validate(
            ServerConfigurationSettingKeys.OnlineThresholdSeconds, "1");

        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Validate_OnlineThresholdAboveItsBound_IsRejected()
    {
        await Assert.That(ServerSettingValidation.Validate(
            ServerConfigurationSettingKeys.OnlineThresholdSeconds, "3601")).IsNotNull();
    }

    [Test]
    public async Task Validate_OnlineThresholdWithinItsBounds_IsAccepted()
    {
        await Assert.That(ServerSettingValidation.Validate(
            ServerConfigurationSettingKeys.OnlineThresholdSeconds, "60")).IsNull();
        await Assert.That(ServerSettingValidation.Validate(
            ServerConfigurationSettingKeys.OnlineThresholdSeconds, "3600")).IsNull();
    }

    [Test]
    public async Task ValidateRelationships_ThresholdUnderTwiceTheHeartbeat_IsRejected()
    {
        // Equal values plus the agent's ±15% jitter is what made a healthy machine flap offline.
        Dictionary<ServerConfigurationSettingKeys, string> resulting = new()
        {
            [ServerConfigurationSettingKeys.AgentHeartbeatSeconds] = "300",
            [ServerConfigurationSettingKeys.OnlineThresholdSeconds] = "300",
        };

        await Assert.That(ServerSettingValidation.ValidateRelationships(resulting)).IsNotNull();
    }

    [Test]
    public async Task ValidateRelationships_ThresholdExactlyTwiceTheHeartbeat_IsAccepted()
    {
        Dictionary<ServerConfigurationSettingKeys, string> resulting = new()
        {
            [ServerConfigurationSettingKeys.AgentHeartbeatSeconds] = "150",
            [ServerConfigurationSettingKeys.OnlineThresholdSeconds] = "300",
        };

        await Assert.That(ServerSettingValidation.ValidateRelationships(resulting)).IsNull();
    }

    [Test]
    public async Task ValidateRelationships_RaisingBothTogether_IsAccepted()
    {
        // The admin panel submits every setting on every save, so a batch that moves both must be
        // judged on its resulting state. Judging each key against the stored values would reject
        // this, and would also permanently lock an already-violating deployment out of its own
        // settings tab — including out of the batch that repairs it.
        Dictionary<ServerConfigurationSettingKeys, string> resulting = new()
        {
            [ServerConfigurationSettingKeys.AgentHeartbeatSeconds] = "200",
            [ServerConfigurationSettingKeys.OnlineThresholdSeconds] = "600",
        };

        await Assert.That(ServerSettingValidation.ValidateRelationships(resulting)).IsNull();
    }

    [Test]
    public async Task ValidateRelationships_RepairingAnAlreadyViolatingDeployment_IsAccepted()
    {
        Dictionary<ServerConfigurationSettingKeys, string> resulting = new()
        {
            [ServerConfigurationSettingKeys.AgentHeartbeatSeconds] = "150",
            [ServerConfigurationSettingKeys.OnlineThresholdSeconds] = "300",
        };

        await Assert.That(ServerSettingValidation.ValidateRelationships(resulting)).IsNull();
    }

    [Test]
    public async Task ValidateRelationships_TheShippedDefaults_AreAccepted()
    {
        Dictionary<ServerConfigurationSettingKeys, string> resulting = new()
        {
            [ServerConfigurationSettingKeys.AgentHeartbeatSeconds] = ServerSettingDefaults.AgentHeartbeatSeconds.ToString(),
            [ServerConfigurationSettingKeys.OnlineThresholdSeconds] = ServerSettingDefaults.OnlineThresholdSeconds.ToString(),
        };

        await Assert.That(ServerSettingValidation.ValidateRelationships(resulting)).IsNull();
    }

    [Test]
    public async Task ValidateRelationships_MissingKeys_FallBackToTheShippedDefaults()
    {
        // A deployment that has never written either row still has effective values, so the rule
        // must judge the defaults rather than treat an absent row as unconstrained.
        Dictionary<ServerConfigurationSettingKeys, string> resulting = new();

        await Assert.That(ServerSettingValidation.ValidateRelationships(resulting)).IsNull();
    }

    [Test]
    public async Task ValidateRelationships_NullDictionary_Throws()
    {
        await Assert.That(() => ServerSettingValidation.ValidateRelationships(null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task ValidateRelationships_NamesBothSettingsSoTheConflictIsDiscoverable()
    {
        // The gRPC path can only submit one key at a time, so the message has to say what the other
        // side of the conflict is or the operator cannot work out the ordering to save in.
        Dictionary<ServerConfigurationSettingKeys, string> resulting = new()
        {
            [ServerConfigurationSettingKeys.AgentHeartbeatSeconds] = "300",
            [ServerConfigurationSettingKeys.OnlineThresholdSeconds] = "300",
        };

        string? error = ServerSettingValidation.ValidateRelationships(resulting);

        await Assert.That(error).Contains("OnlineThresholdSeconds");
        await Assert.That(error).Contains("AgentHeartbeatSeconds");
    }
}
