// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Test.Models;

/// <summary>
/// Tests for <see cref="AlertEmailDeliveryAttempt"/>.
/// </summary>
public sealed class AlertEmailDeliveryAttemptTests
{
    [Test]
    public async Task Properties_SetAndGet_RoundTrip()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AlertEmailDeliveryAttempt attempt = new()
        {
            AlertEventId = 100,
            Recipient = "admin@example.com",
            Status = EmailDeliveryAttemptStatus.Pending,
            AttemptedAt = now,
            SucceededAt = null,
        };

        await Assert.That(attempt.AlertEventId).IsEqualTo(100L);
        await Assert.That(attempt.Recipient).IsEqualTo("admin@example.com");
        await Assert.That(attempt.Status).IsEqualTo(EmailDeliveryAttemptStatus.Pending);
        await Assert.That(attempt.AttemptedAt).IsEqualTo(now);
        await Assert.That(attempt.SucceededAt).IsNull();
    }
}
