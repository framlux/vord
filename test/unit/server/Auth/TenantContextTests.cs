// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Auth;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="TenantContext"/>.
/// </summary>
public sealed class TenantContextTests
{
    [Test]
    public async Task RequireTenantId_WithTenant_ReturnsValue()
    {
        TenantContext context = new();
        context.Set(42, 7);

        await Assert.That(context.RequireTenantId()).IsEqualTo(42);
    }

    [Test]
    public async Task RequireTenantId_WithoutTenant_Throws()
    {
        TenantContext context = new();
        context.Set(null, null);

        await Assert.That(() => context.RequireTenantId()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RequireTenantId_NeverSet_Throws()
    {
        TenantContext context = new();

        await Assert.That(() => context.RequireTenantId()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RequireTenantId_TenantWithoutUser_ReturnsValue()
    {
        TenantContext context = new();
        context.Set(13, null);

        await Assert.That(context.RequireTenantId()).IsEqualTo(13);
    }
}
