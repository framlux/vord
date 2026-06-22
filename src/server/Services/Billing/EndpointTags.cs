// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// Tag applied to endpoints that are exempt from subscription enforcement.
/// Apply this tag in Configure() to allow access regardless of subscription status.
/// </summary>
public static class EndpointTags
{
    /// <summary>
    /// Endpoints with this tag are exempt from subscription enforcement.
    /// </summary>
    public const string SubscriptionExempt = "SubscriptionExempt";

    /// <summary>
    /// Endpoints with this tag require an active Pro or Team subscription. The shared
    /// <see cref="ProSubscriptionPreProcessor"/> returns 403 for tenants on Free, with no
    /// subscription, or whose subscription is not Active.
    /// </summary>
    public const string RequiresProSubscription = "RequiresProSubscription";
}
