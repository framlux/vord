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
}
