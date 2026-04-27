// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Commands;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
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
    private readonly IDatabaseCache _cache;
    private readonly InMemoryMachinePingService _pingService = new();
    private readonly ILogger<RemoteCommandService> _logger = Substitute.For<ILogger<RemoteCommandService>>();

    public RemoteCommandServiceTests()
    {
        _cache = Substitute.For<IDatabaseCache>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        _cache.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(mockTransaction);
    }

    private RemoteCommandService CreateService() => new(_cache, _pingService, _logger);

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
        _cache.GetSigningKeyByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);
        _cache.GetMachineAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
        _cache.IsKeyAuthorizedForMachineAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _cache.GetRemoteCommandByCommandIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((RemoteCommand?)null);
        _cache.IsNonceUsedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _cache.CreateRemoteCommandAsync(Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                RemoteCommand c = callInfo.Arg<RemoteCommand>();
                c.Id = 100;

                return c;
            });
        _cache.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
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
        _cache.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(revokedKey);

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
        _cache.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(otherUsersKey);

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
        _cache.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(wrongTenantKey);

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
        _cache.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(key);

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
        _cache.GetSigningKeyByIdAsync(1, Arg.Any<CancellationToken>()).Returns(key);
        _cache.GetMachineAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
        _cache.GetRemoteCommandByCommandIdAsync(duplicateId, Arg.Any<CancellationToken>())
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
        _cache.IsNonceUsedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
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
        _cache.GetSigningKeyByIdAsync(999, Arg.Any<CancellationToken>())
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

        await _cache.Received(1).InsertAuditLogAsync(
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
        _cache.GetRemoteCommandByIdAsync(1, 999, Arg.Any<CancellationToken>())
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
        _cache.GetRemoteCommandByIdAsync(42, 1, Arg.Any<CancellationToken>()).Returns(cmd);

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
        _cache.GetSigningKeyByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);
        _cache.GetMachineAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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

        _cache.IsKeyAuthorizedForMachineAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
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
}
