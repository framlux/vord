// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Grpc.AgentRegistration;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Machines;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;

namespace Framlux.FleetManagement.Server.Endpoints.Grpc;

/// <summary>
/// gRPC service for handling system registration.
/// </summary>
public sealed class RegistrationService : Registration.RegistrationBase
{
    private readonly IMachineService _machineService;
    private readonly ILogger<RegistrationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RegistrationService"/> class.
    /// </summary>
    /// <param name="service">The Machine Service</param>
    /// <param name="logger">The application-wide logger instance</param>
    /// <exception cref="ArgumentNullException"></exception>
    public RegistrationService(IMachineService service, ILogger<RegistrationService> logger)
    {
        _machineService = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Handles the GetRegistrationStatus gRPC request.
    /// </summary>
    /// <param name="request">The gRPC request payload</param>
    /// <param name="context">The gRPC request context</param>
    /// <returns>Returns the registration status response</returns>
    [AllowAnonymous]
    public override async Task<SystemRegistrationStatusResponse> GetRegistrationStatus(SystemRegistrationStatusRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.SerialNumber))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Serial number is required"));
        }
        else if (string.IsNullOrEmpty(request.SystemId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "System ID is required"));
        }
        else if (string.IsNullOrEmpty(request.RegistrationToken))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Registration token is required"));
        }

        RegistrationStatus status;
        long? machineId = null;
        string? apiKey = null;

        try
        {
            (status, machineId, apiKey) = await _machineService.GetRegistrationStatusAsync(request.SerialNumber, request.SystemId, request.RegistrationToken, request.NeedsApiKey, context.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving registration status for Serial Number {SerialNumber} and System ID {SystemId}", request.SerialNumber, request.SystemId);

            throw new RpcException(new Status(StatusCode.Internal, "Internal server error"));
        }

        return new SystemRegistrationStatusResponse()
        {
            Status = status,
            MachineId = machineId ?? 0,
            ApiKey = apiKey ?? string.Empty,
            ErrorMessage = string.Empty,
        };
    }

    /// <summary>
    /// Handles the RegisterSystem gRPC request.
    /// </summary>
    /// <param name="request">The gRPC request payload</param>
    /// <param name="context">The gRPC request context</param>
    /// <returns>Returns the registration request response</returns>
    [AllowAnonymous]
    public override async Task<RegisterSystemResponse> RegisterSystem(RegisterSystemRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.Hostname))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Hostname is required"));
        }
        else if (string.IsNullOrEmpty(request.SerialNumber))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Serial number is required"));
        }
        else if (string.IsNullOrEmpty(request.SystemId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "System ID is required"));
        }
        else if ((request.MachineType < MachineType.UnknownType) || (request.MachineType > MachineType.VirtualMachineType))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid machine type"));
        }
        else if ((request.Os < FleetManagement.Grpc.AgentRegistration.OperatingSystemType.UnknownOs) || (request.Os > FleetManagement.Grpc.AgentRegistration.OperatingSystemType.DebianOs))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid operating system"));
        }
        else
        {
            (long? machineId, string? apiKey, string errorMessage) result;

            try
            {
                result = await _machineService.RegisterSystemAsync(request, context.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering system with Serial Number {SerialNumber} and System ID {SystemId}", request.SerialNumber, request.SystemId);

                throw new RpcException(new Status(StatusCode.Internal, "Internal server error"));
            }

            if (result.machineId.HasValue && string.IsNullOrEmpty(result.apiKey) == false)
            {
                return new RegisterSystemResponse()
                {
                    MachineId = result.machineId.Value,
                    ApiKey = result.apiKey,
                    ErrorMessage = string.Empty,
                };
            }
            else
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, result.errorMessage));
            }
        }
    }
}
