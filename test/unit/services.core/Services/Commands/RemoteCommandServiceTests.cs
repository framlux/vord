// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Commands;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Kubernetes;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using System.Diagnostics.Metrics;

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
    private readonly IQuotaPauseService _quotaPause = Substitute.For<IQuotaPauseService>();
    private readonly QuotaPauseMetrics _quotaPauseMetrics = TestMetricsFactory.CreateQuotaPauseMetrics();
    private readonly IKubernetesCommandEligibility _kubernetesEligibility = Substitute.For<IKubernetesCommandEligibility>();
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
        _kubernetesEligibility
            .CheckAsync(Arg.Any<int>(), Arg.Any<Machine>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<bool>.Ok(true));
    }

    private RemoteCommandService CreateService() => new(_transactionProvider, _auditLog, _machineRepository, _signingKeyRepository, _remoteCommandRepository, _pingService, _quotaPause, _quotaPauseMetrics, _kubernetesEligibility, _logger);

    // ========== Constructor null guard validation ==========

    [Test]
    public async Task Constructor_NullTransactionProvider_Throws()
    {
        // Intent: A null transaction provider must be caught at construction time so any
        // misconfigured DI container fails loudly rather than causing a deferred NullReferenceException.
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            RemoteCommandService _ = new(null!, _auditLog, _machineRepository, _signingKeyRepository, _remoteCommandRepository, _pingService, _quotaPause, _quotaPauseMetrics, _kubernetesEligibility, _logger);

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
            RemoteCommandService _ = new(_transactionProvider, null!, _machineRepository, _signingKeyRepository, _remoteCommandRepository, _pingService, _quotaPause, _quotaPauseMetrics, _kubernetesEligibility, _logger);

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
            RemoteCommandService _ = new(_transactionProvider, _auditLog, null!, _signingKeyRepository, _remoteCommandRepository, _pingService, _quotaPause, _quotaPauseMetrics, _kubernetesEligibility, _logger);

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
            RemoteCommandService _ = new(_transactionProvider, _auditLog, _machineRepository, null!, _remoteCommandRepository, _pingService, _quotaPause, _quotaPauseMetrics, _kubernetesEligibility, _logger);

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
            RemoteCommandService _ = new(_transactionProvider, _auditLog, _machineRepository, _signingKeyRepository, null!, _pingService, _quotaPause, _quotaPauseMetrics, _kubernetesEligibility, _logger);

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
            RemoteCommandService _ = new(_transactionProvider, _auditLog, _machineRepository, _signingKeyRepository, _remoteCommandRepository, null!, _quotaPause, _quotaPauseMetrics, _kubernetesEligibility, _logger);

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
            RemoteCommandService _ = new(_transactionProvider, _auditLog, _machineRepository, _signingKeyRepository, _remoteCommandRepository, _pingService, _quotaPause, _quotaPauseMetrics, _kubernetesEligibility, null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    [Test]
    public async Task Constructor_NullQuotaPause_Throws()
    {
        // Intent: A null quota pause service must be caught at construction.
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            RemoteCommandService _ = new(_transactionProvider, _auditLog, _machineRepository, _signingKeyRepository, _remoteCommandRepository, _pingService, null!, _quotaPauseMetrics, _kubernetesEligibility, _logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("quotaPause");
    }

    [Test]
    public async Task Constructor_NullQuotaPauseMetrics_Throws()
    {
        // Intent: A null metrics instrument must be caught at construction, not at the first failed check.
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            RemoteCommandService _ = new(_transactionProvider, _auditLog, _machineRepository, _signingKeyRepository, _remoteCommandRepository, _pingService, _quotaPause, null!, _kubernetesEligibility, _logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("quotaPauseMetrics");
    }

    [Test]
    public async Task Constructor_NullKubernetesEligibility_Throws()
    {
        // Intent: a missing eligibility check would silently let every cluster reporter command through
        // to any machine, so it must fail at construction.
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            RemoteCommandService _ = new(_transactionProvider, _auditLog, _machineRepository, _signingKeyRepository, _remoteCommandRepository, _pingService, _quotaPause, _quotaPauseMetrics, null!, _logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("kubernetesEligibility");
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
        _machineRepository.GetActiveMachineByIdAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
        _machineRepository.GetActiveMachineByIdAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
        _machineRepository.GetActiveMachineByIdAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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

    // ========== SubmitCommandAsync — Paused host ==========

    [Test]
    public async Task SubmitCommand_PausedTarget_Returns409WithTheReasonAndCreatesNothing()
    {
        // Intent: a paused host never polls for commands, so accepting one would queue it silently
        // until it expired. Refusing up front tells the user why, and leaves no row or audit entry.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        _quotaPause.IsPausedAsync(key.TenantId, 1, Arg.Any<CancellationToken>()).Returns(true);

        RemoteCommand command = BuildSignedCommand(privateKey);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.ErrorMessage).IsEqualTo("Host is paused: your plan's host limit is reached.");
        await _remoteCommandRepository.DidNotReceiveWithAnyArgs().CreateRemoteCommandAsync(default!, default);
        await _auditLog.DidNotReceiveWithAnyArgs().InsertAuditLogAsync(default!, default);
    }

    [Test]
    public async Task SubmitCommand_PausedTarget_IsCheckedForTheTargetMachineInTheCommandsTenant()
    {
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey(tenantId: 7);
        SetupValidCommandMocks(key, machineId: 55);

        RemoteCommand command = BuildSignedCommand(privateKey, tenantId: 7, machineId: 55);
        RemoteCommandService service = CreateService();

        await service.SubmitCommandAsync(command, CancellationToken.None);

        await _quotaPause.Received(1).IsPausedAsync(7, 55, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubmitCommand_ActiveTarget_IsAccepted()
    {
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        _quotaPause.IsPausedAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(false);

        RemoteCommand command = BuildSignedCommand(privateKey);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task SubmitCommand_PauseCheckUnavailable_LetsTheCommandThrough()
    {
        // Intent: a broken quota check must never stop a paying fleet being managed.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        _quotaPause.IsPausedAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new QuotaCheckUnavailableException(1, new InvalidOperationException("db")));

        RemoteCommand command = BuildSignedCommand(privateKey);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task SubmitCommand_PauseCheckUnavailable_CountsTheFailureAgainstTheCommandsSurface()
    {
        IMeterFactory meterFactory = TestMetricsFactory.CreateMeterFactory();
        using MetricCollector<long> failures = new(meterFactory, VordMeter.Name, "vord.quota.pause_check_failures");
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        _quotaPause.IsPausedAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new QuotaCheckUnavailableException(1, new InvalidOperationException("db")));
        RemoteCommandService service = new(_transactionProvider, _auditLog, _machineRepository, _signingKeyRepository, _remoteCommandRepository, _pingService, _quotaPause, new QuotaPauseMetrics(meterFactory), _kubernetesEligibility, _logger);

        await service.SubmitCommandAsync(BuildSignedCommand(privateKey), CancellationToken.None);

        IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["surface"]).IsEqualTo("commands");
    }

    [Test]
    public async Task SubmitCommand_PauseCheckCanceled_PropagatesTheCancellation()
    {
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        _quotaPause.IsPausedAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new OperationCanceledException());
        RemoteCommandService service = CreateService();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await service.SubmitCommandAsync(BuildSignedCommand(privateKey), CancellationToken.None));
    }

    [Test]
    public async Task SubmitCommand_UnauthorizedKeyOnAPausedHost_ReturnsForbiddenWithoutAskingThePauseService()
    {
        // Intent: authorization comes before state. A same-tenant caller whose key is not authorized for
        // the machine is told so, not that the host is paused, and the request costs no pause lookup.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        _signingKeyRepository.IsKeyAuthorizedForMachineAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _quotaPause.IsPausedAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(BuildSignedCommand(privateKey), CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(403);
        await Assert.That(result.ErrorMessage).IsEqualTo("Signing key is not authorized for this machine");
        await _quotaPause.DidNotReceiveWithAnyArgs().IsPausedAsync(default, default, default);
    }

    [Test]
    public async Task SubmitCommand_UnknownMachine_IsRejectedBeforeThePauseCheck()
    {
        // Intent: whether a host is paused is only revealed for a machine the caller's tenant owns.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        _signingKeyRepository.GetSigningKeyByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);
        _machineRepository.GetActiveMachineByIdAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((Machine?)null);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(BuildSignedCommand(privateKey), CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await _quotaPause.DidNotReceiveWithAnyArgs().IsPausedAsync(default, default, default);
    }

    // ========== SubmitCommandAsync — Cluster reporter commands ==========

    [Test]
    [Arguments("k8s_reporter_enrol")]
    [Arguments("k8s_reporter_unenrol")]
    [Arguments("k8s_reporter_uninstall")]
    public async Task SubmitCommand_KubernetesReporterCommand_IsOnTheAllowlistAndStoredWhenEligible(string commandType)
    {
        // Intent: the three cluster reporter commands travel the same signed path as every other command
        // once the eligibility check passes.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(
            BuildSignedCommand(privateKey, commandType: commandType), CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.CommandType).IsEqualTo(commandType);
        await _remoteCommandRepository.Received(1).CreateRemoteCommandAsync(
            Arg.Is<RemoteCommand>(c => c.CommandType == commandType), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubmitCommand_IneligibleMachine_Returns400WithTheReasonAndCreatesNothing()
    {
        // Intent: the reason the eligibility check gives reaches the caller as a 400, and an ineligible
        // command leaves neither a command row nor an audit entry.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        _kubernetesEligibility
            .CheckAsync(Arg.Any<int>(), Arg.Any<Machine>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<bool>.BadRequest("Only control-plane machines with an admin credential can enrol"));
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(
            BuildSignedCommand(privateKey, commandType: "k8s_reporter_enrol"), CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).IsEqualTo("Only control-plane machines with an admin credential can enrol");
        await _remoteCommandRepository.DidNotReceiveWithAnyArgs().CreateRemoteCommandAsync(default!, default);
        await _auditLog.DidNotReceiveWithAnyArgs().InsertAuditLogAsync(default!, default);
    }

    [Test]
    public async Task SubmitCommand_EligibilityIsGivenTheTenantMachineTypeAndParams()
    {
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey(tenantId: 7);
        SetupValidCommandMocks(key, machineId: 55);
        RemoteCommand command = BuildSignedCommand(privateKey, tenantId: 7, machineId: 55, commandType: "k8s_reporter_unenrol");
        command.Params = "{}";
        RemoteCommandService service = CreateService();

        await service.SubmitCommandAsync(command, CancellationToken.None);

        await _kubernetesEligibility.Received(1).CheckAsync(
            7,
            Arg.Is<Machine>(m => m.Id == 55),
            "k8s_reporter_unenrol",
            "{}",
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubmitCommand_ExistingCommandType_StillGoesThroughTheEligibilityCheck()
    {
        // Intent: the service never decides by itself which types the check applies to, so a spelling of
        // a Kubernetes type cannot slip past a pre-filter. Every command is handed to the check.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        RemoteCommandService service = CreateService();

        await service.SubmitCommandAsync(BuildSignedCommand(privateKey, commandType: "reboot"), CancellationToken.None);

        await _kubernetesEligibility.Received(1).CheckAsync(
            Arg.Any<int>(), Arg.Any<Machine>(), "reboot", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubmitCommand_UnauthorizedKey_IsRefusedBeforeTheEligibilityCheck()
    {
        // Intent: whether a machine is eligible is only revealed to a caller whose key is authorized for
        // it, so authorization comes first and costs no eligibility lookup.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        _signingKeyRepository.IsKeyAuthorizedForMachineAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(false);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(
            BuildSignedCommand(privateKey, commandType: "k8s_reporter_enrol"), CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(403);
        await _kubernetesEligibility.DidNotReceiveWithAnyArgs().CheckAsync(default, default!, default!, default, default);
    }

    [Test]
    public async Task SubmitCommand_UnknownMachine_IsRefusedBeforeTheEligibilityCheck()
    {
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        _signingKeyRepository.GetSigningKeyByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);
        _machineRepository.GetActiveMachineByIdAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((Machine?)null);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(
            BuildSignedCommand(privateKey, commandType: "k8s_reporter_enrol"), CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await _kubernetesEligibility.DidNotReceiveWithAnyArgs().CheckAsync(default, default!, default!, default, default);
    }

    [Test]
    [Arguments("K8S_REPORTER_ENROL")]
    [Arguments("k8s_reporter_Unenrol")]
    [Arguments("K8s_Reporter_Uninstall")]
    public async Task SubmitCommand_MixedCaseKubernetesType_IsRefusedAsAnInvalidCommandType(string commandType)
    {
        // Intent: the allowlist ignores case, so the only thing standing between a mixed-case spelling
        // and a machine that was never checked is the real eligibility check. With it wired in, such a
        // command is refused even for a machine that would be eligible, and nothing is stored.
        IKubernetesInventoryRepository kubernetesRepository = Substitute.For<IKubernetesInventoryRepository>();
        KubernetesCommandEligibility realEligibility = new(kubernetesRepository, new FakeTimeProvider());
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        RemoteCommandService service = new(_transactionProvider, _auditLog, _machineRepository, _signingKeyRepository, _remoteCommandRepository, _pingService, _quotaPause, _quotaPauseMetrics, realEligibility, _logger);

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(
            BuildSignedCommand(privateKey, commandType: commandType), CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).IsEqualTo("Invalid command type");
        await _remoteCommandRepository.DidNotReceiveWithAnyArgs().CreateRemoteCommandAsync(default!, default);
        await _auditLog.DidNotReceiveWithAnyArgs().InsertAuditLogAsync(default!, default);
    }

    [Test]
    public async Task SubmitCommand_UnknownCommandType_IsStillRefusedByTheAllowlist()
    {
        // Intent: adding the reporter commands must not widen the allowlist to anything else that looks
        // like them.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(
            BuildSignedCommand(privateKey, commandType: "k8s_reporter_destroy"), CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).IsEqualTo("Invalid command type");
        await _kubernetesEligibility.DidNotReceiveWithAnyArgs().CheckAsync(default, default!, default!, default, default);
    }

    [Test]
    public async Task SubmitCommand_IneligibleAndPausedHost_GetsTheEligibilityRefusalNotThePauseConflict()
    {
        // Intent: the eligibility check comes before the pause check, so a refusal that no waiting can
        // fix is not reported as a pause that might lift. The pause lookup is never made.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        _quotaPause.IsPausedAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        _kubernetesEligibility
            .CheckAsync(Arg.Any<int>(), Arg.Any<Machine>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<bool>.BadRequest("This machine is not an enrolled reporter"));
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(
            BuildSignedCommand(privateKey, commandType: "k8s_reporter_unenrol"), CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).IsEqualTo("This machine is not an enrolled reporter");
        await _quotaPause.DidNotReceiveWithAnyArgs().IsPausedAsync(default, default, default);
    }

    [Test]
    public async Task SubmitCommand_IneligibleHostWithoutTheCommandsCapability_GetsTheEligibilityRefusal()
    {
        // Intent: the eligibility check also comes before the capability check, so the reason the
        // caller sees is about the machine's Kubernetes state, not about a setting that, once changed,
        // would only lead to the next refusal.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        _pingService.SetAgentCapabilitiesAsync(1, 0UL).GetAwaiter().GetResult();
        _kubernetesEligibility
            .CheckAsync(Arg.Any<int>(), Arg.Any<Machine>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<bool>.BadRequest("Only control-plane machines with an admin credential can enrol"));
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(
            BuildSignedCommand(privateKey, commandType: "k8s_reporter_enrol"), CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).IsEqualTo("Only control-plane machines with an admin credential can enrol");
    }

    [Test]
    public async Task SubmitCommand_EligibleHostWithoutTheCommandsCapability_IsStillRefusedForTheCapability()
    {
        // Intent: passing the eligibility check does not bypass the capability check that follows it.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        _pingService.SetAgentCapabilitiesAsync(1, 0UL).GetAwaiter().GetResult();
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(
            BuildSignedCommand(privateKey, commandType: "k8s_reporter_enrol"), CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).IsEqualTo("Remote commands are not enabled on this machine");
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("\t\n")]
    public async Task SubmitCommand_BlankParams_AreStoredAsNull(string blankParams)
    {
        // Intent: parameters are stored as JSON, and a blank string is not valid JSON, so a database that
        // checks it would refuse the insert. A blank value means no parameters and is stored as none.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        RemoteCommand command = BuildSignedCommand(privateKey, commandType: "reboot");
        command.Params = blankParams;
        RemoteCommandService service = CreateService();

        ServiceResult<RemoteCommand> result = await service.SubmitCommandAsync(command, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await _remoteCommandRepository.Received(1).CreateRemoteCommandAsync(
            Arg.Is<RemoteCommand>(c => c.Params == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubmitCommand_RealParams_AreStoredUnchanged()
    {
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        SetupValidCommandMocks(key);
        RemoteCommand command = BuildSignedCommand(privateKey, commandType: "kill_process");
        command.Params = """{"pid":"42"}""";
        RemoteCommandService service = CreateService();

        await service.SubmitCommandAsync(command, CancellationToken.None);

        await _remoteCommandRepository.Received(1).CreateRemoteCommandAsync(
            Arg.Is<RemoteCommand>(c => c.Params == """{"pid":"42"}"""), Arg.Any<CancellationToken>());
    }

    // ========== SubmitCommandAsync — Signing key not authorized for machine ==========

    [Test]
    public async Task SubmitCommand_SigningKeyNotAuthorizedForMachine_ReturnsForbidden()
    {
        // Intent: Even if a signing key is valid and active, it must be explicitly authorized
        // for the target machine. Submitting without that authorization must be rejected.
        (UserSigningKey key, NSec.Cryptography.Key privateKey) = BuildSignedKey();
        _signingKeyRepository.GetSigningKeyByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);
        _machineRepository.GetActiveMachineByIdAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
