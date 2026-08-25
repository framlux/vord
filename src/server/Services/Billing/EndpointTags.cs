// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// Tags applied to endpoints in Configure() to opt in or out of the behavior enforced by the
/// global pre-processors (subscription gating, tenant-scope enforcement).
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

    /// <summary>
    /// Endpoints with this tag require the Team tier. The shared
    /// <see cref="TeamSubscriptionPreProcessor"/> returns 403 for tenants on any other tier or with
    /// no subscription. Unlike the Pro gate this is tier-only and does not consult status, matching
    /// the hand-written checks it replaced.
    /// </summary>
    public const string RequiresTeamSubscription = "RequiresTeamSubscription";

    /// <summary>
    /// Endpoints with this tag require a tenant scope on the request. The
    /// <see cref="Tenancy.TenantContextPreProcessor"/> rejects tenant-less requests with a
    /// 401 before the handler runs, so tagged handlers may call
    /// <c>ITenantContext.RequireTenantId()</c> without a null check.
    /// </summary>
    public const string RequiresTenant = "RequiresTenant";
}
