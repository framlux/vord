// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Services.Notifications;

/// <summary>
/// Tests for <see cref="NoOpEmailService"/>. Skipped is terminal success: callers must never
/// retry it, so this service must never report Failed.
/// </summary>
public sealed class NoOpEmailServiceTests
{
    /// <summary>
    /// An invitation with no transport configured is skipped, not failed.
    /// </summary>
    [Test]
    public async Task SendInvitationEmailAsync_ReturnsSkipped()
    {
        NoOpEmailService service = new(NullLogger<NoOpEmailService>.Instance);

        EmailDeliveryOutcome outcome = await service.SendInvitationEmailAsync(
            "a@example.com", "Acme", "Dana", "https://example.com", CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(EmailDeliveryOutcome.Skipped);
    }

    /// <summary>
    /// An alert email with no transport configured is skipped, not failed.
    /// </summary>
    [Test]
    public async Task SendAlertEmailAsync_ReturnsSkipped()
    {
        NoOpEmailService service = new(NullLogger<NoOpEmailService>.Instance);

        EmailDeliveryOutcome outcome = await service.SendAlertEmailAsync(
            "a@example.com", "subject", "<p>body</p>", CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(EmailDeliveryOutcome.Skipped);
    }
}
