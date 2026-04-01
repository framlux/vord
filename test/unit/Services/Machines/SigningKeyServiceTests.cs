// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="SigningKeyService"/>.
/// Tests verify the intent of each business rule, not just code coverage.
/// </summary>
public sealed class SigningKeyServiceTests
{
    private readonly IDatabaseCache _cache;
    private readonly ILogger<SigningKeyService> _logger = Substitute.For<ILogger<SigningKeyService>>();

    public SigningKeyServiceTests()
    {
        _cache = Substitute.For<IDatabaseCache>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        _cache.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(mockTransaction);
    }

    private SigningKeyService CreateService() => new(_cache, _logger);

    /// <summary>
    /// Generates a valid 32-byte Ed25519 public key as base64.
    /// </summary>
    private static string GenerateValidPublicKey()
    {
        byte[] key = new byte[32];
        Random.Shared.NextBytes(key);

        return Convert.ToBase64String(key);
    }

    // ========== RegisterKeyAsync — Key format validation ==========

    [Test]
    public async Task RegisterKey_InvalidBase64_RejectsWithBadRequest()
    {
        SigningKeyService service = CreateService();

        ServiceResult<UserSigningKey> result = await service.RegisterKeyAsync(
            1, 1, "My Key", "not-valid-base64!!!", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task RegisterKey_WrongKeyLength_RejectsWithBadRequest()
    {
        // 16 bytes instead of required 32.
        string shortKey = Convert.ToBase64String(new byte[16]);
        SigningKeyService service = CreateService();

        ServiceResult<UserSigningKey> result = await service.RegisterKeyAsync(
            1, 1, "My Key", shortKey, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    // ========== RegisterKeyAsync — Max active keys enforcement ==========

    [Test]
    public async Task RegisterKey_AtMaxActiveKeys_Returns409()
    {
        _cache.GetActiveSigningKeyCountAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(ISigningKeyService.MaxActiveKeysPerUser);
        SigningKeyService service = CreateService();

        ServiceResult<UserSigningKey> result = await service.RegisterKeyAsync(
            1, 1, "One Too Many", GenerateValidPublicKey(), CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
    }

    [Test]
    public async Task RegisterKey_BelowMaxKeys_Succeeds()
    {
        _cache.GetActiveSigningKeyCountAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(ISigningKeyService.MaxActiveKeysPerUser - 1);
        _cache.CreateSigningKeyAsync(Arg.Any<UserSigningKey>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                UserSigningKey k = callInfo.Arg<UserSigningKey>();
                k.Id = 42;

                return k;
            });
        _cache.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        SigningKeyService service = CreateService();

        ServiceResult<UserSigningKey> result = await service.RegisterKeyAsync(
            1, 1, "Valid Key", GenerateValidPublicKey(), CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Id).IsEqualTo(42);
    }

    // ========== RegisterKeyAsync — Fingerprint computation ==========

    [Test]
    public async Task RegisterKey_ComputesSha256Fingerprint()
    {
        _cache.GetActiveSigningKeyCountAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);
        UserSigningKey? captured = null;
        _cache.CreateSigningKeyAsync(Arg.Any<UserSigningKey>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<UserSigningKey>();
                captured.Id = 1;

                return captured;
            });
        _cache.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        byte[] keyBytes = new byte[32];
        keyBytes[0] = 0xAB;
        string publicKeyBase64 = Convert.ToBase64String(keyBytes);
        string expectedFingerprint = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(keyBytes));

        SigningKeyService service = CreateService();
        await service.RegisterKeyAsync(1, 1, "Fingerprint Test", publicKeyBase64, CancellationToken.None);

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.PublicKeyFingerprint).IsEqualTo(expectedFingerprint);
    }

    // ========== RegisterKeyAsync — Audit logging ==========

    [Test]
    public async Task RegisterKey_Success_CreatesAuditEntry()
    {
        _cache.GetActiveSigningKeyCountAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);
        _cache.CreateSigningKeyAsync(Arg.Any<UserSigningKey>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                UserSigningKey k = callInfo.Arg<UserSigningKey>();
                k.Id = 10;

                return k;
            });
        _cache.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        SigningKeyService service = CreateService();
        await service.RegisterKeyAsync(5, 3, "Audit Test", GenerateValidPublicKey(), CancellationToken.None);

        await _cache.Received(1).InsertAuditLogAsync(
            Arg.Is<AuditLogEntry>(a =>
                a.Action == AuditAction.SigningKeyRegistered &&
                a.ResourceType == AuditResourceType.SigningKey &&
                a.UserId == 5 &&
                a.TenantId == 3),
            Arg.Any<CancellationToken>());
    }

    // ========== RevokeKeyAsync — Authorization ==========

    [Test]
    public async Task RevokeKey_NonAdminRevokingOthersKey_ReturnsForbidden()
    {
        UserSigningKey otherUsersKey = new()
        {
            Id = 1,
            UserId = 99,
            TenantId = 1,
            Label = "Other User Key",
            PublicKey = GenerateValidPublicKey(),
            PublicKeyFingerprint = "abc",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _cache.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(otherUsersKey);

        SigningKeyService service = CreateService();
        ServiceResult<bool> result = await service.RevokeKeyAsync(
            keyId: 1, userId: 42, tenantId: 1, isAdminOrTenantAdmin: false, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(403);
    }

    [Test]
    public async Task RevokeKey_AdminRevokingOthersKey_Succeeds()
    {
        UserSigningKey otherUsersKey = new()
        {
            Id = 1,
            UserId = 99,
            TenantId = 1,
            Label = "Other User Key",
            PublicKey = GenerateValidPublicKey(),
            PublicKeyFingerprint = "abc",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _cache.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(otherUsersKey);
        _cache.RevokeSigningKeyAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _cache.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        SigningKeyService service = CreateService();
        ServiceResult<bool> result = await service.RevokeKeyAsync(
            keyId: 1, userId: 42, tenantId: 1, isAdminOrTenantAdmin: true, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
    }

    [Test]
    public async Task RevokeKey_OwnerRevokingOwnKey_Succeeds()
    {
        UserSigningKey ownKey = new()
        {
            Id = 1,
            UserId = 42,
            TenantId = 1,
            Label = "My Key",
            PublicKey = GenerateValidPublicKey(),
            PublicKeyFingerprint = "abc",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _cache.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(ownKey);
        _cache.RevokeSigningKeyAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _cache.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        SigningKeyService service = CreateService();
        ServiceResult<bool> result = await service.RevokeKeyAsync(
            keyId: 1, userId: 42, tenantId: 1, isAdminOrTenantAdmin: false, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
    }

    // ========== RevokeKeyAsync — Cross-tenant isolation ==========

    [Test]
    public async Task RevokeKey_WrongTenant_ReturnsNotFound()
    {
        UserSigningKey key = new()
        {
            Id = 1,
            UserId = 42,
            TenantId = 999,
            Label = "Cross Tenant Key",
            PublicKey = GenerateValidPublicKey(),
            PublicKeyFingerprint = "abc",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _cache.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(key);

        SigningKeyService service = CreateService();
        ServiceResult<bool> result = await service.RevokeKeyAsync(
            keyId: 1, userId: 42, tenantId: 1, isAdminOrTenantAdmin: true, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    // ========== RevokeKeyAsync — Already revoked ==========

    [Test]
    public async Task RevokeKey_AlreadyRevoked_Returns409()
    {
        UserSigningKey revokedKey = new()
        {
            Id = 1,
            UserId = 42,
            TenantId = 1,
            Label = "Revoked Key",
            PublicKey = GenerateValidPublicKey(),
            PublicKeyFingerprint = "abc",
            CreatedAt = DateTimeOffset.UtcNow,
            RevokedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        _cache.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(revokedKey);

        SigningKeyService service = CreateService();
        ServiceResult<bool> result = await service.RevokeKeyAsync(
            keyId: 1, userId: 42, tenantId: 1, isAdminOrTenantAdmin: false, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
    }

    // ========== RevokeKeyAsync — Key not found ==========

    [Test]
    public async Task RevokeKey_NonExistentKey_ReturnsNotFound()
    {
        _cache.GetSigningKeyByIdAsync(999, Arg.Any<CancellationToken>())
            .Returns((UserSigningKey?)null);

        SigningKeyService service = CreateService();
        ServiceResult<bool> result = await service.RevokeKeyAsync(
            keyId: 999, userId: 1, tenantId: 1, isAdminOrTenantAdmin: false, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }
}
