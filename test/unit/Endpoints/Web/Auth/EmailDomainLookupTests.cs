// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Auth;

/// <summary>
/// Tests for email domain SSO lookup functionality used by <see cref="Framlux.FleetManagement.Server.Endpoints.Web.Auth.EmailDomainLookupEndpoint"/>.
/// Tests the underlying <see cref="DatabaseCache.GetTenantOidcConfigurationByEmailDomainAsync"/> method
/// which drives the endpoint behavior.
/// </summary>
public sealed class EmailDomainLookupTests
{
    [Test]
    public async Task LookupByDomain_ValidDomain_ReturnsOidcConfiguration()
    {
        using TestDatabaseFactory dbFactory = new();

        // Insert a tenant with OIDC configuration.
        await dbFactory.Context.InsertAsync(new TenantOidcConfiguration
        {
            TenantId = 1,
            Authority = "https://login.example.com",
            ClientId = "client-123",
            ClientSecret = "secret-456",
            EmailDomain = "example.com",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        ILogger<DatabaseCache> logger = new NullLogger<DatabaseCache>();
        DatabaseCache cache = new(dbFactory.Context, logger);

        TenantOidcConfiguration? result = await cache.GetTenantOidcConfigurationByEmailDomainAsync("example.com");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.TenantId).IsEqualTo(1);
        await Assert.That(result.Authority).IsEqualTo("https://login.example.com");
    }

    [Test]
    public async Task LookupByDomain_UnknownDomain_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();

        ILogger<DatabaseCache> logger = new NullLogger<DatabaseCache>();
        DatabaseCache cache = new(dbFactory.Context, logger);

        TenantOidcConfiguration? result = await cache.GetTenantOidcConfigurationByEmailDomainAsync("unknown.com");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task LookupByDomain_DisabledConfig_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();

        // Insert a disabled OIDC configuration.
        await dbFactory.Context.InsertAsync(new TenantOidcConfiguration
        {
            TenantId = 1,
            Authority = "https://login.disabled.com",
            ClientId = "client-789",
            ClientSecret = "secret-012",
            EmailDomain = "disabled.com",
            IsEnabled = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        ILogger<DatabaseCache> logger = new NullLogger<DatabaseCache>();
        DatabaseCache cache = new(dbFactory.Context, logger);

        TenantOidcConfiguration? result = await cache.GetTenantOidcConfigurationByEmailDomainAsync("disabled.com");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task LookupByDomain_CaseInsensitive_ReturnsConfiguration()
    {
        using TestDatabaseFactory dbFactory = new();

        // Email domains are normalized to lowercase at storage time.
        await dbFactory.Context.InsertAsync(new TenantOidcConfiguration
        {
            TenantId = 1,
            Authority = "https://login.mixedcase.com",
            ClientId = "client-case",
            ClientSecret = "secret-case",
            EmailDomain = "mixedcase.com",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        ILogger<DatabaseCache> logger = new NullLogger<DatabaseCache>();
        DatabaseCache cache = new(dbFactory.Context, logger);

        // Query with mixed case — service normalizes input to lowercase.
        TenantOidcConfiguration? result = await cache.GetTenantOidcConfigurationByEmailDomainAsync("MixedCase.COM");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.TenantId).IsEqualTo(1);
    }
}
