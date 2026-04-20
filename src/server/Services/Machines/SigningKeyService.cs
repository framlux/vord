// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using System.Security.Cryptography;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Implementation of signing key management for remote command authorization.
/// </summary>
public sealed class SigningKeyService : ISigningKeyService
{
    private readonly IDatabaseCache _cache;
    private readonly ILogger<SigningKeyService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SigningKeyService"/> class.
    /// </summary>
    /// <param name="cache">The database caching layer</param>
    /// <param name="logger">The logger</param>
    public SigningKeyService(IDatabaseCache cache, ILogger<SigningKeyService> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<UserSigningKey>> RegisterKeyAsync(int userId, int tenantId, string label, string publicKeyBase64, CancellationToken cancellationToken = default)
    {
        // Validate public key is valid base64 and correct length (32 bytes for Ed25519).
        byte[] publicKeyBytes;
        try
        {
            publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
        }
        catch (FormatException)
        {
            return ServiceResult<UserSigningKey>.BadRequest("Public key must be valid base64-encoded data");
        }

        if (publicKeyBytes.Length != 32)
        {
            return ServiceResult<UserSigningKey>.BadRequest("Public key must be exactly 32 bytes for Ed25519");
        }

        // Enforce max active keys per user per tenant.
        int activeCount = await _cache.GetActiveSigningKeyCountAsync(userId, tenantId, cancellationToken);
        if (activeCount >= ISigningKeyService.MaxActiveKeysPerUser)
        {
            return ServiceResult<UserSigningKey>.Conflict("Maximum number of active signing keys per user has been reached");
        }

        // Compute SHA-256 fingerprint of the public key.
        string fingerprint = ComputeFingerprint(publicKeyBytes);

        UserSigningKey key = new()
        {
            UserId = userId,
            TenantId = tenantId,
            Label = label,
            PublicKey = publicKeyBase64,
            PublicKeyFingerprint = fingerprint,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        using IDatabaseTransaction transaction = await _cache.BeginTransactionAsync(cancellationToken);

        UserSigningKey created = await _cache.CreateSigningKeyAsync(key, cancellationToken);

        await _cache.InsertAuditLogAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            UserId = userId,
            Action = AuditAction.SigningKeyRegistered,
            ResourceType = AuditResourceType.SigningKey,
            ResourceId = created.Id.ToString(),
            Timestamp = DateTimeOffset.UtcNow,
        }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("Signing key {KeyId} registered for user {UserId} in tenant {TenantId}",
            created.Id, userId, tenantId);

        return ServiceResult<UserSigningKey>.Ok(created);
    }

    /// <inheritdoc/>
    public async Task<List<UserSigningKey>> ListKeysAsync(int userId, int tenantId, CancellationToken cancellationToken = default)
    {
        return await _cache.GetSigningKeysForUserAsync(userId, tenantId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<bool>> RevokeKeyAsync(int keyId, int userId, int tenantId, bool isAdminOrTenantAdmin, CancellationToken cancellationToken = default)
    {
        UserSigningKey? key = await _cache.GetSigningKeyByIdAsync(keyId, cancellationToken);
        if ((key is null) || (key.TenantId != tenantId))
        {
            return ServiceResult<bool>.NotFound();
        }

        if (key.RevokedAt is not null)
        {
            return ServiceResult<bool>.Error(409, false);
        }

        // Non-admins can only revoke their own keys.
        if (isAdminOrTenantAdmin == false && key.UserId != userId)
        {
            return ServiceResult<bool>.Error(403, false);
        }

        using IDatabaseTransaction transaction = await _cache.BeginTransactionAsync(cancellationToken);

        await _cache.RevokeSigningKeyAsync(keyId, userId, cancellationToken);

        await _cache.InsertAuditLogAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            UserId = userId,
            Action = AuditAction.SigningKeyRevoked,
            ResourceType = AuditResourceType.SigningKey,
            ResourceId = keyId.ToString(),
            Timestamp = DateTimeOffset.UtcNow,
        }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("Signing key {KeyId} revoked by user {UserId} in tenant {TenantId}",
            keyId, userId, tenantId);

        return ServiceResult<bool>.Ok(true);
    }

    private static string ComputeFingerprint(byte[] publicKeyBytes)
    {
        byte[] hash = SHA256.HashData(publicKeyBytes);

        return Convert.ToHexStringLower(hash);
    }
}
