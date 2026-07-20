// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// Feature-specific 403 messages attached to endpoints tagged
/// <see cref="EndpointTags.RequiresProSubscription"/> via <see cref="RequiresProFeatureMessage"/>
/// metadata. Kept as discoverable constants rather than inline strings so every endpoint that
/// gates on the same feature reports byte-identical wording.
/// </summary>
public static class ProFeatureMessages
{
    /// <summary>Message returned by the alert-rule and alert-event endpoints.</summary>
    public const string Alerting = "Alerting requires a Pro or Team subscription";

    /// <summary>Message returned by the integration-endpoint creation endpoint.</summary>
    public const string Integrations = "Integrations require a Pro or Team subscription";
}
