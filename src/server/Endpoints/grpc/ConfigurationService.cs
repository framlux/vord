// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Grpc.AgentConfiguration;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;

namespace Framlux.FleetManagement.Server.Endpoints.Grpc;

/// <summary>
/// gRPC service for retrieving configuration settings.
/// </summary>
[Authorize(ApiKeyAuthenticationHandler.SchemeName)]
public sealed class ConfigurationService : Configuration.ConfigurationBase
{
    private readonly ISigningKeyRepository _signingKeyRepository;
    private readonly IRemoteCommandRepository _remoteCommandRepository;
    private readonly IMachinePingService _pingService;
    private readonly IMachineRepository _machineRepository;
    private readonly ServerConfigurationService _configService;
    private readonly ILogger<ConfigurationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationService"/> class.
    /// </summary>
    /// <param name="signingKeyRepository">The signing key repository for retrieving trusted keys</param>
    /// <param name="remoteCommandRepository">The remote command repository for pending commands</param>
    /// <param name="pingService">The machine ping tracking service</param>
    /// <param name="machineRepository">The machine repository, used to correct a stale machine type</param>
    /// <param name="configService">The server configuration service for runtime settings</param>
    /// <param name="logger">The application-wide logging service instance</param>
    /// <exception cref="ArgumentNullException"></exception>
    public ConfigurationService(
        ISigningKeyRepository signingKeyRepository,
        IRemoteCommandRepository remoteCommandRepository,
        IMachinePingService pingService,
        IMachineRepository machineRepository,
        ServerConfigurationService configService,
        ILogger<ConfigurationService> logger)
    {
        _signingKeyRepository = signingKeyRepository ?? throw new ArgumentNullException(nameof(signingKeyRepository));
        _remoteCommandRepository = remoteCommandRepository ?? throw new ArgumentNullException(nameof(remoteCommandRepository));
        _pingService = pingService ?? throw new ArgumentNullException(nameof(pingService));
        _machineRepository = machineRepository ?? throw new ArgumentNullException(nameof(machineRepository));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Handles the GetConfiguration gRPC request.
    /// </summary>
    /// <param name="request">The gRPC request payload</param>
    /// <param name="context">The gRPC context</param>
    /// <returns>Returns a configuration response</returns>
    public override async Task<GetConfigurationResponse> GetConfiguration(GetConfigurationRequest request, ServerCallContext context)
    {
        long machineId = ResolveMachineId(context, request.MachineId, "GetConfiguration");

        await ApplyReportedMachineTypeAsync(machineId, request.MachineType, context.CancellationToken);

        int heartbeatSeconds = await _configService.GetAgentHeartbeatSecondsAsync(context.CancellationToken);
        int configRefreshSeconds = await _configService.GetAgentConfigRefreshSecondsAsync(context.CancellationToken);
        int commandPollSeconds = await _configService.GetAgentCommandPollSecondsAsync(context.CancellationToken);
        int telemetryCollectFastSeconds = await _configService.GetTelemetryCollectFastSecondsAsync(context.CancellationToken);
        int telemetryCollectSlowSeconds = await _configService.GetTelemetryCollectSlowSecondsAsync(context.CancellationToken);
        int telemetrySendFastSeconds = await _configService.GetTelemetrySendFastSecondsAsync(context.CancellationToken);
        int telemetrySendSlowSeconds = await _configService.GetTelemetrySendSlowSecondsAsync(context.CancellationToken);
        int serviceStatusSeconds = await _configService.GetServiceStatusSecondsAsync(context.CancellationToken);

        // Include tenant ID so the agent can verify command ownership.
        int tenantId = ExtractTenantIdFromClaims(context);

        GetConfigurationResponse response = new()
        {
            TimeConfig = new TimingConfiguration()
            {
                ConfigurationRefreshTimeInSeconds = configRefreshSeconds,
                HeartbeatTimeInSeconds = heartbeatSeconds,
                CommandPollTimeInSeconds = commandPollSeconds,
                TelemetryCollectFastSeconds = telemetryCollectFastSeconds,
                TelemetryCollectSlowSeconds = telemetryCollectSlowSeconds,
                TelemetrySendFastSeconds = telemetrySendFastSeconds,
                TelemetrySendSlowSeconds = telemetrySendSlowSeconds,
                ServiceStatusSeconds = serviceStatusSeconds,
            },
            TenantId = tenantId,
        };

        if (tenantId > 0)
        {
            List<UserSigningKey> signingKeys = await _signingKeyRepository.GetActiveSigningKeysForMachineAsync(machineId, context.CancellationToken);
            foreach (UserSigningKey key in signingKeys)
            {
                response.SigningKeys.Add(new TrustedSigningKey
                {
                    KeyId = key.Id,
                    UserId = key.UserId,
                    PublicKey = ByteString.FromBase64(key.PublicKey),
                });
            }
        }

        await _pingService.SetAgentCapabilitiesAsync(machineId, request.AgentCapabilities);

        return response;
    }

    /// <summary>
    /// Handles the AgentPing gRPC request.
    /// </summary>
    /// <param name="request">The ping request payload</param>
    /// <param name="context">The gRPC context</param>
    /// <returns>Returns a response indicating the success of the ping</returns>
    public override async Task<AgentPingResponse> AgentPing(AgentPingRequest request, ServerCallContext context)
    {
        long machineId = ResolveMachineId(context, request.MachineId, "AgentPing");
        _logger.LogInformation("Received AgentPing from machine ID {MachineId}", machineId);

        try
        {
            await _pingService.RecordPingAsync(machineId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record ping for machine ID {MachineId}", machineId);

            return new AgentPingResponse { Success = false };
        }

        return new AgentPingResponse { Success = true };
    }

    /// <summary>
    /// Handles the GetPendingCommands gRPC request.
    /// Returns signed commands pending for this machine.
    /// </summary>
    /// <param name="request">The pending commands request payload</param>
    /// <param name="context">The gRPC context</param>
    /// <returns>Returns a response with pending commands</returns>
    public override async Task<GetPendingCommandsResponse> GetPendingCommands(GetPendingCommandsRequest request, ServerCallContext context)
    {
        long machineId = ResolveMachineId(context, request.MachineId, "GetPendingCommands");
        int tenantId = ExtractTenantIdFromClaims(context);

        List<RemoteCommand> pendingCommands = await _remoteCommandRepository.GetPendingCommandsForMachineAsync(machineId, tenantId, context.CancellationToken);

        GetPendingCommandsResponse response = new();
        List<string> deliveredIds = [];

        foreach (RemoteCommand cmd in pendingCommands)
        {
            AgentCommand agentCmd = new()
            {
                Id = cmd.CommandId,
                Type = cmd.CommandType,
                CanonicalPayload = cmd.CanonicalPayload,
                Signature = ByteString.CopyFrom(Convert.FromBase64String(cmd.Signature)),
                SigningKeyId = cmd.SigningKeyId,
                Timestamp = cmd.Timestamp.ToString("o"),
                ExpiresAt = cmd.ExpiresAt.ToString("o"),
                Nonce = cmd.Nonce,
                UserId = cmd.UserId,
                TenantId = cmd.TenantId,
                MachineId = cmd.MachineId,
            };

            if (string.IsNullOrEmpty(cmd.Params) == false)
            {
                using JsonDocument paramsDoc = JsonDocument.Parse(cmd.Params);
                foreach (JsonProperty prop in paramsDoc.RootElement.EnumerateObject())
                {
                    agentCmd.Params[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }

            response.Commands.Add(agentCmd);
            deliveredIds.Add(cmd.CommandId);
        }

        // Mark delivered commands.
        if (deliveredIds.Count > 0)
        {
            await _remoteCommandRepository.MarkCommandsDeliveredAsync(deliveredIds, context.CancellationToken);
        }

        return response;
    }

    /// <summary>
    /// Handles the AcknowledgeCommand gRPC request.
    /// Updates the remote command record with the execution result.
    /// </summary>
    /// <param name="request">The acknowledgement request with command result.</param>
    /// <param name="context">The gRPC context.</param>
    /// <returns>Returns success.</returns>
    public override async Task<AcknowledgeCommandResponse> AcknowledgeCommand(AcknowledgeCommandRequest request, ServerCallContext context)
    {
        long machineId = ResolveMachineId(context, request.MachineId, "AcknowledgeCommand");

        CommandResult? result = request.Result;
        _logger.LogInformation(
            "Command {CommandId} acknowledged by machine {MachineId}: success={Success}, exit_code={ExitCode}, message={Message}",
            request.CommandId,
            machineId,
            result?.Success ?? false,
            result?.ExitCode ?? -1,
            result?.Message ?? string.Empty);

        RemoteCommandStatus status;
        if (result?.ResultType == ResultType.Rejected)
        {
            status = RemoteCommandStatus.Rejected;
        }
        else if (result?.Success == true)
        {
            status = RemoteCommandStatus.Executed;
        }
        else
        {
            status = RemoteCommandStatus.Failed;
        }

        await _remoteCommandRepository.UpdateRemoteCommandStatusAsync(
            request.CommandId,
            machineId,
            status,
            result?.ExitCode,
            result?.Stdout,
            result?.Stderr,
            result?.Message,
            context.CancellationToken);

        return new AcknowledgeCommandResponse { Success = true };
    }

    /// <summary>
    /// Records a machine type reported by the agent when it differs from what is stored. Failures
    /// are swallowed: this is an opportunistic correction and must never fail a configuration fetch.
    /// </summary>
    private async Task ApplyReportedMachineTypeAsync(long machineId, Framlux.FleetManagement.Grpc.AgentRegistration.MachineType reported, CancellationToken cancellationToken)
    {
        MachineTypes? resolved = ResolveReportedMachineType(reported);
        if (resolved is null)
        {
            return;
        }

        try
        {
            await _machineRepository.UpdateMachineTypeAsync(machineId, resolved.Value, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply reported machine type for machine {MachineId}", machineId);
        }
    }

    /// <summary>
    /// Maps a reported machine type onto the stored enum, or null when it should be ignored.
    /// <para>
    /// UnknownType means the agent could not classify the host, which is not the same as asserting
    /// the host is unknown — treating it as an assertion would let a detection failure erase a good
    /// stored value. An out-of-range value is rejected for the same reason a registration would
    /// reject it: an agent must not be able to write an enum the rest of the system cannot read.
    /// </para>
    /// </summary>
    /// <param name="reported">The machine type as sent by the agent.</param>
    /// <returns>The database enum value to store, or null to leave the stored type alone.</returns>
    internal static MachineTypes? ResolveReportedMachineType(Framlux.FleetManagement.Grpc.AgentRegistration.MachineType reported)
    {
        return reported switch
        {
            Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.DesktopType => MachineTypes.Desktop,
            Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.LaptopType => MachineTypes.Laptop,
            Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.BareMetalServerType => MachineTypes.BareMetalServer,
            Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.VirtualMachineType => MachineTypes.VirtualMachine,
            _ => null,
        };
    }

    /// <summary>
    /// Resolves the machine identity for a configuration RPC from the authenticated API-key claim,
    /// never from the client-supplied request value. A missing or unparseable claim is rejected as
    /// <see cref="StatusCode.Unauthenticated"/> rather than silently falling back to the request, so
    /// no code path can ever act on a machine id the caller was not authenticated for. When the
    /// request carries its own machine id it must match the claim, otherwise the call is
    /// <see cref="StatusCode.PermissionDenied"/>.
    /// </summary>
    /// <param name="context">The gRPC call context carrying the authenticated principal.</param>
    /// <param name="requestMachineId">The machine id supplied in the request body.</param>
    /// <param name="operation">The RPC name, used only for diagnostic logging.</param>
    /// <returns>The authenticated machine id.</returns>
    private long ResolveMachineId(ServerCallContext context, long requestMachineId, string operation)
    {
        long claimMachineId = ExtractMachineIdFromClaims(context);
        if (claimMachineId <= 0)
        {
            _logger.LogWarning("{Operation}: request rejected because the authenticated principal carries no machine claim", operation);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Could not determine machine identity"));
        }

        if (requestMachineId != claimMachineId)
        {
            _logger.LogWarning("{Operation}: request MachineId={RequestId} does not match authenticated MachineId={ClaimId}",
                operation, requestMachineId, claimMachineId);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Machine ID mismatch"));
        }

        return claimMachineId;
    }

    private static long ExtractMachineIdFromClaims(ServerCallContext context)
    {
        Claim? machineIdClaim = context.GetHttpContext().User.FindFirst("MachineId");
        if (machineIdClaim is not null && long.TryParse(machineIdClaim.Value, out long machineId))
        {
            return machineId;
        }

        return 0;
    }

    private static int ExtractTenantIdFromClaims(ServerCallContext context)
    {
        Claim? tenantIdClaim = context.GetHttpContext().User.FindFirst("TenantId");
        if (tenantIdClaim is not null && int.TryParse(tenantIdClaim.Value, out int tenantId))
        {
            return tenantId;
        }

        return 0;
    }
}
