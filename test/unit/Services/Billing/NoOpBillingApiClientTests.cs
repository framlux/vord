// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Billing;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="NoOpBillingApiClient"/>.
/// </summary>
public sealed class NoOpBillingApiClientTests
{
    private readonly NoOpBillingApiClient _client = new();

    [Test]
    public async Task UpdateQuantityAsync_AlwaysReturnsTrue()
    {
        bool result = await _client.UpdateQuantityAsync("tenant-123", 5, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task CancelSubscriptionAsync_AlwaysReturnsTrue()
    {
        bool result = await _client.CancelSubscriptionAsync("tenant-123", CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task GetSubscriptionStatusAsync_ReturnsFalseAndNone()
    {
        StripeSubscriptionStatus status = await _client.GetSubscriptionStatusAsync("tenant-123", CancellationToken.None);

        await Assert.That(status.CancelAtPeriodEnd).IsFalse();
        await Assert.That(status.StripeStatus).IsEqualTo("none");
        await Assert.That(status.PriceId).IsEqualTo(string.Empty);
        await Assert.That(status.Quantity).IsEqualTo(0);
        await Assert.That(status.CurrentPeriodEnd).IsNull();
    }

    [Test]
    public async Task UpdateQuantityAsync_NullTenantExternalId_DoesNotThrow()
    {
        bool result = await _client.UpdateQuantityAsync(null!, 5, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task UpdateQuantityAsync_ZeroMachineCount_DoesNotThrow()
    {
        bool result = await _client.UpdateQuantityAsync("tenant-123", 0, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task CancelSubscriptionAsync_NullTenantExternalId_DoesNotThrow()
    {
        bool result = await _client.CancelSubscriptionAsync(null!, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task GetSubscriptionStatusAsync_EmptyString_ReturnsDefaultStatus()
    {
        StripeSubscriptionStatus status = await _client.GetSubscriptionStatusAsync("", CancellationToken.None);

        await Assert.That(status.CancelAtPeriodEnd).IsFalse();
        await Assert.That(status.StripeStatus).IsEqualTo("none");
    }
}
