// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Web.Auth;
using Microsoft.AspNetCore.DataProtection;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Unit tests for the email-domain lookup logic extracted from the endpoint:
/// <see cref="EmailDomainLookupEndpoint.ExtractDomain"/> and the opaque
/// <see cref="TenantSsoSlug"/> build/resolve round-trip.
/// </summary>
public sealed class EmailDomainLookupLogicTests
{
    [Test]
    [Arguments("user@example.com", "example.com")]
    [Arguments("  USER@Example.COM  ", "example.com")]
    [Arguments("first.last@sub.domain.co", "sub.domain.co")]
    public async Task ExtractDomain_ValidEmail_ReturnsLowercaseDomain(string email, string expected)
    {
        string? domain = EmailDomainLookupEndpoint.ExtractDomain(email);

        await Assert.That(domain).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not-an-email")]
    [Arguments("user@")]
    [Arguments("user@localhost")]
    [Arguments("user@.com")]
    public async Task ExtractDomain_InvalidEmail_ReturnsNull(string? email)
    {
        string? domain = EmailDomainLookupEndpoint.ExtractDomain(email);

        await Assert.That(domain).IsNull();
    }

    [Test]
    public async Task BuildSlug_DoesNotContainDecimalTenantId()
    {
        EphemeralDataProtectionProvider provider = new();
        IDataProtector protector = provider.CreateProtector(TenantSsoSlug.Purpose);

        const int tenantId = 123456;
        string slug = TenantSsoSlug.Build(protector, tenantId);

        await Assert.That(slug.Contains(tenantId.ToString())).IsFalse();
    }

    [Test]
    public async Task BuildSlug_RoundTripsBackToTenantId()
    {
        EphemeralDataProtectionProvider provider = new();
        IDataProtector protector = provider.CreateProtector(TenantSsoSlug.Purpose);

        const int tenantId = 987;
        string slug = TenantSsoSlug.Build(protector, tenantId);

        bool resolved = TenantSsoSlug.TryResolve(protector, slug, out int result);

        await Assert.That(resolved).IsTrue();
        await Assert.That(result).IsEqualTo(tenantId);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not-a-valid-protected-token")]
    public async Task TryResolve_InvalidSlug_ReturnsFalse(string? slug)
    {
        EphemeralDataProtectionProvider provider = new();
        IDataProtector protector = provider.CreateProtector(TenantSsoSlug.Purpose);

        bool resolved = TenantSsoSlug.TryResolve(protector, slug, out int result);

        await Assert.That(resolved).IsFalse();
        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task TryResolve_DifferentPurpose_ReturnsFalse()
    {
        EphemeralDataProtectionProvider provider = new();
        IDataProtector buildProtector = provider.CreateProtector(TenantSsoSlug.Purpose);
        IDataProtector wrongProtector = provider.CreateProtector("SomeOtherPurpose");

        string slug = TenantSsoSlug.Build(buildProtector, 42);

        bool resolved = TenantSsoSlug.TryResolve(wrongProtector, slug, out int result);

        await Assert.That(resolved).IsFalse();
        await Assert.That(result).IsEqualTo(0);
    }
}
