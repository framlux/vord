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
    public async Task ReportMachineUsageAsync_AlwaysReturnsTrue()
    {
        bool result = await _client.ReportMachineUsageAsync("tenant-123", 5, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task CancelSubscriptionAsync_AlwaysReturnsTrue()
    {
        bool result = await _client.CancelSubscriptionAsync("tenant-123", "CancelAccount", CancellationToken.None);

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
    public async Task GetUpcomingInvoiceAsync_ReturnsNull()
    {
        UpcomingInvoiceResult? result = await _client.GetUpcomingInvoiceAsync("tenant-123", CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ListInvoicesAsync_ReturnsEmptyList()
    {
        List<InvoiceResult> results = await _client.ListInvoicesAsync("tenant-123", 12, CancellationToken.None);

        await Assert.That(results).IsNotNull();
        await Assert.That(results.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SwapSubscriptionPriceAsync_ReturnsTrue()
    {
        bool result = await _client.SwapSubscriptionPriceAsync("tenant-123", "pro", CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ResumeSubscriptionAsync_ReturnsTrue()
    {
        bool result = await _client.ResumeSubscriptionAsync("tenant-123", CancellationToken.None);

        await Assert.That(result).IsTrue();
    }
}
