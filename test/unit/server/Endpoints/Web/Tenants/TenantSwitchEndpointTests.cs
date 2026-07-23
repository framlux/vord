// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Test.Endpoints.Web.Tenants;

/// <summary>
/// Unit tests for <see cref="TenantSwitchEndpoint.CanSwitchToTenant(bool, bool)"/>.
/// </summary>
public sealed class TenantSwitchEndpointTests
{
    [Test]
    public async Task Member_ActiveTenant_CanSwitch()
    {
        bool result = TenantSwitchEndpoint.CanSwitchToTenant(isMember: true, tenantIsActive: true);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task Member_DeactivatedTenant_CannotSwitch()
    {
        bool result = TenantSwitchEndpoint.CanSwitchToTenant(isMember: true, tenantIsActive: false);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task NonMember_ActiveTenant_CannotSwitch()
    {
        bool result = TenantSwitchEndpoint.CanSwitchToTenant(isMember: false, tenantIsActive: true);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task NonMember_DeactivatedTenant_CannotSwitch()
    {
        bool result = TenantSwitchEndpoint.CanSwitchToTenant(isMember: false, tenantIsActive: false);

        await Assert.That(result).IsFalse();
    }
}
