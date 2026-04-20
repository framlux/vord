// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Services.Machines;

/// <summary>
/// Tests for <see cref="MachineAuthorizedKeyService"/>.
/// Tests verify the intent of each business rule governing per-machine signing key authorization.
/// </summary>
public sealed class MachineAuthorizedKeyServiceTests
{
    /// <summary>
    /// Seeds a tenant, user, machine, and signing key into the test database and returns their IDs.
    /// </summary>
    private static async Task<(int TenantId, int UserId, long MachineId, int SigningKeyId)> SeedFullEnvironment(
        TestDatabaseFactory dbFactory,
        bool machineDeleted = false,
        bool signingKeyRevoked = false,
        int? signingKeyTenantOverride = null)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: user.Id);
        tenant.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.IsDeleted = machineDeleted;
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        int signingKeyTenantId = signingKeyTenantOverride ?? tenant.Id;
        UserSigningKey signingKey = TestDataBuilder.BuildSigningKey(userId: user.Id, tenantId: signingKeyTenantId);
        if (signingKeyRevoked)
        {
            signingKey.RevokedAt = DateTimeOffset.UtcNow.AddHours(-1);
        }

        signingKey.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(signingKey);

        return (tenant.Id, user.Id, machine.Id, signingKey.Id);
    }

    private static MachineAuthorizedKeyService CreateService(TestDatabaseFactory dbFactory)
    {
        IDatabaseCache cache = new DatabaseCache(dbFactory.Context, new NullLogger<DatabaseCache>());

        return new MachineAuthorizedKeyService(
            dbFactory.Context,
            cache,
            new NullLogger<MachineAuthorizedKeyService>());
    }

    // ========== AuthorizeKeyAsync — Happy path ==========

    [Test]
    public async Task AuthorizeKey_ValidKeyAndMachine_ReturnsSuccessWithCorrectFields()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedFullEnvironment(dbFactory);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        ServiceResult<MachineAuthorizedKey> result = await service.AuthorizeKeyAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data).IsNotNull();
        await Assert.That(result.Data!.MachineId).IsEqualTo(machineId);
        await Assert.That(result.Data!.SigningKeyId).IsEqualTo(signingKeyId);
        await Assert.That(result.Data!.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.Data!.AuthorizedByUserId).IsEqualTo(userId);
        await Assert.That(result.Data!.RevokedAt).IsNull();
    }

    // ========== AuthorizeKeyAsync — Audit log verification ==========

    [Test]
    public async Task AuthorizeKey_Success_CreatesAuditLogEntry()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedFullEnvironment(dbFactory);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        ServiceResult<MachineAuthorizedKey> result = await service.AuthorizeKeyAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);

        AuditLogEntry? auditEntry = await dbFactory.Context.AuditLog
            .FirstOrDefaultAsync(a => (a.Action == AuditAction.MachineKeyAuthorized) &&
                                      (a.MachineId == machineId) &&
                                      (a.TenantId == tenantId));

        await Assert.That(auditEntry).IsNotNull();
        await Assert.That(auditEntry!.UserId).IsEqualTo(userId);
        await Assert.That(auditEntry.ResourceType).IsEqualTo(AuditResourceType.MachineAuthorizedKey);
        await Assert.That(auditEntry.ResourceId).IsEqualTo(result.Data!.Id.ToString());
    }

    // ========== AuthorizeKeyAsync — Invalid tenant ==========

    [Test]
    public async Task AuthorizeKey_TenantIdZero_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        ServiceResult<MachineAuthorizedKey> result = await service.AuthorizeKeyAsync(
            1, 1, 1, 0, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task AuthorizeKey_TenantIdNegative_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        ServiceResult<MachineAuthorizedKey> result = await service.AuthorizeKeyAsync(
            1, 1, 1, -5, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    // ========== AuthorizeKeyAsync — Machine does not exist ==========

    [Test]
    public async Task AuthorizeKey_NonExistentMachine_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int userId, long _, int signingKeyId) = await SeedFullEnvironment(dbFactory);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        ServiceResult<MachineAuthorizedKey> result = await service.AuthorizeKeyAsync(
            99999, signingKeyId, userId, tenantId, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    // ========== AuthorizeKeyAsync — Machine in different tenant ==========

    [Test]
    public async Task AuthorizeKey_MachineInDifferentTenant_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedFullEnvironment(dbFactory);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        // Use a tenant ID that does not match the machine's tenant.
        int wrongTenantId = tenantId + 100;

        ServiceResult<MachineAuthorizedKey> result = await service.AuthorizeKeyAsync(
            machineId, signingKeyId, userId, wrongTenantId, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    // ========== AuthorizeKeyAsync — Soft-deleted machine ==========

    [Test]
    public async Task AuthorizeKey_DeletedMachine_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedFullEnvironment(dbFactory, machineDeleted: true);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        ServiceResult<MachineAuthorizedKey> result = await service.AuthorizeKeyAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    // ========== AuthorizeKeyAsync — Signing key does not exist ==========

    [Test]
    public async Task AuthorizeKey_NonExistentSigningKey_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int userId, long machineId, int _) = await SeedFullEnvironment(dbFactory);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        ServiceResult<MachineAuthorizedKey> result = await service.AuthorizeKeyAsync(
            machineId, 99999, userId, tenantId, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    // ========== AuthorizeKeyAsync — Signing key in different tenant ==========

    [Test]
    public async Task AuthorizeKey_SigningKeyInDifferentTenant_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        // Create environment where the signing key belongs to a different tenant.
        UserAccount user = TestDataBuilder.BuildUser();
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant1 = TestDataBuilder.BuildTenant(createdByUserId: user.Id);
        tenant1.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant1);

        Tenant tenant2 = TestDataBuilder.BuildTenant(createdByUserId: user.Id);
        tenant2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant1.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        UserSigningKey signingKey = TestDataBuilder.BuildSigningKey(userId: user.Id, tenantId: tenant2.Id);
        signingKey.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(signingKey);

        MachineAuthorizedKeyService service = CreateService(dbFactory);

        ServiceResult<MachineAuthorizedKey> result = await service.AuthorizeKeyAsync(
            machine.Id, signingKey.Id, user.Id, tenant1.Id, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    // ========== AuthorizeKeyAsync — Revoked signing key ==========

    [Test]
    public async Task AuthorizeKey_RevokedSigningKey_ReturnsBadRequest()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedFullEnvironment(dbFactory, signingKeyRevoked: true);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        ServiceResult<MachineAuthorizedKey> result = await service.AuthorizeKeyAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).Contains("revoked");
    }

    // ========== AuthorizeKeyAsync — Duplicate authorization ==========

    [Test]
    public async Task AuthorizeKey_AlreadyActiveAuthorization_ReturnsConflict()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedFullEnvironment(dbFactory);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        // First authorization should succeed.
        ServiceResult<MachineAuthorizedKey> firstResult = await service.AuthorizeKeyAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);
        await Assert.That(firstResult.IsSuccess).IsEqualTo(true);

        // Second authorization for the same key-machine pair should conflict.
        ServiceResult<MachineAuthorizedKey> secondResult = await service.AuthorizeKeyAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);

        await Assert.That(secondResult.StatusCode).IsEqualTo(409);
        await Assert.That(secondResult.ErrorMessage).Contains("already authorized");
    }

    // ========== AuthorizeKeyAsync — Re-authorize after revocation ==========

    [Test]
    public async Task AuthorizeKey_PreviouslyRevokedAuthorization_ReactivatesExistingRow()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedFullEnvironment(dbFactory);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        // Authorize, then revoke.
        ServiceResult<MachineAuthorizedKey> authResult = await service.AuthorizeKeyAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);
        await Assert.That(authResult.IsSuccess).IsEqualTo(true);
        int originalId = authResult.Data!.Id;

        ServiceResult<bool> revokeResult = await service.RevokeAuthorizationAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);
        await Assert.That(revokeResult.IsSuccess).IsEqualTo(true);

        // Re-authorization should succeed by reactivating the same row.
        ServiceResult<MachineAuthorizedKey> reAuthResult = await service.AuthorizeKeyAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);
        await Assert.That(reAuthResult.IsSuccess).IsEqualTo(true);
        await Assert.That(reAuthResult.Data!.Id).IsEqualTo(originalId);
        await Assert.That(reAuthResult.Data!.RevokedAt.HasValue).IsEqualTo(false);
        await Assert.That(reAuthResult.Data!.RevokedByUserId.HasValue).IsEqualTo(false);
    }

    // ========== RevokeAuthorizationAsync — Happy path ==========

    [Test]
    public async Task RevokeAuthorization_ActiveAuthorization_ReturnsSuccess()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedFullEnvironment(dbFactory);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        // Authorize first.
        ServiceResult<MachineAuthorizedKey> authResult = await service.AuthorizeKeyAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);
        await Assert.That(authResult.IsSuccess).IsEqualTo(true);

        // Revoke the authorization.
        ServiceResult<bool> revokeResult = await service.RevokeAuthorizationAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);

        await Assert.That(revokeResult.IsSuccess).IsEqualTo(true);
        await Assert.That(revokeResult.Data).IsEqualTo(true);

        // Verify the authorization record is actually revoked in the database.
        MachineAuthorizedKey? record = await dbFactory.Context.MachineAuthorizedKeys
            .FirstOrDefaultAsync(a => a.Id == authResult.Data!.Id);
        await Assert.That(record).IsNotNull();
        await Assert.That(record!.RevokedAt).IsNotNull();
    }

    // ========== RevokeAuthorizationAsync — Audit log verification ==========

    [Test]
    public async Task RevokeAuthorization_Success_CreatesAuditLogEntry()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedFullEnvironment(dbFactory);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        ServiceResult<MachineAuthorizedKey> authResult = await service.AuthorizeKeyAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);
        await Assert.That(authResult.IsSuccess).IsEqualTo(true);

        await service.RevokeAuthorizationAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);

        AuditLogEntry? auditEntry = await dbFactory.Context.AuditLog
            .FirstOrDefaultAsync(a => (a.Action == AuditAction.MachineKeyRevoked) &&
                                      (a.MachineId == machineId) &&
                                      (a.TenantId == tenantId));

        await Assert.That(auditEntry).IsNotNull();
        await Assert.That(auditEntry!.UserId).IsEqualTo(userId);
        await Assert.That(auditEntry.ResourceType).IsEqualTo(AuditResourceType.MachineAuthorizedKey);
    }

    // ========== RevokeAuthorizationAsync — Non-existent authorization ==========

    [Test]
    public async Task RevokeAuthorization_NoActiveAuthorization_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedFullEnvironment(dbFactory);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        // No authorization exists, so revocation should fail.
        ServiceResult<bool> result = await service.RevokeAuthorizationAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    // ========== RevokeAuthorizationAsync — Wrong tenant ==========

    [Test]
    public async Task RevokeAuthorization_WrongTenant_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedFullEnvironment(dbFactory);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        // Authorize first.
        ServiceResult<MachineAuthorizedKey> authResult = await service.AuthorizeKeyAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);
        await Assert.That(authResult.IsSuccess).IsEqualTo(true);

        // Revoke with wrong tenant should not find the record.
        int wrongTenantId = tenantId + 100;
        ServiceResult<bool> revokeResult = await service.RevokeAuthorizationAsync(
            machineId, signingKeyId, userId, wrongTenantId, CancellationToken.None);

        await Assert.That(revokeResult.IsNotFound).IsEqualTo(true);
    }

    // ========== ListAuthorizedKeysAsync — Empty machine ==========

    [Test]
    public async Task ListAuthorizedKeys_NoAuthorizations_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int _, long machineId, int _) = await SeedFullEnvironment(dbFactory);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        ServiceResult<List<MachineAuthorizedKeyDto>> result = await service.ListAuthorizedKeysAsync(
            machineId, tenantId, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data).IsNotNull();
        await Assert.That(result.Data!.Count).IsEqualTo(0);
    }

    // ========== ListAuthorizedKeysAsync — Active and revoked keys ==========

    [Test]
    public async Task ListAuthorizedKeys_OneActiveOneRevoked_ReturnsBothWithCorrectDtoFields()
    {
        using TestDatabaseFactory dbFactory = new();
        UserAccount user = TestDataBuilder.BuildUser(username: "testowner@example.com");
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: user.Id);
        tenant.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // Create two signing keys — one will be authorized and active, the other authorized then revoked.
        UserSigningKey key1 = TestDataBuilder.BuildSigningKey(userId: user.Id, tenantId: tenant.Id, label: "Active Key");
        key1.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(key1);

        UserSigningKey key2 = TestDataBuilder.BuildSigningKey(userId: user.Id, tenantId: tenant.Id, label: "Revoked Key");
        key2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(key2);

        MachineAuthorizedKeyService service = CreateService(dbFactory);

        // Authorize both keys.
        ServiceResult<MachineAuthorizedKey> auth1 = await service.AuthorizeKeyAsync(
            machine.Id, key1.Id, user.Id, tenant.Id, CancellationToken.None);
        await Assert.That(auth1.IsSuccess).IsEqualTo(true);

        ServiceResult<MachineAuthorizedKey> auth2 = await service.AuthorizeKeyAsync(
            machine.Id, key2.Id, user.Id, tenant.Id, CancellationToken.None);
        await Assert.That(auth2.IsSuccess).IsEqualTo(true);

        // Revoke the second one.
        await service.RevokeAuthorizationAsync(machine.Id, key2.Id, user.Id, tenant.Id, CancellationToken.None);

        // List should return both.
        ServiceResult<List<MachineAuthorizedKeyDto>> listResult = await service.ListAuthorizedKeysAsync(
            machine.Id, tenant.Id, CancellationToken.None);

        await Assert.That(listResult.IsSuccess).IsEqualTo(true);
        await Assert.That(listResult.Data!.Count).IsEqualTo(2);

        // Verify DTO fields on the active authorization.
        MachineAuthorizedKeyDto? activeDto = listResult.Data!.Find(d => d.SigningKeyId == key1.Id);
        await Assert.That(activeDto).IsNotNull();
        await Assert.That(activeDto!.Label).IsEqualTo("Active Key");
        await Assert.That(activeDto.Fingerprint).IsNotNull();
        await Assert.That(string.IsNullOrEmpty(activeDto.Fingerprint)).IsEqualTo(false);
        await Assert.That(activeDto.OwnerUsername).IsEqualTo("testowner@example.com");
        await Assert.That(activeDto.AuthorizedByUsername).IsEqualTo("testowner@example.com");
        await Assert.That(activeDto.IsActive).IsEqualTo(true);
        await Assert.That(activeDto.RevokedAt).IsNull();

        // Verify DTO fields on the revoked authorization.
        MachineAuthorizedKeyDto? revokedDto = listResult.Data!.Find(d => d.SigningKeyId == key2.Id);
        await Assert.That(revokedDto).IsNotNull();
        await Assert.That(revokedDto!.Label).IsEqualTo("Revoked Key");
        await Assert.That(revokedDto.IsActive).IsEqualTo(false);
        await Assert.That(revokedDto.RevokedAt).IsNotNull();
    }

    // ========== ListAuthorizedKeysAsync — Machine does not exist ==========

    [Test]
    public async Task ListAuthorizedKeys_NonExistentMachine_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int _, long _, int _) = await SeedFullEnvironment(dbFactory);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        ServiceResult<List<MachineAuthorizedKeyDto>> result = await service.ListAuthorizedKeysAsync(
            99999, tenantId, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    // ========== ListAuthorizedKeysAsync — Cross-machine isolation ==========

    [Test]
    public async Task ListAuthorizedKeys_OtherMachineAuthorizations_NotReturned()
    {
        using TestDatabaseFactory dbFactory = new();
        UserAccount user = TestDataBuilder.BuildUser();
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: user.Id);
        tenant.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        Machine machine1 = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine1);

        Machine machine2 = TestDataBuilder.BuildMachine(tenantId: tenant.Id);
        machine2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine2);

        UserSigningKey key = TestDataBuilder.BuildSigningKey(userId: user.Id, tenantId: tenant.Id);
        key.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(key);

        MachineAuthorizedKeyService service = CreateService(dbFactory);

        // Authorize the key for machine2 only.
        ServiceResult<MachineAuthorizedKey> authResult = await service.AuthorizeKeyAsync(
            machine2.Id, key.Id, user.Id, tenant.Id, CancellationToken.None);
        await Assert.That(authResult.IsSuccess).IsEqualTo(true);

        // Listing for machine1 should return no authorizations.
        ServiceResult<List<MachineAuthorizedKeyDto>> listResult = await service.ListAuthorizedKeysAsync(
            machine1.Id, tenant.Id, CancellationToken.None);

        await Assert.That(listResult.IsSuccess).IsEqualTo(true);
        await Assert.That(listResult.Data!.Count).IsEqualTo(0);
    }

    // ========== AuthorizeKeyAsync — Generated ID is positive ==========

    [Test]
    public async Task AuthorizeKey_Success_GeneratesPositiveId()
    {
        using TestDatabaseFactory dbFactory = new();
        (int tenantId, int userId, long machineId, int signingKeyId) = await SeedFullEnvironment(dbFactory);
        MachineAuthorizedKeyService service = CreateService(dbFactory);

        ServiceResult<MachineAuthorizedKey> result = await service.AuthorizeKeyAsync(
            machineId, signingKeyId, userId, tenantId, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Id > 0).IsEqualTo(true);
    }
}
