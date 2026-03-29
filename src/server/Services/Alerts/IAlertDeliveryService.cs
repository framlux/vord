// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Server.Services.Alerts;

/// <summary>
/// Service for delivering alert notifications via email and webhook.
/// </summary>
public interface IAlertDeliveryService
{
    /// <summary>
    /// Delivers an alert event via configured notification channels.
    /// </summary>
    /// <param name="alertEvent">The alert event to deliver.</param>
    /// <param name="rule">The alert rule that triggered the event.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeliverAsync(AlertEvent alertEvent, AlertRule rule, CancellationToken ct);
}
