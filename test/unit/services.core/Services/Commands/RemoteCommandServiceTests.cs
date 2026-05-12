// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Commands;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="RemoteCommandService"/>.
/// Tests verify the intent of each business rule, not just code coverage.
/// </summary>
public sealed class RemoteCommandServiceTests
{
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;
    private readonly IMachineRepository _machineRepository;
    private readonly ISigningKeyRepository _signingKeyRepository;
    private readonly IRemoteCommandRepository _remoteCommandRepository;
    private readonly InMemoryMachinePingService _pingService = new();
    private readonly ILogger<RemoteCommandService> _logger = Substitute.For<ILogger<RemoteCommandService>>();

    public RemoteCommandServiceTests()
    {
        _transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        _auditLog = Substitute.For<IAuditLogRepository>();
        _machineRepository = Substitute.For<IMachineRepository>();
        _signingKeyRepository = Substitute.For<ISigningKeyRepository>();
        _remoteCommandRepository = Substitute.For<IRemoteCommandRepository>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        _transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(mockTransaction);
    }

    private RemoteCommandService CreateService() => new(_transactionProvider, _auditLog, _machineRepository, _signingKeyRepository, _remoteCommandRepository, _pingService, _logger);

    // ========== Constructor null guard validation ==========

    [Test]
    public async Task Constructor_NullTransactionProvider_Throws()
    {
        // Intent: A null transaction provider must be caught at construction time so any
        // misconfigured DI container fails loudly rather than causing a deferred NullReferenceException.
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            RemoteCommandService _ = new(null!, _auditLog, _machineRepository, _signingKeyRepository, _remoteCommandRepository, _pingService, _logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("transactionProvider");
    }

    [Test]
    public async Task Constructor_NullAuditLog_Throws()
    {
        // Intent: A null audit log must be caught at construction to ensure the service
        // never silently proceeds without its required audit dependency.
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            RemoteCommandService _ = new(_transactionProvider, null!, _machineRepository, _signingKeyRepository, _remoteCommandRepository, _pingService, _logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("auditLog");
    }

    [Test]
    public async Task Constructor_NullMachineRepository_Throws()
    {
        // Intent: A null machine repository must be caught at construction.
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            RemoteCommandService _ = new(_transactionProvider, _auditLog, null!, _signingKeyRepository, _remoteCommandRepository, _pingService, _logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("machineRepository");
    }

    [Test]
    public async Task Constructor_NullSigningKeyRepository_Throws()
    {
        // Intent: A null signing key repository must be caught at construction.
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            RemoteCommandService _ = new(_transactionProvider, _auditLog, _machineRepository, null!, _remoteCommandRepository, _pingService, _logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("signingKeyRepository");
    }

    [Test]
    public async Task Constructor_NullRemoteCommandRepository_Throws()
    {
        // Intent: A null remote command repository must be caught at construction.
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            RemoteCommandService _ = new(_transactionProvider, _auditLog, _machineRepository, _signingKeyRepository, null!, _pingService, _logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("remoteCommandRepository");
    }

    [Test]
    public async Task Constructor_NullPingService_Throws()
    {
        // Intent: A null machine ping service must be caught at construction.
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            RemoteCommandService _ = new(_transactionProvider, _auditLog, _machineRepository, _signingKeyRepository, _remoteCommandRepository, null!, _logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("pingService");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        // Intent: A null logger must be caught at construction.
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            RemoteCommandService _ = new(_transactionProvider, _auditLog, _machineRepository, _signingKeyRepository, _remoteCommandRepository, _pingService, null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    private static (UserSigningKey key, NSec.Cryptography.Key privateKey) BuildSignedKey(int id = 1, int userId = 1, int tenantId = 1)
    {
        NSec.Cryptography.SignatureAlgorithm algorithm = NSec.Cryptography.SignatureAlgorithm.Ed25519;
        NSec.Cryptography.Key privateKey = NSec.Cryptography.Key.Create(algorithm);
        byte[] publicKeyBytes = privateKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);

        UserSigningKey signingKey = new()
        {
            Id = id,
            UserId = userId,
            TenantId = tenantId,
            Label = "Test Key",
            PublicKey = Convert.ToBase64String(publicKeyBytes),
            PublicKeyFingerprint = "abc",
            CreatedAt = DateTimeOffset.UtcNow,
            RevokedAt = null,
        };

        return (signingKey, privateKey);
    }

    private static UserSigningKey BuildActiveKey(int id = 1, int userId = 1, int tenantId = 1)
    {
        return new UserSigningKey
        {
            Id = id,
            UserId = userId,
            TenantId = tenantId,
            Label = "Test Key",
            PublicKey = Convert.ToBase64String(new byte[32]),
            PublicKeyFingerprint = "abc",
            CreatedAt = DateTimeOffset.UtcNow,
            RevokedAt = null,
        };
    }

    private static RemoteCommand BuildSignedCommand(
        NSec.Cryptography.Key privateKey,
        int signingKeyId = 1,
        int userId = 1,
        int tenantId = 1,
        long machineId = 1,
        string commandType = "reboot",
        string? commandId = null)
    {
        NSec.Cryptography.SignatureAlgorithm algorithm = NSec.Cryptography.SignatureAlgorithm.Ed25519;
        string payload = "{}";
        byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
        byte[] signature = algorithm.Sign(privateKey, payloadBytes);

        return new RemoteCommand
        {
            CommandId = commandId ?? Guid.NewGuid().ToString("D"),
            TenantId = tenantId,
            MachineId = machineId,
            UserId = userId,
            SigningKeyId = signingKeyId,
            CommandType = commandType,
            Params = null,
            Nonce = Guid.NewGuid().ToString("N"),
            Signature = Convert.ToBase64String(signature),
            CanonicalPayload = payload,
            Timestamp = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            Status = RemoteCommandStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private void SetupValidCommandMocks(UserSigningKey key, long machineId = 1)
    {
        _signingKeyRepository.GetSigningKeyByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);
        _machineRepository.GetMachineAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Machine
            {
                Id = machineId,
                TenantId = key.TenantId,
                Name = "test",
                ApiKeyHash = "test",
                SerialNumber = "test",
                SystemId = "test",
                MachineType = MachineTypes.BareMetalServer,
                OperatingSystem = OperatingSystems.Ubuntu,
                RegistrationTokenId = 1,
                RegisteredOn = DateTimeOffset.UtcNow,
                IsDeleted = false,
            });
        _signingKeyRepository.IsKeyAuthorizedForMachineAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _remoteCommandRepository.GetRemoteCommandByCommandIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((RemoteCommand?)null);
        _remoteCommandRepository.IsNonceUsedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _remoteCommandRepository.CreateRemoteCommandAsync(Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                RemoteCommand c = callInfo.Arg<RemoteCommand>();
                c.Id = 100;

                return c;
            });
        _auditLog.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Enable remote commands capability (bit 0) for the machine.
        _pingService.SetAgentCapabilitiesAsync(machineId, 1UL).GetAwaiter().GetResult();
    }

    // ========== SubmitCommandAsync — Revoked key rejection ==========

    [Test]
    public async Task SubmitCommand_RevokedSigningKey_RejectsCommand()
    {
        UserSigningKey revokedKey = BuildActiveKey();
        revokedKey.RevokedAt = DateTimeOffset.UtcNow.AddHours(-1);
        _signingKeyRepository.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(revokedKey);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(signingKeyId: 1);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    // ========== SubmitCommandAsync — Key ownership validation ==========

    [Test]
    public async Task SubmitCommand_KeyBelongsToOtherUser_ReturnsForbidden()
    {
        UserSigningKey otherUsersKey = BuildActiveKey(userId: 99);
        _signingKeyRepository.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(otherUsersKey);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(userId: 42, signingKeyId: 1);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(403);
    }

    // ========== SubmitCommandAsync — Cross-tenant isolation ==========

    [Test]
    public async Task SubmitCommand_KeyFromDifferentTenant_Rejects()
    {
        UserSigningKey wrongTenantKey = BuildActiveKey(tenantId: 999);
        _signingKeyRepository.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(wrongTenantKey);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(tenantId: 1, signingKeyId: 1);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    // ========== SubmitCommandAsync — Invalid command type ==========

    [Test]
    public async Task SubmitCommand_InvalidCommandType_Returns400()
    {
        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(commandType: "dangerous_unknown_type");
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    // ========== SubmitCommandAsync — Invalid signature ==========

    [Test]
    public async Task SubmitCommand_InvalidSignature_Returns400()
    {
        (UserSigningKey key, NSec.Cryptography.Key _) = BuildSignedKey();
        _signingKeyRepository.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(key);

        // Use a command with an invalid signature (all zeroes).
        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(signingKeyId: 1);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    // ========== SubmitCommandAsync — Valid signature succeeds ==========

    [Test]
    public async Task SubmitCommand_ValidSignature_Succeeds()
    {
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);

        RemoteCommand command = BuildSignedCommand(privateKey);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    // ========== SubmitCommandAsync — Machine in different tenant ==========

    [Test]
    public async Task SubmitCommand_MachineInDifferentTenant_Returns400()
    {
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        _signingKeyRepository.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(key);
        _machineRepository.GetMachineAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((Machine?)null);

        RemoteCommand command = BuildSignedCommand(privateKey);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    // ========== SubmitCommandAsync — Duplicate command prevention ==========

    [Test]
    public async Task SubmitCommand_DuplicateCommandId_Returns409()
    {
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);

        string duplicateId = Guid.NewGuid().ToString("D");
        _remoteCommandRepository.GetRemoteCommandByCommandIdAsync(duplicateId, Arg.Any<CancellationToken>())
            .Returns(TestDataBuilder.BuildRemoteCommand(commandId: duplicateId));

        RemoteCommand command = BuildSignedCommand(privateKey, commandId: duplicateId);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
    }

    // ========== SubmitCommandAsync — Duplicate nonce ==========

    [Test]
    public async Task SubmitCommand_DuplicateNonce_Returns409()
    {
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        _remoteCommandRepository.IsNonceUsedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        RemoteCommand command = BuildSignedCommand(privateKey);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
    }

    // ========== SubmitCommandAsync — Non-existent signing key ==========

    [Test]
    public async Task SubmitCommand_NonExistentSigningKey_Rejects()
    {
        _signingKeyRepository.GetSigningKeyByIdAsync(999, Arg.Any<CancellationToken>())
            .Returns((UserSigningKey?)null);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(signingKeyId: 999);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    // ========== SubmitCommandAsync — Valid command succeeds ==========

    [Test]
    public async Task SubmitCommand_ValidCommand_CreatesAndReturnsCommand()
    {
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);

        RemoteCommand command = BuildSignedCommand(privateKey);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Id).IsEqualTo(100L);
        await Assert.That(result.Data!.Status).IsEqualTo(RemoteCommandStatus.Pending);
    }

    // ========== SubmitCommandAsync — Audit logging ==========

    [Test]
    public async Task SubmitCommand_Success_CreatesAuditEntry()
    {
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey(userId: 5, tenantId: 3);
        SetupValidCommandMocks(key, machineId: 77);

        RemoteCommand command = BuildSignedCommand(privateKey, userId: 5, tenantId: 3, machineId: 77);
        RemoteCommandService service = CreateService();

        await service.SubmitCommandAsync(command, CancellationToken.None);

        await _auditLog.Received(1).InsertAuditLogAsync(
            Arg.Is<AuditLogEntry>(a =>
                a.Action == AuditAction.RemoteCommandSent &&
                a.ResourceType == AuditResourceType.RemoteCommand &&
                a.UserId == 5 &&
                a.TenantId == 3 &&
                a.MachineId == 77),
            Arg.Any<CancellationToken>());
    }

    // ========== GetCommandDetailAsync — Cross-tenant isolation ==========

    [Test]
    public async Task GetCommandDetail_WrongTenant_ReturnsNotFound()
    {
        _remoteCommandRepository.GetRemoteCommandByIdAsync(1, 999, Arg.Any<CancellationToken>())
            .Returns((RemoteCommand?)null);

        RemoteCommandService service = CreateService();
        ServiceResult<RemoteCommand> result = await service.GetCommandDetailAsync(1, 999, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetCommandDetail_ValidTenant_ReturnsCommand()
    {
        RemoteCommand cmd = TestDataBuilder.BuildRemoteCommand(tenantId: 1);
        cmd.Id = 42;
        _remoteCommandRepository.GetRemoteCommandByIdAsync(42, 1, Arg.Any<CancellationToken>()).Returns(cmd);

        RemoteCommandService service = CreateService();
        ServiceResult<RemoteCommand> result = await service.GetCommandDetailAsync(42, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Id).IsEqualTo(42L);
    }

    // ========== SubmitCommandAsync — Agent capabilities validation ==========

    [Test]
    public async Task SubmitCommand_CommandsCapabilityNotSet_Returns400()
    {
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        _signingKeyRepository.GetSigningKeyByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);
        _machineRepository.GetMachineAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Machine
            {
                Id = 1,
                TenantId = key.TenantId,
                Name = "test",
                ApiKeyHash = "test",
                SerialNumber = "test",
                SystemId = "test",
                MachineType = MachineTypes.BareMetalServer,
                OperatingSystem = OperatingSystems.Ubuntu,
                RegistrationTokenId = 1,
                RegisteredOn = DateTimeOffset.UtcNow,
                IsDeleted = false,
            });

        _signingKeyRepository.IsKeyAuthorizedForMachineAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // No capabilities set — machine has never reported or commands are disabled.
        RemoteCommand command = BuildSignedCommand(privateKey);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task SubmitCommand_CommandsCapabilitySet_AllowsCommand()
    {
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);

        RemoteCommand command = BuildSignedCommand(privateKey);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    // ========== SubmitCommandAsync — Signing key not authorized for machine ==========

    [Test]
    public async Task SubmitCommand_SigningKeyNotAuthorizedForMachine_ReturnsForbidden()
    {
        // Intent: Even if a signing key is valid and active, it must be explicitly authorized
        // for the target machine. Submitting without that authorization must be rejected.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        _signingKeyRepository.GetSigningKeyByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);
        _machineRepository.GetMachineAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Machine
            {
                Id = 1,
                TenantId = key.TenantId,
                Name = "test",
                ApiKeyHash = "test",
                SerialNumber = "test",
                SystemId = "test",
                MachineType = MachineTypes.BareMetalServer,
                OperatingSystem = OperatingSystems.Ubuntu,
                RegistrationTokenId = 1,
                RegisteredOn = DateTimeOffset.UtcNow,
                IsDeleted = false,
            });

        // Explicitly deny authorization for this key/machine combination.
        _signingKeyRepository.IsKeyAuthorizedForMachineAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(false);

        RemoteCommand command = BuildSignedCommand(privateKey);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(403);
    }

    // ========== SubmitCommandAsync — Invalid base64 in public key or signature ==========

    [Test]
    public async Task SubmitCommand_InvalidBase64InPublicKey_Returns400()
    {
        // Intent: When the stored public key is not valid base64 the server must reject
        // the command with a clear bad-request response rather than throwing an unhandled exception.
        UserSigningKey keyWithBadBase64 = new()
        {
            Id = 1,
            UserId = 1,
            TenantId = 1,
            Label = "Bad Key",
            PublicKey = "!!! not-valid-base64 !!!",
            PublicKeyFingerprint = "abc",
            CreatedAt = DateTimeOffset.UtcNow,
            RevokedAt = null,
        };

        _signingKeyRepository.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(keyWithBadBase64);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(signingKeyId: 1);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task SubmitCommand_InvalidBase64InSignature_Returns400()
    {
        // Intent: When the submitted signature field is not valid base64 the server must
        // reject the command with a bad-request rather than propagating an unhandled exception.
        (UserSigningKey key, NSec.Cryptography.Key _) = BuildSignedKey();
        _signingKeyRepository.GetSigningKeyByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(signingKeyId: key.Id);
        command.Signature = "!!! not-valid-base64 !!!";

        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    // ========== SubmitCommandAsync — Malformed public key bytes ==========

    [Test]
    public async Task SubmitCommand_MalformedPublicKeyBytes_Returns400()
    {
        // Intent: Base64 that decodes successfully but does not represent a valid Ed25519
        // public key must be caught and returned as a bad-request rather than an unhandled exception.
        UserSigningKey keyWithBadKeyBytes = new()
        {
            Id = 1,
            UserId = 1,
            TenantId = 1,
            Label = "Malformed Key",
            // Valid base64 but not a valid 32-byte Ed25519 public key (only 4 bytes).
            PublicKey = Convert.ToBase64String(new byte[] { 0x01, 0x02, 0x03, 0x04 }),
            PublicKeyFingerprint = "abc",
            CreatedAt = DateTimeOffset.UtcNow,
            RevokedAt = null,
        };

        _signingKeyRepository.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(keyWithBadKeyBytes);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(signingKeyId: 1);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    // ========== GetCommandHistoryAsync — Delegates to repository ==========

    [Test]
    public async Task GetCommandHistory_ReturnsPagedResultsFromRepository()
    {
        // Intent: GetCommandHistoryAsync is responsible for retrieving paginated command
        // history for a machine and must pass the caller's pagination parameters to the repository.
        List<RemoteCommand> expectedCommands = new()
        {
            TestDataBuilder.BuildRemoteCommand(machineId: 5, tenantId: 2),
            TestDataBuilder.BuildRemoteCommand(machineId: 5, tenantId: 2),
        };

        _remoteCommandRepository.GetCommandsForMachineAsync(5, 2, 1, 10, Arg.Any<CancellationToken>())
            .Returns(expectedCommands);

        RemoteCommandService service = CreateService();

        List<RemoteCommand> result = await service.GetCommandHistoryAsync(5, 2, 1, 10, CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(2);
        await _remoteCommandRepository.Received(1).GetCommandsForMachineAsync(5, 2, 1, 10, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetCommandHistory_NoCommandsForMachine_ReturnsEmptyList()
    {
        // Intent: A machine that has never had commands issued should return an empty list
        // rather than null or an error result.
        _remoteCommandRepository.GetCommandsForMachineAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<RemoteCommand>());

        RemoteCommandService service = CreateService();

        List<RemoteCommand> result = await service.GetCommandHistoryAsync(99, 1, 1, 10, CancellationToken.None);

        await Assert.That(result).IsEmpty();
    }
}
