// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Billing;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Unit tests for <see cref="ProSubscriptionPreProcessor.RequiresProGate"/>, the consolidated
/// Pro+ gating decision shared by the alert endpoints.
/// </summary>
public sealed class ProSubscriptionPreProcessorTests
{
    private static TenantSubscription Build(SubscriptionTier tier, SubscriptionStatus status)
    {
        return new TenantSubscription
        {
            Id = 1,
            TenantId = 1,
            Tier = tier,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Test]
    public async Task NullSubscription_Denied()
    {
        await Assert.That(ProSubscriptionPreProcessor.RequiresProGate(null)).IsTrue();
    }

    [Test]
    public async Task FreeTier_Denied()
    {
        await Assert.That(ProSubscriptionPreProcessor.RequiresProGate(Build(SubscriptionTier.Free, SubscriptionStatus.Active))).IsTrue();
    }

    [Test]
    public async Task ProActive_Allowed()
    {
        await Assert.That(ProSubscriptionPreProcessor.RequiresProGate(Build(SubscriptionTier.Pro, SubscriptionStatus.Active))).IsFalse();
    }

    [Test]
    public async Task TeamActive_Allowed()
    {
        await Assert.That(ProSubscriptionPreProcessor.RequiresProGate(Build(SubscriptionTier.Team, SubscriptionStatus.Active))).IsFalse();
    }

    [Test]
    public async Task ProButCanceled_Denied()
    {
        await Assert.That(ProSubscriptionPreProcessor.RequiresProGate(Build(SubscriptionTier.Pro, SubscriptionStatus.Canceled))).IsTrue();
    }

    [Test]
    public async Task ProButPastDue_Denied()
    {
        await Assert.That(ProSubscriptionPreProcessor.RequiresProGate(Build(SubscriptionTier.Pro, SubscriptionStatus.PastDue))).IsTrue();
    }
}
