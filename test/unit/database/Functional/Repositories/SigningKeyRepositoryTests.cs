// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for signing-key and machine-authorization methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class SigningKeyRepositoryTests
{
    /// <summary>
    /// Seeds a user, tenant, and machine in the database.
    /// Signing key tests require these prerequisite records for FK satisfaction.
    /// </summary>
    private static async Task<(int userId, int tenantId, long machineId)> SeedUserTenantMachineAsync(TestDatabaseFactory dbFactory)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        return (userId, tenantId, machineId);
    }

    // ========== CreateSigningKeyAsync tests ==========

    [Test]
    public async Task CreateSigningKeyAsync_ValidKey_ReturnsKeyWithAssignedId()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long _) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);

        UserSigningKey result = await repo.CreateSigningKeyAsync(key);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsNotEqualTo(0);
        await Assert.That(result.UserId).IsEqualTo(userId);
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.Label).IsEqualTo(key.Label);
        await Assert.That(result.PublicKey).IsEqualTo(key.PublicKey);
        await Assert.That(result.PublicKeyFingerprint).IsEqualTo(key.PublicKeyFingerprint);
    }

    // ========== GetSigningKeysForUserAsync tests ==========

    [Test]
    public async Task GetSigningKeysForUserAsync_KeysExistForUser_ReturnsKeysOrderedByCreatedAtDesc()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long _) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey olderKey = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        olderKey.CreatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await repo.CreateSigningKeyAsync(olderKey);

        UserSigningKey newerKey = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        newerKey.CreatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await repo.CreateSigningKeyAsync(newerKey);

        List<UserSigningKey> result = await repo.GetSigningKeysForUserAsync(userId, tenantId);

        await Assert.That(result.Count).IsEqualTo(2);
        // Newest first due to OrderByDescending(CreatedAt)
        await Assert.That(result[0].CreatedAt >= result[1].CreatedAt).IsTrue();
    }

    [Test]
    public async Task GetSigningKeysForUserAsync_WrongTenant_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long _) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        await repo.CreateSigningKeyAsync(key);

        int otherTenantId = tenantId + 1000;
        List<UserSigningKey> result = await repo.GetSigningKeysForUserAsync(userId, otherTenantId);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetSigningKeysForUserAsync_NoKeys_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<UserSigningKey> result = await repo.GetSigningKeysForUserAsync(99999, 99999);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== GetActiveSigningKeyCountAsync tests ==========

    [Test]
    public async Task GetActiveSigningKeyCountAsync_CountsOnlyNonRevokedKeys()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long _) = await SeedUserTenantMachineAsync(dbFactory);

        // Create two active keys
        UserSigningKey activeKey1 = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        await repo.CreateSigningKeyAsync(activeKey1);

        UserSigningKey activeKey2 = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        await repo.CreateSigningKeyAsync(activeKey2);

        // Create one revoked key
        UserSigningKey revokedKey = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdRevoked = await repo.CreateSigningKeyAsync(revokedKey);
        await repo.RevokeSigningKeyAsync(createdRevoked.Id, userId);

        int count = await repo.GetActiveSigningKeyCountAsync(userId, tenantId);

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task GetActiveSigningKeyCountAsync_NoKeys_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        int count = await repo.GetActiveSigningKeyCountAsync(99999, 99999);

        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task GetActiveSigningKeyCountAsync_AllKeysRevoked_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long _) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey created = await repo.CreateSigningKeyAsync(key);
        await repo.RevokeSigningKeyAsync(created.Id, userId);

        int count = await repo.GetActiveSigningKeyCountAsync(userId, tenantId);

        await Assert.That(count).IsEqualTo(0);
    }

    // ========== GetSigningKeyByIdAsync tests ==========

    [Test]
    public async Task GetSigningKeyByIdAsync_ExistingKey_ReturnsKey()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long _) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey created = await repo.CreateSigningKeyAsync(key);

        UserSigningKey? result = await repo.GetSigningKeyByIdAsync(created.Id);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(created.Id);
        await Assert.That(result.UserId).IsEqualTo(userId);
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.Label).IsEqualTo(key.Label);
    }

    [Test]
    public async Task GetSigningKeyByIdAsync_NonExistentId_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserSigningKey? result = await repo.GetSigningKeyByIdAsync(99999);

        await Assert.That(result).IsNull();
    }

    // ========== RevokeSigningKeyAsync tests ==========

    [Test]
    public async Task RevokeSigningKeyAsync_ActiveKey_SetsRevokedAtAndRevokedByUserId()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long _) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey created = await repo.CreateSigningKeyAsync(key);

        // Verify key is not revoked before the operation
        UserSigningKey? beforeRevoke = await repo.GetSigningKeyByIdAsync(created.Id);
        await Assert.That(beforeRevoke).IsNotNull();
        await Assert.That(beforeRevoke!.RevokedAt).IsNull();
        await Assert.That(beforeRevoke.RevokedByUserId).IsNull();

        await repo.RevokeSigningKeyAsync(created.Id, userId);

        UserSigningKey? afterRevoke = await repo.GetSigningKeyByIdAsync(created.Id);

        await Assert.That(afterRevoke).IsNotNull();
        await Assert.That(afterRevoke!.RevokedAt).IsNotNull();
        await Assert.That(afterRevoke.RevokedByUserId).IsEqualTo(userId);
    }

    // ========== CreateMachineAuthorizationAsync tests ==========

    [Test]
    public async Task CreateMachineAuthorizationAsync_ValidAuth_ReturnsAuthWithAssignedId()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId,
            signingKeyId: createdKey.Id,
            tenantId: tenantId,
            authorizedByUserId: userId);

        MachineAuthorizedKey result = await repo.CreateMachineAuthorizationAsync(auth);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsNotEqualTo(0);
        await Assert.That(result.MachineId).IsEqualTo(machineId);
        await Assert.That(result.SigningKeyId).IsEqualTo(createdKey.Id);
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.AuthorizedByUserId).IsEqualTo(userId);
    }

    // ========== GetAuthorizedKeysForMachineAsync tests ==========

    [Test]
    public async Task GetAuthorizedKeysForMachineAsync_ActiveAndRevoked_ReturnsBothOrderedByAuthorizedAtDesc()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key1 = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey1 = await repo.CreateSigningKeyAsync(key1);

        UserSigningKey key2 = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey2 = await repo.CreateSigningKeyAsync(key2);

        // Create an older authorization and revoke it
        MachineAuthorizedKey olderAuth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey1.Id, tenantId: tenantId, authorizedByUserId: userId);
        olderAuth.AuthorizedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await repo.CreateMachineAuthorizationAsync(olderAuth);
        await repo.RevokeMachineAuthorizationAsync(machineId, createdKey1.Id, userId);

        // Create a newer active authorization
        MachineAuthorizedKey newerAuth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey2.Id, tenantId: tenantId, authorizedByUserId: userId);
        newerAuth.AuthorizedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await repo.CreateMachineAuthorizationAsync(newerAuth);

        List<MachineAuthorizedKey> result = await repo.GetAuthorizedKeysForMachineAsync(machineId);

        // Should return both active and revoked
        await Assert.That(result.Count).IsEqualTo(2);
        // Newest first due to OrderByDescending(AuthorizedAt)
        await Assert.That(result[0].AuthorizedAt >= result[1].AuthorizedAt).IsTrue();
    }

    [Test]
    public async Task GetAuthorizedKeysForMachineAsync_NoAuthorizations_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<MachineAuthorizedKey> result = await repo.GetAuthorizedKeysForMachineAsync(99999);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== GetActiveSigningKeysForMachineAsync tests ==========

    [Test]
    public async Task GetActiveSigningKeysForMachineAsync_OnlyActiveAuthAndActiveKey_ReturnsKey()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey.Id, tenantId: tenantId, authorizedByUserId: userId);
        await repo.CreateMachineAuthorizationAsync(auth);

        List<UserSigningKey> result = await repo.GetActiveSigningKeysForMachineAsync(machineId);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Id).IsEqualTo(createdKey.Id);
    }

    [Test]
    public async Task GetActiveSigningKeysForMachineAsync_RevokedAuth_ExcludesKey()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey.Id, tenantId: tenantId, authorizedByUserId: userId);
        await repo.CreateMachineAuthorizationAsync(auth);

        // Revoke the authorization
        await repo.RevokeMachineAuthorizationAsync(machineId, createdKey.Id, userId);

        List<UserSigningKey> result = await repo.GetActiveSigningKeysForMachineAsync(machineId);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetActiveSigningKeysForMachineAsync_RevokedSigningKey_ExcludesKey()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey.Id, tenantId: tenantId, authorizedByUserId: userId);
        await repo.CreateMachineAuthorizationAsync(auth);

        // Revoke the signing key itself (not the authorization)
        await repo.RevokeSigningKeyAsync(createdKey.Id, userId);

        List<UserSigningKey> result = await repo.GetActiveSigningKeysForMachineAsync(machineId);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetActiveSigningKeysForMachineAsync_NoAuthorizations_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<UserSigningKey> result = await repo.GetActiveSigningKeysForMachineAsync(99999);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== IsKeyAuthorizedForMachineAsync tests ==========

    [Test]
    public async Task IsKeyAuthorizedForMachineAsync_ActiveAuthorization_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey.Id, tenantId: tenantId, authorizedByUserId: userId);
        await repo.CreateMachineAuthorizationAsync(auth);

        bool result = await repo.IsKeyAuthorizedForMachineAsync(createdKey.Id, machineId);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsKeyAuthorizedForMachineAsync_RevokedAuthorization_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey.Id, tenantId: tenantId, authorizedByUserId: userId);
        await repo.CreateMachineAuthorizationAsync(auth);

        await repo.RevokeMachineAuthorizationAsync(machineId, createdKey.Id, userId);

        bool result = await repo.IsKeyAuthorizedForMachineAsync(createdKey.Id, machineId);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsKeyAuthorizedForMachineAsync_NonExistentAuthorization_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        bool result = await repo.IsKeyAuthorizedForMachineAsync(99999, 99999);

        await Assert.That(result).IsFalse();
    }

    // ========== RevokeMachineAuthorizationAsync tests ==========

    [Test]
    public async Task RevokeMachineAuthorizationAsync_ActiveRecord_SetsRevokedAtAndRevokedByUserId()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey.Id, tenantId: tenantId, authorizedByUserId: userId);
        MachineAuthorizedKey createdAuth = await repo.CreateMachineAuthorizationAsync(auth);

        // Verify the authorization is active before revoking
        bool isAuthorizedBefore = await repo.IsKeyAuthorizedForMachineAsync(createdKey.Id, machineId);
        await Assert.That(isAuthorizedBefore).IsTrue();

        await repo.RevokeMachineAuthorizationAsync(machineId, createdKey.Id, userId);

        // Verify the authorization is no longer active
        bool isAuthorizedAfter = await repo.IsKeyAuthorizedForMachineAsync(createdKey.Id, machineId);
        await Assert.That(isAuthorizedAfter).IsFalse();

        // Verify revocation fields were set by reading the record directly
        MachineAuthorizedKey? revokedAuth = await repo.GetRevokedAuthorizationAsync(machineId, createdKey.Id, tenantId);
        await Assert.That(revokedAuth).IsNotNull();
        await Assert.That(revokedAuth!.RevokedAt).IsNotNull();
        await Assert.That(revokedAuth.RevokedByUserId).IsEqualTo(userId);
    }

    [Test]
    public async Task RevokeMachineAuthorizationAsync_AlreadyRevoked_DoesNotUpdateAgain()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey.Id, tenantId: tenantId, authorizedByUserId: userId);
        await repo.CreateMachineAuthorizationAsync(auth);

        // Revoke once
        await repo.RevokeMachineAuthorizationAsync(machineId, createdKey.Id, userId);

        MachineAuthorizedKey? firstRevoke = await repo.GetRevokedAuthorizationAsync(machineId, createdKey.Id, tenantId);
        await Assert.That(firstRevoke).IsNotNull();
        DateTimeOffset? firstRevokedAt = firstRevoke!.RevokedAt;

        // Create a second user to attempt a second revocation
        UserAccount secondUser = TestDataBuilder.BuildUser();
        int secondUserId = await dbFactory.Context.InsertWithInt32IdentityAsync(secondUser);

        // Revoke again with different user -- should not update because the WHERE clause requires RevokedAt == null
        await repo.RevokeMachineAuthorizationAsync(machineId, createdKey.Id, secondUserId);

        MachineAuthorizedKey? secondRevoke = await repo.GetRevokedAuthorizationAsync(machineId, createdKey.Id, tenantId);
        await Assert.That(secondRevoke).IsNotNull();
        // The revoking user should still be the first user since the record was already revoked
        await Assert.That(secondRevoke!.RevokedByUserId).IsEqualTo(userId);
    }

    // ========== GetRevokedAuthorizationAsync tests ==========

    [Test]
    public async Task GetRevokedAuthorizationAsync_RevokedRecord_ReturnsAuth()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey.Id, tenantId: tenantId, authorizedByUserId: userId);
        await repo.CreateMachineAuthorizationAsync(auth);

        await repo.RevokeMachineAuthorizationAsync(machineId, createdKey.Id, userId);

        MachineAuthorizedKey? result = await repo.GetRevokedAuthorizationAsync(machineId, createdKey.Id, tenantId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.MachineId).IsEqualTo(machineId);
        await Assert.That(result.SigningKeyId).IsEqualTo(createdKey.Id);
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.RevokedAt).IsNotNull();
        await Assert.That(result.RevokedByUserId).IsEqualTo(userId);
    }

    [Test]
    public async Task GetRevokedAuthorizationAsync_ActiveRecord_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey.Id, tenantId: tenantId, authorizedByUserId: userId);
        await repo.CreateMachineAuthorizationAsync(auth);

        // Authorization is still active, so GetRevokedAuthorizationAsync should return null
        MachineAuthorizedKey? result = await repo.GetRevokedAuthorizationAsync(machineId, createdKey.Id, tenantId);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetRevokedAuthorizationAsync_WrongTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey.Id, tenantId: tenantId, authorizedByUserId: userId);
        await repo.CreateMachineAuthorizationAsync(auth);

        await repo.RevokeMachineAuthorizationAsync(machineId, createdKey.Id, userId);

        int otherTenantId = tenantId + 1000;
        MachineAuthorizedKey? result = await repo.GetRevokedAuthorizationAsync(machineId, createdKey.Id, otherTenantId);

        await Assert.That(result).IsNull();
    }

    // ========== ReactivateAuthorizationAsync tests ==========

    [Test]
    public async Task ReactivateAuthorizationAsync_RevokedRecord_ClearsRevocationAndUpdatesAuthorization()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey.Id, tenantId: tenantId, authorizedByUserId: userId);
        MachineAuthorizedKey createdAuth = await repo.CreateMachineAuthorizationAsync(auth);

        // Revoke the authorization
        await repo.RevokeMachineAuthorizationAsync(machineId, createdKey.Id, userId);

        // Create a second user for reactivation
        UserAccount reactivator = TestDataBuilder.BuildUser();
        int reactivatorId = await dbFactory.Context.InsertWithInt32IdentityAsync(reactivator);

        // Reactivate with a different user
        await repo.ReactivateAuthorizationAsync(createdAuth.Id, reactivatorId);

        // Verify that the authorization is active again
        MachineAuthorizedKey? reactivated = await repo.GetActiveAuthorizationAsync(machineId, createdKey.Id, tenantId);
        await Assert.That(reactivated).IsNotNull();
        await Assert.That(reactivated!.RevokedAt).IsNull();
        await Assert.That(reactivated.RevokedByUserId).IsNull();
        await Assert.That(reactivated.AuthorizedByUserId).IsEqualTo(reactivatorId);

        // Verify the record is no longer returned as revoked
        MachineAuthorizedKey? revoked = await repo.GetRevokedAuthorizationAsync(machineId, createdKey.Id, tenantId);
        await Assert.That(revoked).IsNull();
    }

    // ========== GetActiveAuthorizationAsync tests ==========

    [Test]
    public async Task GetActiveAuthorizationAsync_ActiveRecord_ReturnsAuth()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey.Id, tenantId: tenantId, authorizedByUserId: userId);
        await repo.CreateMachineAuthorizationAsync(auth);

        MachineAuthorizedKey? result = await repo.GetActiveAuthorizationAsync(machineId, createdKey.Id, tenantId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.MachineId).IsEqualTo(machineId);
        await Assert.That(result.SigningKeyId).IsEqualTo(createdKey.Id);
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.RevokedAt).IsNull();
    }

    [Test]
    public async Task GetActiveAuthorizationAsync_RevokedRecord_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey.Id, tenantId: tenantId, authorizedByUserId: userId);
        await repo.CreateMachineAuthorizationAsync(auth);

        await repo.RevokeMachineAuthorizationAsync(machineId, createdKey.Id, userId);

        MachineAuthorizedKey? result = await repo.GetActiveAuthorizationAsync(machineId, createdKey.Id, tenantId);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetActiveAuthorizationAsync_WrongTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId) = await SeedUserTenantMachineAsync(dbFactory);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        UserSigningKey createdKey = await repo.CreateSigningKeyAsync(key);

        MachineAuthorizedKey auth = TestDataBuilder.BuildMachineAuthorizedKey(
            machineId: machineId, signingKeyId: createdKey.Id, tenantId: tenantId, authorizedByUserId: userId);
        await repo.CreateMachineAuthorizationAsync(auth);

        int otherTenantId = tenantId + 1000;
        MachineAuthorizedKey? result = await repo.GetActiveAuthorizationAsync(machineId, createdKey.Id, otherTenantId);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetActiveAuthorizationAsync_NonExistentRecords_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        ISigningKeyRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        MachineAuthorizedKey? result = await repo.GetActiveAuthorizationAsync(99999, 99999, 99999);

        await Assert.That(result).IsNull();
    }
}
