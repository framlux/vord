// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseCache;

/// <summary>
/// Functional tests for user-related methods on <see cref="Database.Cache.DatabaseCache"/>.
/// </summary>
public class UserCacheTests
{
    [Test]
    public async Task CreateUserAccountAsync_ValidUser_ReturnsUserWithId()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

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
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

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
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount? result = await cache.GetUserByExternalIdAsync("does-not-exist");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetUserByExternalIdAsync_InactiveUser_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser(externalId: "ext-inactive-1", isActive: false);
        await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserAccount? result = await cache.GetUserByExternalIdAsync("ext-inactive-1");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetUserByEmailAsync_ExistingActiveUser_ReturnsUser()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

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
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

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
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser(username: "inactive-email@example.com", isActive: false);
        await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserAccount? result = await cache.GetUserByEmailAsync("inactive-email@example.com");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetUserByEmailAsync_NonExistentEmail_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount? result = await cache.GetUserByEmailAsync("nobody@nowhere.com");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task DoAnyUsersExistAsync_NoUsers_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        bool result = await cache.DoAnyUsersExistAsync();

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task DoAnyUsersExistAsync_ActiveNonSystemUser_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser();
        await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        bool result = await cache.DoAnyUsersExistAsync();

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task DoAnyUsersExistAsync_OnlyInactiveUsers_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser(isActive: false);
        await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        bool result = await cache.DoAnyUsersExistAsync();

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task UpdateUserEmailAsync_ExistingUser_UpdatesEmail()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser(username: "old@example.com");
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        await cache.UpdateUserEmailAsync(userId, "new@example.com");

        UserAccount? updated = await cache.GetUserByEmailAsync("new@example.com");

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Id).IsEqualTo(userId);
        await Assert.That(updated.Username).IsEqualTo("new@example.com");
    }
}
