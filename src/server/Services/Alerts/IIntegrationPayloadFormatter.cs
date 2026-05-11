// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Server.Services.Alerts;

/// <summary>
/// Formats alert event payloads into provider-specific HTTP requests for delivery.
/// </summary>
public interface IIntegrationPayloadFormatter
{
    /// <summary>The provider this formatter handles.</summary>
    IntegrationProvider Provider { get; }

    /// <summary>
    /// Builds an HTTP request message with the correctly formatted payload for the target provider.
    /// </summary>
    /// <param name="alertEvent">The alert event to deliver.</param>
    /// <param name="rule">The alert rule that triggered the event.</param>
    /// <param name="integration">The integration endpoint containing provider configuration.</param>
    /// <returns>A ready-to-send HTTP request message.</returns>
    HttpRequestMessage FormatRequest(AlertEvent alertEvent, AlertRule rule, IntegrationEndpoint integration);
}
