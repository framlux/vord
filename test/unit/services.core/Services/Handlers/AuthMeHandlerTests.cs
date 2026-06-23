// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="AuthMeHandler"/>.
/// </summary>
public class AuthMeHandlerTests
{
    [Test]
    public async Task GetCurrentUserAsync_UserNotFound_Returns404()
    {
        IUserRepository userRepository = Substitute.For<IUserRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        userRepository.GetUserByExternalIdForProviderAsync(Arg.Any<AuthProviderType>(), "unknown", Arg.Any<CancellationToken>()).Returns((UserAccount?)null);
        AuthMeHandler handler = new(userRepository, tenantRepository);

        ServiceResult<AuthMeResult> result = await handler.GetCurrentUserAsync(AuthProviderType.Google, "unknown", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetCurrentUserAsync_ForwardsAuthProvider_AndMissMapsToNotFound()
    {
        IUserRepository userRepository = Substitute.For<IUserRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        userRepository.GetUserByExternalIdForProviderAsync(AuthProviderType.GitHub, "ext-provider", Arg.Any<CancellationToken>()).Returns((UserAccount?)null);
        AuthMeHandler handler = new(userRepository, tenantRepository);

        ServiceResult<AuthMeResult> result = await handler.GetCurrentUserAsync(AuthProviderType.GitHub, "ext-provider", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
        await userRepository.Received(1).GetUserByExternalIdForProviderAsync(AuthProviderType.GitHub, "ext-provider", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetCurrentUserAsync_ResolvesTenantsByUserId_NotExternalId()
    {
        IUserRepository userRepository = Substitute.For<IUserRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        UserAccount user = new()
        {
            Id = 42,
            ExternalId = "ext-shared",
            Username = "user@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        userRepository.GetUserByExternalIdForProviderAsync(Arg.Any<AuthProviderType>(), "ext-shared", Arg.Any<CancellationToken>()).Returns(user);
        tenantRepository.GetTenantsForUserByIdAsync(42, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        AuthMeHandler handler = new(userRepository, tenantRepository);

        await handler.GetCurrentUserAsync(AuthProviderType.Google, "ext-shared", CancellationToken.None);

        await tenantRepository.Received(1).GetTenantsForUserByIdAsync(42, Arg.Any<CancellationToken>());
        await tenantRepository.DidNotReceive().GetTenantsForUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetCurrentUserAsync_UserFoundNoTenants_ReturnsNeedsOnboarding()
    {
        IUserRepository userRepository = Substitute.For<IUserRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        UserAccount user = new()
        {
            Id = 1,
            ExternalId = "ext-1",
            Username = "user@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        userRepository.GetUserByExternalIdForProviderAsync(Arg.Any<AuthProviderType>(), "ext-1", Arg.Any<CancellationToken>()).Returns(user);
        tenantRepository.GetTenantsForUserByIdAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        AuthMeHandler handler = new(userRepository, tenantRepository);

        ServiceResult<AuthMeResult> result = await handler.GetCurrentUserAsync(AuthProviderType.Google, "ext-1", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.NeedsOnboarding).IsTrue();
        await Assert.That(result.Data!.Tenants.Count).IsEqualTo(0);
        await Assert.That(result.Data!.UserId).IsEqualTo(1);
    }

    [Test]
    public async Task GetCurrentUserAsync_UserWithTenants_ReturnsTenants()
    {
        IUserRepository userRepository = Substitute.For<IUserRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        UserAccount user = new()
        {
            Id = 5,
            ExternalId = "ext-5",
            Username = "admin@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = true,
        };
        userRepository.GetUserByExternalIdForProviderAsync(Arg.Any<AuthProviderType>(), "ext-5", Arg.Any<CancellationToken>()).Returns(user);

        Tenant tenant = new()
        {
            Id = 10,
            Name = "Test Org",
            ExternalId = "ext-tenant-10",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 5,
            IsActive = true,
            LogoUrl = "",
        };
        List<UserTenantRole> roles = new()
        {
            new UserTenantRole
            {
                UserId = 5,
                AssignedTenantId = 10,
                AssignedTenant = tenant,
                Role = UserAccountRoles.TenantAdmin,
                AssignedByUserId = 5,
                AssignedAt = DateTimeOffset.UtcNow,
                IsActive = true,
            }
        };
        tenantRepository.GetTenantsForUserByIdAsync(5, Arg.Any<CancellationToken>()).Returns(roles);
        AuthMeHandler handler = new(userRepository, tenantRepository);

        ServiceResult<AuthMeResult> result = await handler.GetCurrentUserAsync(AuthProviderType.Google, "ext-5", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.IsGlobalAdmin).IsTrue();
        await Assert.That(result.Data!.NeedsOnboarding).IsFalse();
        await Assert.That(result.Data!.Tenants.Count).IsEqualTo(1);
        await Assert.That(result.Data!.Tenants[0].TenantId).IsEqualTo(10);
        await Assert.That(result.Data!.Tenants[0].TenantName).IsEqualTo("Test Org");
    }

    [Test]
    public async Task GetCurrentUserAsync_NonAdmin_ReturnsIsGlobalAdminFalse()
    {
        IUserRepository userRepository = Substitute.For<IUserRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        UserAccount user = new()
        {
            Id = 3,
            ExternalId = "ext-3",
            Username = "viewer@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        userRepository.GetUserByExternalIdForProviderAsync(Arg.Any<AuthProviderType>(), "ext-3", Arg.Any<CancellationToken>()).Returns(user);
        tenantRepository.GetTenantsForUserByIdAsync(3, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        AuthMeHandler handler = new(userRepository, tenantRepository);

        ServiceResult<AuthMeResult> result = await handler.GetCurrentUserAsync(AuthProviderType.Google, "ext-3", CancellationToken.None);

        await Assert.That(result.Data!.IsGlobalAdmin).IsFalse();
    }
}
