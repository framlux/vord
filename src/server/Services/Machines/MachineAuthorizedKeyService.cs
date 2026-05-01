// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Implementation of per-machine signing key authorization management.
/// </summary>
public sealed class MachineAuthorizedKeyService : IMachineAuthorizedKeyService
{
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;
    private readonly IMachineRepository _machineRepository;
    private readonly ISigningKeyRepository _signingKeyRepository;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<MachineAuthorizedKeyService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MachineAuthorizedKeyService"/> class.
    /// </summary>
    /// <param name="transactionProvider">The database transaction provider</param>
    /// <param name="auditLog">The audit log repository</param>
    /// <param name="machineRepository">The machine repository</param>
    /// <param name="signingKeyRepository">The signing key repository</param>
    /// <param name="userRepository">The user repository</param>
    /// <param name="logger">The logger</param>
    public MachineAuthorizedKeyService(
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog,
        IMachineRepository machineRepository,
        ISigningKeyRepository signingKeyRepository,
        IUserRepository userRepository,
        ILogger<MachineAuthorizedKeyService> logger)
    {
        _transactionProvider = transactionProvider ?? throw new ArgumentNullException(nameof(transactionProvider));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _machineRepository = machineRepository ?? throw new ArgumentNullException(nameof(machineRepository));
        _signingKeyRepository = signingKeyRepository ?? throw new ArgumentNullException(nameof(signingKeyRepository));
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<MachineAuthorizedKey>> AuthorizeKeyAsync(long machineId, int signingKeyId, int userId, int tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId <= 0)
        {
            return ServiceResult<MachineAuthorizedKey>.NotFound();
        }

        // Verify the machine exists, belongs to the tenant, and is not deleted.
        Machine? machine = await _machineRepository.GetMachineAsync(machineId, tenantId, cancellationToken);
        if (machine is null)
        {
            return ServiceResult<MachineAuthorizedKey>.NotFound();
        }

        // Verify the signing key exists and belongs to the tenant.
        UserSigningKey? signingKey = await _signingKeyRepository.GetSigningKeyByIdAsync(signingKeyId, cancellationToken);
        if ((signingKey is null) || (signingKey.TenantId != tenantId))
        {
            return ServiceResult<MachineAuthorizedKey>.NotFound();
        }

        // A revoked signing key cannot be authorized for a machine.
        if (signingKey.RevokedAt is not null)
        {
            return ServiceResult<MachineAuthorizedKey>.BadRequest("Cannot authorize a revoked signing key");
        }

        // Check if the signing key is already actively authorized for this machine.
        bool alreadyAuthorized = await _signingKeyRepository.IsKeyAuthorizedForMachineAsync(signingKeyId, machineId, cancellationToken);
        if (alreadyAuthorized)
        {
            return ServiceResult<MachineAuthorizedKey>.Conflict("Signing key is already authorized for this machine");
        }

        // Check if a previously-revoked authorization exists for this machine-key pair.
        // If so, re-activate it instead of inserting a new row (unique constraint on MachineId+SigningKeyId).
        MachineAuthorizedKey? existingRevoked = await _signingKeyRepository.GetRevokedAuthorizationAsync(machineId, signingKeyId, tenantId, cancellationToken);

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(cancellationToken);

        MachineAuthorizedKey result;
        if (existingRevoked is not null)
        {
            // Re-activate the existing revoked authorization.
            await _signingKeyRepository.ReactivateAuthorizationAsync(existingRevoked.Id, userId, cancellationToken);

            existingRevoked.RevokedAt = null;
            existingRevoked.RevokedByUserId = null;
            existingRevoked.AuthorizedAt = DateTimeOffset.UtcNow;
            existingRevoked.AuthorizedByUserId = userId;
            result = existingRevoked;
        }
        else
        {
            // First-time authorization — insert a new row.
            MachineAuthorizedKey authorization = new()
            {
                MachineId = machineId,
                SigningKeyId = signingKeyId,
                TenantId = tenantId,
                AuthorizedAt = DateTimeOffset.UtcNow,
                AuthorizedByUserId = userId,
            };
            result = await _signingKeyRepository.CreateMachineAuthorizationAsync(authorization, cancellationToken);
        }

        await _auditLog.InsertAuditLogAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            UserId = userId,
            MachineId = machineId,
            Action = AuditAction.MachineKeyAuthorized,
            ResourceType = AuditResourceType.MachineAuthorizedKey,
            ResourceId = result.Id.ToString(),
            Timestamp = DateTimeOffset.UtcNow,
        }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Signing key {SigningKeyId} authorized for machine {MachineId} by user {UserId} in tenant {TenantId}",
            signingKeyId, machineId, userId, tenantId);

        return ServiceResult<MachineAuthorizedKey>.Ok(result);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<bool>> RevokeAuthorizationAsync(long machineId, int signingKeyId, int userId, int tenantId, CancellationToken cancellationToken = default)
    {
        // Find the active authorization record for this machine and signing key.
        MachineAuthorizedKey? authorization = await _signingKeyRepository.GetActiveAuthorizationAsync(machineId, signingKeyId, tenantId, cancellationToken);

        if (authorization is null)
        {
            return ServiceResult<bool>.NotFound();
        }

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(cancellationToken);

        await _signingKeyRepository.RevokeMachineAuthorizationAsync(machineId, signingKeyId, userId, cancellationToken);

        await _auditLog.InsertAuditLogAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            UserId = userId,
            MachineId = machineId,
            Action = AuditAction.MachineKeyRevoked,
            ResourceType = AuditResourceType.MachineAuthorizedKey,
            ResourceId = authorization.Id.ToString(),
            Timestamp = DateTimeOffset.UtcNow,
        }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Signing key {SigningKeyId} authorization revoked for machine {MachineId} by user {UserId} in tenant {TenantId}",
            signingKeyId, machineId, userId, tenantId);

        return ServiceResult<bool>.Ok(true);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<List<MachineAuthorizedKeyDto>>> ListAuthorizedKeysAsync(long machineId, int tenantId, CancellationToken cancellationToken = default)
    {
        // Verify the machine exists in the tenant.
        Machine? machine = await _machineRepository.GetMachineAsync(machineId, tenantId, cancellationToken);
        if (machine is null)
        {
            return ServiceResult<List<MachineAuthorizedKeyDto>>.NotFound();
        }

        // Get authorization records for this machine.
        List<MachineAuthorizedKey> authorizations = await _signingKeyRepository.GetAuthorizedKeysForMachineAsync(machineId, cancellationToken);

        // Filter to this tenant and enrich with signing key and user display data.
        List<MachineAuthorizedKeyDto> dtos = [];
        foreach (MachineAuthorizedKey auth in authorizations.Where(a => a.TenantId == tenantId).OrderByDescending(a => a.AuthorizedAt))
        {
            UserSigningKey? signingKey = await _signingKeyRepository.GetSigningKeyByIdAsync(auth.SigningKeyId, cancellationToken);
            UserAccount? owner = signingKey is not null ? await _userRepository.GetUserByIdAsync(signingKey.UserId, cancellationToken) : null;
            UserAccount? authorizer = await _userRepository.GetUserByIdAsync(auth.AuthorizedByUserId, cancellationToken);

            dtos.Add(new MachineAuthorizedKeyDto
            {
                Id = auth.Id,
                SigningKeyId = auth.SigningKeyId,
                Label = signingKey?.Label ?? string.Empty,
                Fingerprint = signingKey?.PublicKeyFingerprint ?? string.Empty,
                OwnerUsername = owner?.Username ?? string.Empty,
                AuthorizedAt = auth.AuthorizedAt,
                AuthorizedByUsername = authorizer?.Username ?? string.Empty,
                RevokedAt = auth.RevokedAt,
                IsActive = auth.RevokedAt is null,
            });
        }

        return ServiceResult<List<MachineAuthorizedKeyDto>>.Ok(dtos);
    }
}
