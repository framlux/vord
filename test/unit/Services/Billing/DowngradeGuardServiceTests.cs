// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="DowngradeGuardService"/>.
/// </summary>
public class DowngradeGuardServiceTests
{
    private static (ITenantRepository repo, TestDatabaseFactory dbFactory) BuildRepoAndFactory()
    {
        TestDatabaseFactory dbFactory = new();
        ITenantRepository repo = new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());

        return (repo, dbFactory);
    }

    [Test]
    public async Task CanDowngradeFromTeamAsync_TenantAdminWithGitHub_ReturnsTrue()
    {
        (ITenantRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            UserAccount user = TestDataBuilder.BuildUser();
            user.AuthProvider = AuthProviderType.GitHub;
            user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

            UserTenantRole role = TestDataBuilder.BuildUserTenantRole(
                userId: user.Id, tenantId: 1, role: UserAccountRoles.TenantAdmin);
            await dbFactory.Context.InsertAsync(role);

            DowngradeGuardService service = new(repo);

            bool result = await service.CanDowngradeFromTeamAsync(1, CancellationToken.None);

            await Assert.That(result).IsTrue();
        }
    }

    [Test]
    public async Task CanDowngradeFromTeamAsync_TenantAdminWithGoogle_ReturnsTrue()
    {
        (ITenantRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            UserAccount user = TestDataBuilder.BuildUser();
            user.AuthProvider = AuthProviderType.Google;
            user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

            UserTenantRole role = TestDataBuilder.BuildUserTenantRole(
                userId: user.Id, tenantId: 1, role: UserAccountRoles.TenantAdmin);
            await dbFactory.Context.InsertAsync(role);

            DowngradeGuardService service = new(repo);

            bool result = await service.CanDowngradeFromTeamAsync(1, CancellationToken.None);

            await Assert.That(result).IsTrue();
        }
    }

    [Test]
    public async Task CanDowngradeFromTeamAsync_TenantAdminWithMicrosoft_ReturnsTrue()
    {
        (ITenantRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            UserAccount user = TestDataBuilder.BuildUser();
            user.AuthProvider = AuthProviderType.Microsoft;
            user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

            UserTenantRole role = TestDataBuilder.BuildUserTenantRole(
                userId: user.Id, tenantId: 1, role: UserAccountRoles.TenantAdmin);
            await dbFactory.Context.InsertAsync(role);

            DowngradeGuardService service = new(repo);

            bool result = await service.CanDowngradeFromTeamAsync(1, CancellationToken.None);

            await Assert.That(result).IsTrue();
        }
    }

    [Test]
    public async Task CanDowngradeFromTeamAsync_AllTenantAdminsUseCustomOidc_ReturnsFalse()
    {
        (ITenantRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            UserAccount user = TestDataBuilder.BuildUser();
            user.AuthProvider = AuthProviderType.CustomOidc;
            user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

            UserTenantRole role = TestDataBuilder.BuildUserTenantRole(
                userId: user.Id, tenantId: 1, role: UserAccountRoles.TenantAdmin);
            await dbFactory.Context.InsertAsync(role);

            DowngradeGuardService service = new(repo);

            bool result = await service.CanDowngradeFromTeamAsync(1, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    [Test]
    public async Task CanDowngradeFromTeamAsync_NoTenantAdmins_ReturnsFalse()
    {
        (ITenantRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            DowngradeGuardService service = new(repo);

            bool result = await service.CanDowngradeFromTeamAsync(1, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    [Test]
    public async Task CanDowngradeFromTeamAsync_MixOfCustomOidcAndSocial_ReturnsTrue()
    {
        (ITenantRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // First admin uses CustomOidc
            UserAccount oidcUser = TestDataBuilder.BuildUser();
            oidcUser.AuthProvider = AuthProviderType.CustomOidc;
            oidcUser.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(oidcUser);

            UserTenantRole oidcRole = TestDataBuilder.BuildUserTenantRole(
                userId: oidcUser.Id, tenantId: 1, role: UserAccountRoles.TenantAdmin);
            await dbFactory.Context.InsertAsync(oidcRole);

            // Second admin uses GitHub (social)
            UserAccount socialUser = TestDataBuilder.BuildUser();
            socialUser.AuthProvider = AuthProviderType.GitHub;
            socialUser.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(socialUser);

            UserTenantRole socialRole = TestDataBuilder.BuildUserTenantRole(
                userId: socialUser.Id, tenantId: 1, role: UserAccountRoles.TenantAdmin);
            await dbFactory.Context.InsertAsync(socialRole);

            DowngradeGuardService service = new(repo);

            bool result = await service.CanDowngradeFromTeamAsync(1, CancellationToken.None);

            await Assert.That(result).IsTrue();
        }
    }

    [Test]
    public async Task CanDowngradeFromTeamAsync_TenantAdminWithUnknownProvider_ReturnsTrue()
    {
        (ITenantRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            UserAccount user = TestDataBuilder.BuildUser();
            user.AuthProvider = AuthProviderType.Unknown;
            user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

            UserTenantRole role = TestDataBuilder.BuildUserTenantRole(
                userId: user.Id, tenantId: 1, role: UserAccountRoles.TenantAdmin);
            await dbFactory.Context.InsertAsync(role);

            DowngradeGuardService service = new(repo);

            bool result = await service.CanDowngradeFromTeamAsync(1, CancellationToken.None);

            await Assert.That(result).IsTrue();
        }
    }

    [Test]
    public async Task CanDowngradeFromTeamAsync_OnlyInactiveTenantAdminsHaveSocialLogin_ReturnsFalse()
    {
        (ITenantRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // Active user with CustomOidc in an active role
            UserAccount activeOidcUser = TestDataBuilder.BuildUser();
            activeOidcUser.AuthProvider = AuthProviderType.CustomOidc;
            activeOidcUser.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(activeOidcUser);

            UserTenantRole activeRole = TestDataBuilder.BuildUserTenantRole(
                userId: activeOidcUser.Id, tenantId: 1, role: UserAccountRoles.TenantAdmin);
            await dbFactory.Context.InsertAsync(activeRole);

            // Social-login user with an inactive role
            UserAccount socialUser = TestDataBuilder.BuildUser();
            socialUser.AuthProvider = AuthProviderType.GitHub;
            socialUser.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(socialUser);

            UserTenantRole inactiveRole = TestDataBuilder.BuildUserTenantRole(
                userId: socialUser.Id, tenantId: 1, role: UserAccountRoles.TenantAdmin);
            inactiveRole.IsActive = false;
            await dbFactory.Context.InsertAsync(inactiveRole);

            DowngradeGuardService service = new(repo);

            bool result = await service.CanDowngradeFromTeamAsync(1, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    [Test]
    public async Task CanDowngradeFromTeamAsync_InactiveUserWithSocialLogin_ReturnsFalse()
    {
        (ITenantRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // Inactive user with social login
            UserAccount inactiveUser = TestDataBuilder.BuildUser(isActive: false);
            inactiveUser.AuthProvider = AuthProviderType.GitHub;
            inactiveUser.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(inactiveUser);

            UserTenantRole role = TestDataBuilder.BuildUserTenantRole(
                userId: inactiveUser.Id, tenantId: 1, role: UserAccountRoles.TenantAdmin);
            await dbFactory.Context.InsertAsync(role);

            DowngradeGuardService service = new(repo);

            bool result = await service.CanDowngradeFromTeamAsync(1, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }

    [Test]
    public async Task CanDowngradeFromTeamAsync_DifferentTenant_ReturnsFalse()
    {
        (ITenantRepository repo, TestDatabaseFactory dbFactory) = BuildRepoAndFactory();
        using (dbFactory)
        {
            // Social-login admin exists but for a different tenant
            UserAccount user = TestDataBuilder.BuildUser();
            user.AuthProvider = AuthProviderType.GitHub;
            user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

            UserTenantRole role = TestDataBuilder.BuildUserTenantRole(
                userId: user.Id, tenantId: 99, role: UserAccountRoles.TenantAdmin);
            await dbFactory.Context.InsertAsync(role);

            DowngradeGuardService service = new(repo);

            // Querying tenant 1 should not see tenant 99's admin
            bool result = await service.CanDowngradeFromTeamAsync(1, CancellationToken.None);

            await Assert.That(result).IsFalse();
        }
    }
}
