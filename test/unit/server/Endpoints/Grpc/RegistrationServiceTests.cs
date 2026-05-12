// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Grpc.AgentRegistration;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Services.Core.Machines;
using Grpc.Core.Testing;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Endpoints.Grpc;

/// <summary>
/// Unit tests for <see cref="RegistrationService"/>.
/// </summary>
public sealed class RegistrationServiceTests
{
    private readonly IMachineService _machineService = Substitute.For<IMachineService>();
    private readonly ILogger<RegistrationService> _logger = Substitute.For<ILogger<RegistrationService>>();

    private RegistrationService CreateService()
    {
        return new RegistrationService(_machineService, _logger);
    }

    private static ServerCallContext CreateCallContext()
    {
        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: new Metadata(),
            cancellationToken: CancellationToken.None,
            peer: "127.0.0.1",
            authContext: null,
            contextPropagationToken: null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => new WriteOptions(),
            writeOptionsSetter: _ => { });
    }

    // --- GetRegistrationStatus tests ---

    [Test]
    public async Task GetRegistrationStatus_EmptySerialNumber_ThrowsInvalidArgument()
    {
        RegistrationService service = CreateService();
        ServerCallContext context = CreateCallContext();

        SystemRegistrationStatusRequest request = new()
        {
            SerialNumber = "",
            SystemId = "SYS-001"
        };

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await service.GetRegistrationStatus(request, context));
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(ex.Status.Detail).IsEqualTo("Serial number is required");
    }

    [Test]
    public async Task GetRegistrationStatus_EmptySystemId_ThrowsInvalidArgument()
    {
        RegistrationService service = CreateService();
        ServerCallContext context = CreateCallContext();

        SystemRegistrationStatusRequest request = new()
        {
            SerialNumber = "SN-001",
            SystemId = ""
        };

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await service.GetRegistrationStatus(request, context));
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(ex.Status.Detail).IsEqualTo("System ID is required");
    }

    [Test]
    public async Task GetRegistrationStatus_ValidRequest_ReturnsServiceResult()
    {
        _machineService.GetRegistrationStatusAsync("SN-001", "SYS-001", "test-token", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((RegistrationStatus.RegistrationActive, (long?)42, "test-api-key"));

        RegistrationService service = CreateService();
        ServerCallContext context = CreateCallContext();

        SystemRegistrationStatusRequest request = new()
        {
            SerialNumber = "SN-001",
            SystemId = "SYS-001",
            RegistrationToken = "test-token"
        };

        SystemRegistrationStatusResponse response = await service.GetRegistrationStatus(request, context);

        await Assert.That(response.Status).IsEqualTo(RegistrationStatus.RegistrationActive);
        await Assert.That(response.MachineId).IsEqualTo(42);
        await Assert.That(response.ApiKey).IsEqualTo("test-api-key");
        await Assert.That(response.ErrorMessage).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task GetRegistrationStatus_ServiceThrows_ThrowsInternalError()
    {
        _machineService.GetRegistrationStatusAsync("SN-001", "SYS-001", "test-token", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Database error"));

        RegistrationService service = CreateService();
        ServerCallContext context = CreateCallContext();

        SystemRegistrationStatusRequest request = new()
        {
            SerialNumber = "SN-001",
            SystemId = "SYS-001",
            RegistrationToken = "test-token"
        };

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await service.GetRegistrationStatus(request, context));
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.Internal);
        await Assert.That(ex.Status.Detail).IsEqualTo("Internal server error");
    }

    [Test]
    public async Task GetRegistrationStatus_EmptyRegistrationToken_ThrowsInvalidArgument()
    {
        RegistrationService service = CreateService();
        ServerCallContext context = CreateCallContext();

        SystemRegistrationStatusRequest request = new()
        {
            SerialNumber = "SN-001",
            SystemId = "SYS-001",
            RegistrationToken = ""
        };

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await service.GetRegistrationStatus(request, context));
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(ex.Status.Detail).IsEqualTo("Registration token is required");
    }

    [Test]
    public async Task GetRegistrationStatus_NullMachineId_ReturnsZeroMachineId()
    {
        _machineService.GetRegistrationStatusAsync("SN-001", "SYS-001", "test-token", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((RegistrationStatus.UnknownRegistration, (long?)null, (string?)null));

        RegistrationService service = CreateService();
        ServerCallContext context = CreateCallContext();

        SystemRegistrationStatusRequest request = new()
        {
            SerialNumber = "SN-001",
            SystemId = "SYS-001",
            RegistrationToken = "test-token"
        };

        SystemRegistrationStatusResponse response = await service.GetRegistrationStatus(request, context);

        await Assert.That(response.Status).IsEqualTo(RegistrationStatus.UnknownRegistration);
        await Assert.That(response.MachineId).IsEqualTo(0);
    }

    // --- RegisterSystem tests ---

    [Test]
    public async Task RegisterSystem_EmptyHostname_ThrowsInvalidArgument()
    {
        RegistrationService service = CreateService();
        ServerCallContext context = CreateCallContext();

        RegisterSystemRequest request = new()
        {
            Hostname = "",
            SerialNumber = "SN-001",
            SystemId = "SYS-001",
            MachineType = MachineType.BareMetalServerType,
            Os = FleetManagement.Grpc.AgentRegistration.OperatingSystemType.UbuntuOs
        };

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await service.RegisterSystem(request, context));
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(ex.Status.Detail).IsEqualTo("Hostname is required");
    }

    [Test]
    public async Task RegisterSystem_EmptySerialNumber_ThrowsInvalidArgument()
    {
        RegistrationService service = CreateService();
        ServerCallContext context = CreateCallContext();

        RegisterSystemRequest request = new()
        {
            Hostname = "host-1",
            SerialNumber = "",
            SystemId = "SYS-001"
        };

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await service.RegisterSystem(request, context));
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(ex.Status.Detail).IsEqualTo("Serial number is required");
    }

    [Test]
    public async Task RegisterSystem_EmptySystemId_ThrowsInvalidArgument()
    {
        RegistrationService service = CreateService();
        ServerCallContext context = CreateCallContext();

        RegisterSystemRequest request = new()
        {
            Hostname = "host-1",
            SerialNumber = "SN-001",
            SystemId = ""
        };

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await service.RegisterSystem(request, context));
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(ex.Status.Detail).IsEqualTo("System ID is required");
    }

    [Test]
    public async Task RegisterSystem_ValidRequest_ReturnsMachineIdAndApiKey()
    {
        _machineService.RegisterSystemAsync(Arg.Any<RegisterSystemRequest>(), Arg.Any<CancellationToken>())
            .Returns(((long?)99, "test-api-key", string.Empty));

        RegistrationService service = CreateService();
        ServerCallContext context = CreateCallContext();

        RegisterSystemRequest request = new()
        {
            Hostname = "host-1",
            SerialNumber = "SN-001",
            SystemId = "SYS-001",
            MachineType = MachineType.BareMetalServerType,
            Os = FleetManagement.Grpc.AgentRegistration.OperatingSystemType.UbuntuOs
        };

        RegisterSystemResponse response = await service.RegisterSystem(request, context);

        await Assert.That(response.MachineId).IsEqualTo(99);
        await Assert.That(response.ApiKey).IsEqualTo("test-api-key");
        await Assert.That(response.ErrorMessage).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task RegisterSystem_ServiceThrows_ThrowsInternalError()
    {
        _machineService.RegisterSystemAsync(Arg.Any<RegisterSystemRequest>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Database error"));

        RegistrationService service = CreateService();
        ServerCallContext context = CreateCallContext();

        RegisterSystemRequest request = new()
        {
            Hostname = "host-1",
            SerialNumber = "SN-001",
            SystemId = "SYS-001",
            MachineType = MachineType.BareMetalServerType,
            Os = FleetManagement.Grpc.AgentRegistration.OperatingSystemType.UbuntuOs
        };

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await service.RegisterSystem(request, context));
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.Internal);
        await Assert.That(ex.Status.Detail).IsEqualTo("Internal server error");
    }

    [Test]
    public async Task RegisterSystem_ServiceReturnsError_ThrowsInvalidArgument()
    {
        _machineService.RegisterSystemAsync(Arg.Any<RegisterSystemRequest>(), Arg.Any<CancellationToken>())
            .Returns(((long?)null, (string?)null, "Machine limit reached"));

        RegistrationService service = CreateService();
        ServerCallContext context = CreateCallContext();

        RegisterSystemRequest request = new()
        {
            Hostname = "host-1",
            SerialNumber = "SN-001",
            SystemId = "SYS-001",
            MachineType = MachineType.BareMetalServerType,
            Os = FleetManagement.Grpc.AgentRegistration.OperatingSystemType.UbuntuOs
        };

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await service.RegisterSystem(request, context));
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(ex.Status.Detail).IsEqualTo("Machine limit reached");
    }
}
