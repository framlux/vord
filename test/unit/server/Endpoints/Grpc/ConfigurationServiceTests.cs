// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Grpc.AgentConfiguration;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Framlux.FleetManagement.Test.Infrastructure;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using System.Security.Claims;

namespace Framlux.FleetManagement.Test.Endpoints.Grpc;

/// <summary>
/// Unit tests for <see cref="ConfigurationService"/>.
/// </summary>
public sealed class ConfigurationServiceTests
{
    private readonly IMachinePingService _pingService = Substitute.For<IMachinePingService>();
    private readonly ILogger<ConfigurationService> _logger = Substitute.For<ILogger<ConfigurationService>>();

    private ConfigurationService CreateService(
        IServerSettingsCache? settingsCache = null,
        ISigningKeyRepository? signingKeyRepo = null,
        IRemoteCommandRepository? remoteCommandRepo = null)
    {
        IServerSettingsCache resolvedSettingsCache = settingsCache ?? Substitute.For<IServerSettingsCache>();
        ISigningKeyRepository resolvedSigningKeyRepo = signingKeyRepo ?? Substitute.For<ISigningKeyRepository>();
        IRemoteCommandRepository resolvedRemoteCommandRepo = remoteCommandRepo ?? Substitute.For<IRemoteCommandRepository>();
        resolvedSigningKeyRepo.GetActiveSigningKeysForMachineAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.UserSigningKey>());
        ServerConfigurationService configService = new(resolvedSettingsCache, Substitute.For<IConnectionMultiplexer>());

        return new ConfigurationService(resolvedSigningKeyRepo, resolvedRemoteCommandRepo, _pingService, configService, _logger);
    }

    private static ServerCallContext CreateAuthenticatedContext(long machineId, int tenantId = 1)
    {
        DefaultHttpContext httpContext = new();
        ClaimsIdentity identity = new("ApiKey");
        identity.AddClaim(new Claim("MachineId", machineId.ToString()));
        identity.AddClaim(new Claim("TenantId", tenantId.ToString()));
        httpContext.User = new ClaimsPrincipal(identity);

        return new TestServerCallContext(httpContext, new Metadata());
    }

    [Test]
    public async Task GetConfiguration_NoDbSettings_ReturnsDefaults()
    {
        ConfigurationService service = CreateService();
        ServerCallContext context = CreateAuthenticatedContext(1);

        GetConfigurationResponse response = await service.GetConfiguration(
            new GetConfigurationRequest { MachineId = 1 }, context);

        // Default heartbeat = 300 seconds (5 min), default config refresh = 900 seconds (15 min).
        await Assert.That(response.TimeConfig.HeartbeatTimeInSeconds).IsEqualTo(300);
        await Assert.That(response.TimeConfig.ConfigurationRefreshTimeInSeconds).IsEqualTo(900);
    }

    [Test]
    public async Task GetConfiguration_WithDbSettings_ReturnsConfiguredValues()
    {
        IServerSettingsCache settingsCache = Substitute.For<IServerSettingsCache>();
        settingsCache.GetSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, Arg.Any<CancellationToken>())
            .Returns("60");
        settingsCache.GetSettingAsync(ServerConfigurationSettingKeys.AgentConfigRefreshSeconds, Arg.Any<CancellationToken>())
            .Returns("3600");

        ConfigurationService service = CreateService(settingsCache: settingsCache);
        ServerCallContext context = CreateAuthenticatedContext(1);

        GetConfigurationResponse response = await service.GetConfiguration(
            new GetConfigurationRequest { MachineId = 1 }, context);

        await Assert.That(response.TimeConfig.HeartbeatTimeInSeconds).IsEqualTo(60);
        await Assert.That(response.TimeConfig.ConfigurationRefreshTimeInSeconds).IsEqualTo(3600);
    }

    [Test]
    public async Task GetConfiguration_NoDbSettings_ReturnsDefaultServiceStatusSeconds()
    {
        ConfigurationService service = CreateService();
        ServerCallContext context = CreateAuthenticatedContext(1);

        GetConfigurationResponse response = await service.GetConfiguration(
            new GetConfigurationRequest { MachineId = 1 }, context);

        // Default service status = 3600 seconds (1 hour).
        await Assert.That(response.TimeConfig.ServiceStatusSeconds).IsEqualTo(3600);
    }

    [Test]
    public async Task GetConfiguration_WithServiceStatusSetting_ReturnsConfiguredValue()
    {
        IServerSettingsCache settingsCache = Substitute.For<IServerSettingsCache>();
        settingsCache.GetSettingAsync(ServerConfigurationSettingKeys.ServiceStatusSeconds, Arg.Any<CancellationToken>())
            .Returns("1800");

        ConfigurationService service = CreateService(settingsCache: settingsCache);
        ServerCallContext context = CreateAuthenticatedContext(1);

        GetConfigurationResponse response = await service.GetConfiguration(
            new GetConfigurationRequest { MachineId = 1 }, context);

        await Assert.That(response.TimeConfig.ServiceStatusSeconds).IsEqualTo(1800);
    }

    [Test]
    public async Task GetConfiguration_MismatchedMachineId_ThrowsPermissionDenied()
    {
        ConfigurationService service = CreateService();
        ServerCallContext context = CreateAuthenticatedContext(42);

        GetConfigurationRequest request = new() { MachineId = 99 };

        try
        {
            await service.GetConfiguration(request, context);
            Assert.Fail("Expected RpcException");
        }
        catch (RpcException ex)
        {
            await Assert.That(ex.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
        }
    }

    [Test]
    public async Task AgentPing_ValidRequest_ReturnsSuccess()
    {
        _pingService.RecordPingAsync(Arg.Any<long>()).Returns(Task.CompletedTask);
        ConfigurationService service = CreateService();
        ServerCallContext context = CreateAuthenticatedContext(10);

        AgentPingResponse response = await service.AgentPing(
            new AgentPingRequest { MachineId = 10 }, context);

        await Assert.That(response.Success).IsTrue();
        await _pingService.Received(1).RecordPingAsync(10);
    }

    [Test]
    public async Task AgentPing_MismatchedMachineId_ThrowsPermissionDenied()
    {
        ConfigurationService service = CreateService();
        ServerCallContext context = CreateAuthenticatedContext(42);

        AgentPingRequest request = new() { MachineId = 99 };

        try
        {
            await service.AgentPing(request, context);
            Assert.Fail("Expected RpcException");
        }
        catch (RpcException ex)
        {
            await Assert.That(ex.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
        }
    }

    [Test]
    public async Task AgentPing_PingServiceFailure_ReturnsFalse()
    {
        _pingService.RecordPingAsync(Arg.Any<long>()).Returns(Task.FromException(new InvalidOperationException("Redis down")));
        ConfigurationService service = CreateService();
        ServerCallContext context = CreateAuthenticatedContext(10);

        AgentPingResponse response = await service.AgentPing(
            new AgentPingRequest { MachineId = 10 }, context);

        await Assert.That(response.Success).IsFalse();
    }

    [Test]
    public async Task GetPendingCommands_ValidRequest_ReturnsEmptyResponse()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();
        remoteCommandRepo.GetPendingCommandsForMachineAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.RemoteCommand>());

        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateAuthenticatedContext(1);

        GetPendingCommandsResponse response = await service.GetPendingCommands(
            new GetPendingCommandsRequest { MachineId = 1 }, context);

        await Assert.That(response.Commands.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetPendingCommands_MismatchedMachineId_ThrowsPermissionDenied()
    {
        ConfigurationService service = CreateService();
        ServerCallContext context = CreateAuthenticatedContext(42);

        GetPendingCommandsRequest request = new() { MachineId = 99 };

        try
        {
            await service.GetPendingCommands(request, context);
            Assert.Fail("Expected RpcException");
        }
        catch (RpcException ex)
        {
            await Assert.That(ex.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
        }
    }

    [Test]
    public async Task AcknowledgeCommand_ValidRequest_ReturnsSuccess()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();
        remoteCommandRepo.UpdateRemoteCommandStatusAsync(
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<Database.Enums.RemoteCommandStatus>(),
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateAuthenticatedContext(1);

        AcknowledgeCommandResponse response = await service.AcknowledgeCommand(
            new AcknowledgeCommandRequest
            {
                MachineId = 1,
                CommandId = "cmd-1",
                Result = new CommandResult { Success = true, ExitCode = 0, Message = "OK" }
            }, context);

        await Assert.That(response.Success).IsTrue();
    }

    [Test]
    public async Task AcknowledgeCommand_MismatchedMachineId_ThrowsPermissionDenied()
    {
        ConfigurationService service = CreateService();
        ServerCallContext context = CreateAuthenticatedContext(42);

        AcknowledgeCommandRequest request = new() { MachineId = 99, CommandId = "cmd-1" };

        try
        {
            await service.AcknowledgeCommand(request, context);
            Assert.Fail("Expected RpcException");
        }
        catch (RpcException ex)
        {
            await Assert.That(ex.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
        }
    }

    // ========== T1: GetPendingCommands with actual data ==========

    [Test]
    public async Task GetPendingCommands_WithData_ConvertsToProtoAndMarksDelivered()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();

        Database.Models.RemoteCommand pendingCmd = new()
        {
            Id = 1,
            CommandId = "cmd-abc",
            TenantId = 1,
            MachineId = 1,
            UserId = 1,
            SigningKeyId = 1,
            CommandType = "reboot",
            Params = "{\"delay_minutes\":\"5\"}",
            Nonce = "nonce1",
            Signature = Convert.ToBase64String(new byte[64]),
            CanonicalPayload = "{}",
            Timestamp = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            Status = Database.Enums.RemoteCommandStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        remoteCommandRepo.GetPendingCommandsForMachineAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.RemoteCommand> { pendingCmd });
        remoteCommandRepo.MarkCommandsDeliveredAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateAuthenticatedContext(1, tenantId: 1);

        GetPendingCommandsResponse response = await service.GetPendingCommands(
            new GetPendingCommandsRequest { MachineId = 1 }, context);

        await Assert.That(response.Commands.Count).IsEqualTo(1);
        await Assert.That(response.Commands[0].Id).IsEqualTo("cmd-abc");
        await Assert.That(response.Commands[0].Type).IsEqualTo("reboot");
        await Assert.That(response.Commands[0].Params["delay_minutes"]).IsEqualTo("5");

        await remoteCommandRepo.Received(1).MarkCommandsDeliveredAsync(
            Arg.Is<IEnumerable<string>>(ids => ids.Contains("cmd-abc")),
            Arg.Any<CancellationToken>());
    }

    // ========== T2: AcknowledgeCommand failure and rejected paths ==========

    [Test]
    public async Task AcknowledgeCommand_Failure_MapsFailed()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();
        remoteCommandRepo.UpdateRemoteCommandStatusAsync(
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<Database.Enums.RemoteCommandStatus>(),
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateAuthenticatedContext(1);

        AcknowledgeCommandResponse response = await service.AcknowledgeCommand(
            new AcknowledgeCommandRequest
            {
                MachineId = 1,
                CommandId = "cmd-fail",
                Result = new CommandResult { Success = false, ExitCode = 1, Message = "error", ResultType = Framlux.FleetManagement.Grpc.AgentConfiguration.ResultType.Completed }
            }, context);

        await Assert.That(response.Success).IsTrue();
        await remoteCommandRepo.Received(1).UpdateRemoteCommandStatusAsync(
            "cmd-fail", 1L, Database.Enums.RemoteCommandStatus.Failed,
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcknowledgeCommand_Rejected_MapsRejected()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();
        remoteCommandRepo.UpdateRemoteCommandStatusAsync(
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<Database.Enums.RemoteCommandStatus>(),
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateAuthenticatedContext(1);

        AcknowledgeCommandResponse response = await service.AcknowledgeCommand(
            new AcknowledgeCommandRequest
            {
                MachineId = 1,
                CommandId = "cmd-reject",
                Result = new CommandResult { Success = false, ExitCode = -1, Message = "invalid signature", ResultType = Framlux.FleetManagement.Grpc.AgentConfiguration.ResultType.Rejected }
            }, context);

        await Assert.That(response.Success).IsTrue();
        await remoteCommandRepo.Received(1).UpdateRemoteCommandStatusAsync(
            "cmd-reject", 1L, Database.Enums.RemoteCommandStatus.Rejected,
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcknowledgeCommand_FailedResultType_MapsFailed()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();
        remoteCommandRepo.UpdateRemoteCommandStatusAsync(
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<Database.Enums.RemoteCommandStatus>(),
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateAuthenticatedContext(1);

        AcknowledgeCommandResponse response = await service.AcknowledgeCommand(
            new AcknowledgeCommandRequest
            {
                MachineId = 1,
                CommandId = "cmd-failed-type",
                Result = new CommandResult { Success = false, ExitCode = 1, Message = "reboot failed", ResultType = Framlux.FleetManagement.Grpc.AgentConfiguration.ResultType.Failed }
            }, context);

        await Assert.That(response.Success).IsTrue();
        await remoteCommandRepo.Received(1).UpdateRemoteCommandStatusAsync(
            "cmd-failed-type", 1L, Database.Enums.RemoteCommandStatus.Failed,
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcknowledgeCommand_NullResult_MapsFailed()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();
        remoteCommandRepo.UpdateRemoteCommandStatusAsync(
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<Database.Enums.RemoteCommandStatus>(),
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateAuthenticatedContext(1);

        AcknowledgeCommandResponse response = await service.AcknowledgeCommand(
            new AcknowledgeCommandRequest
            {
                MachineId = 1,
                CommandId = "cmd-null",
            }, context);

        await Assert.That(response.Success).IsTrue();
        await remoteCommandRepo.Received(1).UpdateRemoteCommandStatusAsync(
            "cmd-null", 1L, Database.Enums.RemoteCommandStatus.Failed,
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetConfiguration_WithActiveTenantId_LoadsSigningKeys()
    {
        ISigningKeyRepository signingKeyRepo = Substitute.For<ISigningKeyRepository>();

        // CreateService also stubs this, so we configure the return after creating the service.
        ConfigurationService service = CreateService(signingKeyRepo: signingKeyRepo);

        // Override to return a real signing key — the CreateService factory sets empty by default.
        signingKeyRepo.GetActiveSigningKeysForMachineAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.UserSigningKey>
            {
                new Database.Models.UserSigningKey
                {
                    Id = 1,
                    UserId = 1,
                    TenantId = 1,
                    PublicKey = Convert.ToBase64String(new byte[32]),
                    PublicKeyFingerprint = "fp1",
                    Label = "My Key",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            });

        // tenantId > 0 causes the signing keys branch to execute.
        ServerCallContext context = CreateAuthenticatedContext(machineId: 1, tenantId: 5);

        GetConfigurationResponse response = await service.GetConfiguration(
            new GetConfigurationRequest { MachineId = 1 }, context);

        await Assert.That(response.SigningKeys.Count).IsEqualTo(1);
        await Assert.That(response.SigningKeys[0].KeyId).IsEqualTo(1);
        await signingKeyRepo.Received(1).GetActiveSigningKeysForMachineAsync(1L, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetConfiguration_WithZeroTenantId_SkipsSigningKeyLoad()
    {
        ISigningKeyRepository signingKeyRepo = Substitute.For<ISigningKeyRepository>();
        signingKeyRepo.GetActiveSigningKeysForMachineAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.UserSigningKey>());

        ConfigurationService service = CreateService(signingKeyRepo: signingKeyRepo);

        // tenantId = 0 (no TenantId claim) means the signing keys branch is skipped.
        DefaultHttpContext httpContext = new();
        ClaimsIdentity identity = new("ApiKey");
        identity.AddClaim(new Claim("MachineId", "1"));
        httpContext.User = new ClaimsPrincipal(identity);
        ServerCallContext context = new TestServerCallContext(httpContext, new Metadata());

        GetConfigurationResponse response = await service.GetConfiguration(
            new GetConfigurationRequest { MachineId = 1 }, context);

        await Assert.That(response.SigningKeys.Count).IsEqualTo(0);
        await signingKeyRepo.DidNotReceive().GetActiveSigningKeysForMachineAsync(
            Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Custom <see cref="ServerCallContext"/> subclass for testing gRPC endpoints.
    /// </summary>
    private sealed class TestServerCallContext : ServerCallContext
    {
        private readonly Metadata _requestHeaders;

        /// <summary>
        /// Creates a new test context.
        /// </summary>
        public TestServerCallContext(HttpContext httpContext, Metadata requestHeaders)
        {
            _requestHeaders = requestHeaders;
            UserState["__HttpContext"] = httpContext;
        }

        /// <inheritdoc/>
        protected override string MethodCore => "Test";

        /// <inheritdoc/>
        protected override string HostCore => "localhost";

        /// <inheritdoc/>
        protected override string PeerCore => "127.0.0.1";

        /// <inheritdoc/>
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);

        /// <inheritdoc/>
        protected override Metadata RequestHeadersCore => _requestHeaders;

        /// <inheritdoc/>
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;

        /// <inheritdoc/>
        protected override Metadata ResponseTrailersCore => new();

        /// <inheritdoc/>
        protected override Status StatusCore { get; set; }

        /// <inheritdoc/>
        protected override WriteOptions? WriteOptionsCore { get; set; }

        /// <inheritdoc/>
        protected override AuthContext AuthContextCore => new(string.Empty, new Dictionary<string, List<AuthProperty>>());

        /// <inheritdoc/>
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotImplementedException();

        /// <inheritdoc/>
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
