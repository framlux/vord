// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Tenancy;

namespace Framlux.FleetManagement.Test.Services.Tenancy;

/// <summary>
/// Unit tests for <see cref="TenantContextPreProcessor.ShouldReject"/>, the gating decision for
/// endpoints tagged <see cref="EndpointTags.RequiresTenant"/>.
/// </summary>
public sealed class TenantContextPreProcessorTests
{
    [Test]
    public async Task TaggedEndpoint_NoTenant_Rejected()
    {
        string[] tags = [EndpointTags.RequiresTenant];

        await Assert.That(TenantContextPreProcessor.ShouldReject(tags, null)).IsTrue();
    }

    [Test]
    public async Task TaggedEndpoint_WithTenant_Allowed()
    {
        string[] tags = [EndpointTags.RequiresTenant];

        await Assert.That(TenantContextPreProcessor.ShouldReject(tags, 42)).IsFalse();
    }

    [Test]
    public async Task UntaggedEndpoint_NoTenant_Allowed()
    {
        string[] tags = [EndpointTags.SubscriptionExempt];

        await Assert.That(TenantContextPreProcessor.ShouldReject(tags, null)).IsFalse();
    }

    [Test]
    public async Task NullTags_NoTenant_Allowed()
    {
        await Assert.That(TenantContextPreProcessor.ShouldReject(null, null)).IsFalse();
    }

    [Test]
    public async Task EmptyTags_NoTenant_Allowed()
    {
        await Assert.That(TenantContextPreProcessor.ShouldReject([], null)).IsFalse();
    }

    [Test]
    public async Task TagAmongOthers_NoTenant_Rejected()
    {
        string[] tags = [EndpointTags.SubscriptionExempt, EndpointTags.RequiresTenant];

        await Assert.That(TenantContextPreProcessor.ShouldReject(tags, null)).IsTrue();
    }
}
