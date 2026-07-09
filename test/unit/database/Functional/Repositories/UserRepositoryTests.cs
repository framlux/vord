// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for user-related methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class UserCacheTests
{
    [Test]
    public async Task CreateUserAccountAsync_ValidUser_ReturnsUserWithId()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser(externalId: "ext-create-1", username: "create1@example.com");

        UserAccount result = await cache.CreateUserAccountAsync(user);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsNotEqualTo(0);
        await Assert.That(result.ExternalId).IsEqualTo("ext-create-1");
        await Assert.That(result.Username).IsEqualTo("create1@example.com");
    }

    [Test]
    public async Task GetUserByExternalIdAsync_ExistingActiveUser_ReturnsUser()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser(externalId: "ext-lookup-1");
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserAccount? result = await cache.GetUserByExternalIdAsync("ext-lookup-1");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(userId);
        await Assert.That(result.ExternalId).IsEqualTo("ext-lookup-1");
    }

    [Test]
    public async Task GetUserByExternalIdAsync_NonExistentExternalId_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount? result = await cache.GetUserByExternalIdAsync("does-not-exist");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetUserByExternalIdAsync_InactiveUser_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser(externalId: "ext-inactive-1", isActive: false);
        await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserAccount? result = await cache.GetUserByExternalIdAsync("ext-inactive-1");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetUserByExternalIdForProviderAsync_MatchingProviderAndExternalId_ReturnsUser()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser(externalId: "shared-sub");
        user.AuthProvider = AuthProviderType.Google;
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserAccount? result = await cache.GetUserByExternalIdForProviderAsync(AuthProviderType.Google, "shared-sub");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(userId);
    }

    [Test]
    public async Task GetUserByExternalIdForProviderAsync_SameExternalIdDifferentProvider_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser(externalId: "shared-sub");
        user.AuthProvider = AuthProviderType.Google;
        await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserAccount? result = await cache.GetUserByExternalIdForProviderAsync(AuthProviderType.GitHub, "shared-sub");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetUserByExternalIdForProviderAsync_InactiveUser_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser(externalId: "provider-inactive", isActive: false);
        user.AuthProvider = AuthProviderType.Microsoft;
        await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserAccount? result = await cache.GetUserByExternalIdForProviderAsync(AuthProviderType.Microsoft, "provider-inactive");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetUserByExternalIdForProviderAsync_NullExternalId_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetUserByExternalIdForProviderAsync(AuthProviderType.Google, null!, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetUserByEmailAsync_ExistingActiveUser_ReturnsUser()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser(username: "email-lookup@example.com");
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserAccount? result = await cache.GetUserByEmailAsync("email-lookup@example.com");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(userId);
        await Assert.That(result.Username).IsEqualTo("email-lookup@example.com");
    }

    [Test]
    public async Task GetUserByEmailAsync_CaseInsensitiveMatch_ReturnsUser()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser(username: "CaseMix@Example.COM");
        await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserAccount? result = await cache.GetUserByEmailAsync("casemix@example.com");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Username).IsEqualTo("CaseMix@Example.COM");
    }

    [Test]
    public async Task GetUserByEmailAsync_InactiveUser_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser(username: "inactive-email@example.com", isActive: false);
        await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserAccount? result = await cache.GetUserByEmailAsync("inactive-email@example.com");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetUserByEmailAsync_NonExistentEmail_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount? result = await cache.GetUserByEmailAsync("nobody@nowhere.com");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task DoAnyUsersExistAsync_NoUsers_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        bool result = await cache.DoAnyUsersExistAsync();

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task DoAnyUsersExistAsync_ActiveNonSystemUser_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        bool result = await cache.DoAnyUsersExistAsync();

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task DoAnyUsersExistAsync_OnlyInactiveUsers_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser(isActive: false);
        await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        bool result = await cache.DoAnyUsersExistAsync();

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task UpdateUserEmailAsync_ExistingUser_UpdatesEmail()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser(username: "old@example.com");
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        await cache.UpdateUserEmailAsync(userId, "new@example.com");

        UserAccount? updated = await cache.GetUserByEmailAsync("new@example.com");

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Id).IsEqualTo(userId);
        await Assert.That(updated.Username).IsEqualTo("new@example.com");
    }

    // ========== DeactivateUserAccountAsync cross-tenant hardening tests ==========

    [Test]
    public async Task DeactivateUserAccountAsync_WrongTenant_ReturnsFalse_AndNoChange()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        // The user belonged to tenant 1 (role now inactive) but never to tenant 999. An acting tenant
        // the user never belonged to must not be able to deactivate the cross-tenant account.
        UserTenantRole role = TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: 1, isActive: false);
        await dbFactory.Context.InsertWithInt32IdentityAsync(role);

        bool result = await cache.DeactivateUserAccountAsync(userId, 999, currentUserId: userId);

        await Assert.That(result).IsFalse();

        UserAccount? unchanged = await cache.GetUserByIdAsync(userId);
        await Assert.That(unchanged).IsNotNull();
        await Assert.That(unchanged!.IsActive).IsTrue();
        await Assert.That(unchanged.DeletedOn).IsNull();
    }

    [Test]
    public async Task DeactivateUserAccountAsync_CorrectTenant_ReturnsTrue_AndChanges()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        // The user belonged to tenant 1 and now has no active role anywhere, mirroring the caller's
        // last-role invariant, so the account-level deactivation is allowed for the acting tenant.
        UserTenantRole role = TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: 1, isActive: false);
        await dbFactory.Context.InsertWithInt32IdentityAsync(role);

        bool result = await cache.DeactivateUserAccountAsync(userId, 1, currentUserId: userId);

        await Assert.That(result).IsTrue();

        UserAccount? changed = await cache.GetUserByIdAsync(userId);
        await Assert.That(changed).IsNotNull();
        await Assert.That(changed!.IsActive).IsFalse();
        await Assert.That(changed.DeletedOn).IsNotNull();
        await Assert.That(changed.DeletedByUserId).IsEqualTo(userId);
    }

    [Test]
    public async Task DeactivateUserAccountAsync_UserStillHasActiveRole_ReturnsFalse_AndNoChange()
    {
        using TestDatabaseFactory dbFactory = new();
        IUserRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        // The user still holds an active role in another tenant, so the platform-wide account
        // deactivation must not fire even for a tenant the user belonged to.
        UserTenantRole actingTenantRole = TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: 1, isActive: false);
        await dbFactory.Context.InsertWithInt32IdentityAsync(actingTenantRole);

        UserTenantRole otherActiveRole = TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: 2, isActive: true);
        await dbFactory.Context.InsertWithInt32IdentityAsync(otherActiveRole);

        bool result = await cache.DeactivateUserAccountAsync(userId, 1, currentUserId: userId);

        await Assert.That(result).IsFalse();

        UserAccount? unchanged = await cache.GetUserByIdAsync(userId);
        await Assert.That(unchanged).IsNotNull();
        await Assert.That(unchanged!.IsActive).IsTrue();
    }
}
