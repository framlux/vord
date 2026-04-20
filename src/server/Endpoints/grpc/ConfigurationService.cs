// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using System.Text.Json;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Grpc.AgentConfiguration;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
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
    private readonly IDatabaseCache _cache;
    private readonly IMachinePingService _pingService;
    private readonly ServerConfigurationService _configService;
    private readonly ILogger<ConfigurationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationService"/> class.
    /// </summary>
    /// <param name="cache">The database caching layer instance</param>
    /// <param name="pingService">The machine ping tracking service</param>
    /// <param name="configService">The server configuration service for runtime settings</param>
    /// <param name="logger">The application-wide logging service instance</param>
    /// <exception cref="ArgumentNullException"></exception>
    public ConfigurationService(IDatabaseCache cache, IMachinePingService pingService, ServerConfigurationService configService, ILogger<ConfigurationService> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _pingService = pingService ?? throw new ArgumentNullException(nameof(pingService));
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
        long claimMachineId = ExtractMachineIdFromClaims(context);
        if ((claimMachineId > 0) && (request.MachineId != claimMachineId))
        {
            _logger.LogWarning("GetConfiguration: request MachineId={RequestId} does not match authenticated MachineId={ClaimId}",
                request.MachineId, claimMachineId);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Machine ID mismatch"));
        }

        int heartbeatSeconds = await _configService.GetAgentHeartbeatSecondsAsync(context.CancellationToken);
        int configRefreshSeconds = await _configService.GetAgentConfigRefreshSecondsAsync(context.CancellationToken);
        int commandPollSeconds = await _configService.GetAgentCommandPollSecondsAsync(context.CancellationToken);
        int telemetryCollectFastSeconds = await _configService.GetTelemetryCollectFastSecondsAsync(context.CancellationToken);
        int telemetryCollectSlowSeconds = await _configService.GetTelemetryCollectSlowSecondsAsync(context.CancellationToken);
        int telemetrySendFastSeconds = await _configService.GetTelemetrySendFastSecondsAsync(context.CancellationToken);
        int telemetrySendSlowSeconds = await _configService.GetTelemetrySendSlowSecondsAsync(context.CancellationToken);

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
            },
            TenantId = tenantId,
        };
        // Store the agent's reported capabilities so the UI can reflect them.
        long machineId = claimMachineId > 0 ? claimMachineId : request.MachineId;

        if (tenantId > 0)
        {
            List<UserSigningKey> signingKeys = await _cache.GetActiveSigningKeysForMachineAsync(machineId, context.CancellationToken);
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
        long claimMachineId = ExtractMachineIdFromClaims(context);
        if ((claimMachineId > 0) && (request.MachineId != claimMachineId))
        {
            _logger.LogWarning("AgentPing: request MachineId={RequestId} does not match authenticated MachineId={ClaimId}",
                request.MachineId, claimMachineId);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Machine ID mismatch"));
        }

        long machineId = claimMachineId > 0 ? claimMachineId : request.MachineId;
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
        long claimMachineId = ExtractMachineIdFromClaims(context);
        if ((claimMachineId > 0) && (request.MachineId != claimMachineId))
        {
            _logger.LogWarning("GetPendingCommands: request MachineId={RequestId} does not match authenticated MachineId={ClaimId}",
                request.MachineId, claimMachineId);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Machine ID mismatch"));
        }

        long machineId = claimMachineId > 0 ? claimMachineId : request.MachineId;
        int tenantId = ExtractTenantIdFromClaims(context);

        List<RemoteCommand> pendingCommands = await _cache.GetPendingCommandsForMachineAsync(machineId, tenantId, context.CancellationToken);

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
            await _cache.MarkCommandsDeliveredAsync(deliveredIds, context.CancellationToken);
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
        long claimMachineId = ExtractMachineIdFromClaims(context);
        if ((claimMachineId > 0) && (request.MachineId != claimMachineId))
        {
            _logger.LogWarning("AcknowledgeCommand: request MachineId={RequestId} does not match authenticated MachineId={ClaimId}",
                request.MachineId, claimMachineId);
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Machine ID mismatch"));
        }

        long machineId = claimMachineId > 0 ? claimMachineId : request.MachineId;

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

        await _cache.UpdateRemoteCommandStatusAsync(
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
