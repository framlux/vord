// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Notifications;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="EmailDeliveryOutcome"/>.
/// </summary>
public sealed class EmailDeliveryOutcomeTests
{
    /// <summary>
    /// Skipped must hold the zero slot so a default-initialised or unstubbed value reads as
    /// "nothing was sent" rather than "the provider accepted the message".
    /// </summary>
    [Test]
    public async Task EmailDeliveryOutcome_DefaultIsSkipped()
    {
        EmailDeliveryOutcome outcome = default;

        await Assert.That(outcome).IsEqualTo(EmailDeliveryOutcome.Skipped);
    }
}
