// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;

namespace Framlux.FleetManagement.Server.Services.Commands;

/// <summary>
/// Implementation of remote command management.
/// </summary>
public sealed class RemoteCommandService : IRemoteCommandService
{
    private const ulong CapabilityRemoteCommands = 1UL;

    private static readonly HashSet<string> AllowedCommandTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "reboot",
        "kill_process",
        "kill_session",
        "check_updates",
        "install_updates",
    };

    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;
    private readonly IMachineRepository _machineRepository;
    private readonly ISigningKeyRepository _signingKeyRepository;
    private readonly IRemoteCommandRepository _remoteCommandRepository;
    private readonly IMachinePingService _pingService;
    private readonly ILogger<RemoteCommandService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteCommandService"/> class.
    /// </summary>
    /// <param name="transactionProvider">The database transaction provider</param>
    /// <param name="auditLog">The audit log repository</param>
    /// <param name="machineRepository">The machine repository</param>
    /// <param name="signingKeyRepository">The signing key repository</param>
    /// <param name="remoteCommandRepository">The remote command repository</param>
    /// <param name="pingService">The machine ping and capabilities service</param>
    /// <param name="logger">The logger</param>
    public RemoteCommandService(
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog,
        IMachineRepository machineRepository,
        ISigningKeyRepository signingKeyRepository,
        IRemoteCommandRepository remoteCommandRepository,
        IMachinePingService pingService,
        ILogger<RemoteCommandService> logger)
    {
        _transactionProvider = transactionProvider ?? throw new ArgumentNullException(nameof(transactionProvider));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _machineRepository = machineRepository ?? throw new ArgumentNullException(nameof(machineRepository));
        _signingKeyRepository = signingKeyRepository ?? throw new ArgumentNullException(nameof(signingKeyRepository));
        _remoteCommandRepository = remoteCommandRepository ?? throw new ArgumentNullException(nameof(remoteCommandRepository));
        _pingService = pingService ?? throw new ArgumentNullException(nameof(pingService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<RemoteCommand>> SubmitCommandAsync(RemoteCommand command, CancellationToken cancellationToken = default)
    {
        // Validate command type against allowlist.
        if (AllowedCommandTypes.Contains(command.CommandType) == false)
        {
            return ServiceResult<RemoteCommand>.BadRequest("Invalid command type");
        }

        // Verify the signing key exists, belongs to the correct tenant, and is not revoked.
        UserSigningKey? signingKey = await _signingKeyRepository.GetSigningKeyByIdAsync(command.SigningKeyId, cancellationToken);
        if ((signingKey is null) || (signingKey.TenantId != command.TenantId))
        {
            return ServiceResult<RemoteCommand>.BadRequest("Signing key not found or does not belong to tenant");
        }

        if (signingKey.RevokedAt is not null)
        {
            return ServiceResult<RemoteCommand>.BadRequest("Signing key has been revoked");
        }

        if (signingKey.UserId != command.UserId)
        {
            return ServiceResult<RemoteCommand>.Forbidden("Signing key does not belong to the authenticated user");
        }

        // Verify Ed25519 signature.
        byte[] publicKeyBytes;
        byte[] signatureBytes;
        try
        {
            publicKeyBytes = Convert.FromBase64String(signingKey.PublicKey);
            signatureBytes = Convert.FromBase64String(command.Signature);
        }
        catch (FormatException)
        {
            return ServiceResult<RemoteCommand>.BadRequest("Invalid base64 encoding in key or signature");
        }

        byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(command.CanonicalPayload);

        NSec.Cryptography.SignatureAlgorithm algorithm = NSec.Cryptography.SignatureAlgorithm.Ed25519;
        NSec.Cryptography.PublicKey publicKey;
        try
        {
            publicKey = NSec.Cryptography.PublicKey.Import(algorithm, publicKeyBytes, NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        }
        catch (Exception)
        {
            return ServiceResult<RemoteCommand>.BadRequest("Invalid public key format");
        }

        if (algorithm.Verify(publicKey, payloadBytes, signatureBytes) == false)
        {
            return ServiceResult<RemoteCommand>.BadRequest("Signature verification failed");
        }

        // Validate target machine belongs to user's tenant.
        Machine? machine = await _machineRepository.GetMachineAsync(command.MachineId, command.TenantId, cancellationToken);
        if (machine is null)
        {
            return ServiceResult<RemoteCommand>.BadRequest("Target machine not found or does not belong to tenant");
        }

        // Verify the signing key is authorized for this specific machine.
        bool isAuthorized = await _signingKeyRepository.IsKeyAuthorizedForMachineAsync(command.SigningKeyId, command.MachineId, cancellationToken);
        if (isAuthorized == false)
        {
            return ServiceResult<RemoteCommand>.Forbidden("Signing key is not authorized for this machine");
        }

        // Reject commands when the agent has not reported the remote commands capability.
        ulong capabilities = await _pingService.GetAgentCapabilitiesAsync(command.MachineId);
        if ((capabilities & CapabilityRemoteCommands) == 0)
        {
            return ServiceResult<RemoteCommand>.BadRequest("Remote commands are not enabled on this machine");
        }

        // Check for duplicate command ID.
        RemoteCommand? existing = await _remoteCommandRepository.GetRemoteCommandByCommandIdAsync(command.CommandId, cancellationToken);
        if (existing is not null)
        {
            return ServiceResult<RemoteCommand>.Conflict("Duplicate command ID");
        }

        // Check nonce uniqueness.
        bool nonceUsed = await _remoteCommandRepository.IsNonceUsedAsync(command.Nonce, cancellationToken);
        if (nonceUsed)
        {
            return ServiceResult<RemoteCommand>.Conflict("Nonce already used");
        }

        command.Status = RemoteCommandStatus.Pending;
        command.CreatedAt = DateTimeOffset.UtcNow;

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(cancellationToken);

        RemoteCommand created = await _remoteCommandRepository.CreateRemoteCommandAsync(command, cancellationToken);

        await _auditLog.InsertAuditLogAsync(new AuditLogEntry
        {
            TenantId = command.TenantId,
            UserId = command.UserId,
            MachineId = command.MachineId,
            Action = AuditAction.RemoteCommandSent,
            ResourceType = AuditResourceType.RemoteCommand,
            ResourceId = created.Id.ToString(),
            Timestamp = DateTimeOffset.UtcNow,
        }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("Remote command {CommandId} submitted: type={CommandType}, machine={MachineId}, user={UserId}",
            command.CommandId, command.CommandType, command.MachineId, command.UserId);

        return ServiceResult<RemoteCommand>.Ok(created);
    }

    /// <inheritdoc/>
    public async Task<List<RemoteCommand>> GetCommandHistoryAsync(long machineId, int tenantId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        return await _remoteCommandRepository.GetCommandsForMachineAsync(machineId, tenantId, page, pageSize, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<RemoteCommand>> GetCommandDetailAsync(long id, int tenantId, CancellationToken cancellationToken = default)
    {
        RemoteCommand? command = await _remoteCommandRepository.GetRemoteCommandByIdAsync(id, tenantId, cancellationToken);
        if (command is null)
        {
            return ServiceResult<RemoteCommand>.NotFound();
        }

        return ServiceResult<RemoteCommand>.Ok(command);
    }
}
