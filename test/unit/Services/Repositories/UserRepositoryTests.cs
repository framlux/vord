// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Services.Cache;

/// <summary>
/// Tests for <see cref="DatabaseRepository"/> user cache methods, specifically UpdateUserAuthProviderAsync.
/// </summary>
public class UserCacheTests
{
    private static DatabaseRepository CreateCache(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }

    [Test]
    public async Task UpdateUserAuthProviderAsync_UpdatesProviderOnExistingUser()
    {
        using TestDatabaseFactory dbFactory = new();
        UserAccount user = TestDataBuilder.BuildUser();
        user.AuthProvider = AuthProviderType.GitHub;
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        DatabaseRepository cache = CreateCache(dbFactory);

        await cache.UpdateUserAuthProviderAsync(user.Id, AuthProviderType.Google, CancellationToken.None);

        UserAccount? updated = await dbFactory.Context.UserAccounts
            .FirstOrDefaultAsync(u => u.Id == user.Id);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.AuthProvider).IsEqualTo(AuthProviderType.Google);
    }

    [Test]
    public async Task UpdateUserAuthProviderAsync_ChangesToCustomOidc()
    {
        using TestDatabaseFactory dbFactory = new();
        UserAccount user = TestDataBuilder.BuildUser();
        user.AuthProvider = AuthProviderType.GitHub;
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        DatabaseRepository cache = CreateCache(dbFactory);

        await cache.UpdateUserAuthProviderAsync(user.Id, AuthProviderType.CustomOidc, CancellationToken.None);

        UserAccount? updated = await dbFactory.Context.UserAccounts
            .FirstOrDefaultAsync(u => u.Id == user.Id);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.AuthProvider).IsEqualTo(AuthProviderType.CustomOidc);
    }

    [Test]
    public async Task UpdateUserAuthProviderAsync_ChangesFromCustomOidcToSocial()
    {
        using TestDatabaseFactory dbFactory = new();
        UserAccount user = TestDataBuilder.BuildUser();
        user.AuthProvider = AuthProviderType.CustomOidc;
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        DatabaseRepository cache = CreateCache(dbFactory);

        await cache.UpdateUserAuthProviderAsync(user.Id, AuthProviderType.Microsoft, CancellationToken.None);

        UserAccount? updated = await dbFactory.Context.UserAccounts
            .FirstOrDefaultAsync(u => u.Id == user.Id);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.AuthProvider).IsEqualTo(AuthProviderType.Microsoft);
    }

    [Test]
    public async Task UpdateUserAuthProviderAsync_UserDoesNotExist_NoError()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = CreateCache(dbFactory);

        // Should not throw when user does not exist
        await cache.UpdateUserAuthProviderAsync(999, AuthProviderType.Google, CancellationToken.None);

        int count = await dbFactory.Context.UserAccounts.CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateUserAuthProviderAsync_DoesNotChangeOtherFields()
    {
        using TestDatabaseFactory dbFactory = new();
        UserAccount user = TestDataBuilder.BuildUser(username: "original@example.com");
        user.AuthProvider = AuthProviderType.GitHub;
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        DatabaseRepository cache = CreateCache(dbFactory);

        await cache.UpdateUserAuthProviderAsync(user.Id, AuthProviderType.Google, CancellationToken.None);

        UserAccount? updated = await dbFactory.Context.UserAccounts
            .FirstOrDefaultAsync(u => u.Id == user.Id);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.AuthProvider).IsEqualTo(AuthProviderType.Google);
        await Assert.That(updated.Username).IsEqualTo("original@example.com");
        await Assert.That(updated.IsActive).IsTrue();
        await Assert.That(updated.ExternalId).IsEqualTo(user.ExternalId);
    }

    [Test]
    public async Task UpdateUserAuthProviderAsync_SameProvider_NoError()
    {
        using TestDatabaseFactory dbFactory = new();
        UserAccount user = TestDataBuilder.BuildUser();
        user.AuthProvider = AuthProviderType.GitHub;
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        DatabaseRepository cache = CreateCache(dbFactory);

        // Setting the same provider should be a no-op but not error
        await cache.UpdateUserAuthProviderAsync(user.Id, AuthProviderType.GitHub, CancellationToken.None);

        UserAccount? updated = await dbFactory.Context.UserAccounts
            .FirstOrDefaultAsync(u => u.Id == user.Id);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.AuthProvider).IsEqualTo(AuthProviderType.GitHub);
    }

    [Test]
    public async Task UpdateUserAuthProviderAsync_ChangesToUnknown()
    {
        using TestDatabaseFactory dbFactory = new();
        UserAccount user = TestDataBuilder.BuildUser();
        user.AuthProvider = AuthProviderType.Google;
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        DatabaseRepository cache = CreateCache(dbFactory);

        await cache.UpdateUserAuthProviderAsync(user.Id, AuthProviderType.Unknown, CancellationToken.None);

        UserAccount? updated = await dbFactory.Context.UserAccounts
            .FirstOrDefaultAsync(u => u.Id == user.Id);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.AuthProvider).IsEqualTo(AuthProviderType.Unknown);
    }
}
