// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Grpc.AgentRegistration;
using Framlux.FleetManagement.Services.Core.Billing;
using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Service for managing machine registration, activation, and lifecycle operations.
/// Uses Redis for API key delivery cache to work across Kubernetes replicas.
/// </summary>
public sealed class MachineService : IMachineService
{
    // 1-hour TTL: the agent picks up the plaintext key once and persists it locally; if the
    // agent hasn't claimed it within an hour, the row should regenerate rather than carry
    // sensitive material in Redis indefinitely.
    private static readonly TimeSpan ApiKeyCacheTtl = TimeSpan.FromHours(1);
    private const string PendingApiKeyProtectorPurpose = "MachineService.PendingApiKey";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<MachineService> _logger;
    private readonly IConnectionMultiplexer _redis;
    private readonly IBillingApiClient _billingApiClient;
    private readonly IDataProtector _pendingApiKeyProtector;

    /// <summary>
    /// Initializes a new instance of the <see cref="MachineService"/> class.
    /// </summary>
    /// <param name="scopeFactory">Factory for creating service scopes for database operations.</param>
    /// <param name="logger">The logger instance for diagnostic logging.</param>
    /// <param name="redis">Redis connection for cross-replica API key delivery cache.</param>
    /// <param name="billingApiClient">Client for communicating billing updates to Stripe.</param>
    /// <param name="dataProtectionProvider">Data protection provider for encrypting pending API keys at rest in Redis.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required parameter is null.</exception>
    public MachineService(
        IServiceScopeFactory scopeFactory,
        ILogger<MachineService> logger,
        IConnectionMultiplexer redis,
        IBillingApiClient billingApiClient,
        IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(billingApiClient);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _serviceScopeFactory = scopeFactory;
        _logger = logger;
        _redis = redis;
        _billingApiClient = billingApiClient;
        _pendingApiKeyProtector = dataProtectionProvider.CreateProtector(PendingApiKeyProtectorPurpose);
    }

    /// <inheritdoc/>
    public async Task<(RegistrationStatus status, long? id, string? apiKey)> GetRegistrationStatusAsync(string serialNumber, string systemId, string registrationToken, bool needsApiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(registrationToken))
        {
            return (RegistrationStatus.UnknownRegistration, null, null);
        }

        string tokenHash = ComputeSha256Hash(registrationToken);

        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        IRegistrationTokenRepository tokenRepo = scope.ServiceProvider.GetRequiredService<IRegistrationTokenRepository>();
        IMachineRepository machineRepo = scope.ServiceProvider.GetRequiredService<IMachineRepository>();

        // Validate the registration token exists
        RegistrationToken? token = await tokenRepo.GetTokenByHashAsync(tokenHash, cancellationToken);

        if (token is null)
        {
            return (RegistrationStatus.UnknownRegistration, null, null);
        }

        if (token.IsRevoked)
        {
            _logger.LogWarning("GetRegistrationStatus: token {TokenId} is revoked", token.Id);

            return (RegistrationStatus.UnknownRegistration, null, null);
        }

        if (IsTokenExpired(token, DateTimeOffset.UtcNow))
        {
            _logger.LogWarning("GetRegistrationStatus: token {TokenId} is expired", token.Id);

            return (RegistrationStatus.UnknownRegistration, null, null);
        }

        // Normalize to match how data is stored (lowercase).
        string normalizedSerial = serialNumber.ToLowerInvariant();
        string normalizedSystemId = systemId.ToLowerInvariant();

        // Look up the machine by serial number and system ID within the token's tenant
        Machine? machine = await machineRepo.GetMachineBySerialAndSystemIdAsync(normalizedSerial, normalizedSystemId, token.TenantId, cancellationToken);

        if (machine is null)
        {
            return (RegistrationStatus.UnknownRegistration, null, null);
        }

        // If the caller does not need an API key, return status only.
        if (needsApiKey == false)
        {
            return (RegistrationStatus.RegistrationActive, machine.Id, null);
        }

        // Try to deliver the cached plaintext key first (one-time delivery from initial registration).
        // Cache value is encrypted at rest via IDataProtector so Redis snapshots/MONITOR cannot
        // extract live API keys.
        IDatabase redisDb = _redis.GetDatabase();
        string cacheKey = $"pending_api_key:{machine.Id}";
        string? plaintextKey = null;
        RedisValue cachedKey = await redisDb.StringGetAsync(cacheKey);

        if (cachedKey.HasValue)
        {
            // Delete from cache first — safe direction: if MarkKeyDelivered fails afterward,
            // the reissue path below will recover. The reverse order risked stale cache entries
            // allowing duplicate delivery if KeyDeleteAsync failed after a successful DB update.
            await redisDb.KeyDeleteAsync(cacheKey);

            int updated = await machineRepo.MarkKeyDeliveredAsync(machine.Id, cancellationToken);

            if (updated > 0)
            {
                string? decrypted = TryUnprotectPendingKey(cachedKey.ToString()!, machine.Id);
                if (decrypted is not null)
                {
                    plaintextKey = decrypted;
                    _logger.LogInformation("API key delivered to machine {MachineId}", machine.Id);
                }
                else
                {
                    _logger.LogWarning("Cached API key for machine {MachineId} could not be decrypted; falling through to reissue", machine.Id);
                }
            }
            else
            {
                _logger.LogWarning("Concurrent API key delivery attempt for machine {MachineId}", machine.Id);
            }
        }

        // If no cached key was available, re-issue a new one.
        if (plaintextKey is null)
        {
            plaintextKey = await machineRepo.ReissueApiKeyAsync(machine.Id, cancellationToken);

            if (plaintextKey is not null)
            {
                // Cache the encrypted form for retry resilience and mark as delivered.
                string protectedKey = _pendingApiKeyProtector.Protect(plaintextKey);
                await redisDb.StringSetAsync(cacheKey, protectedKey, ApiKeyCacheTtl);
                await machineRepo.SetKeyDeliveredAsync(machine.Id, cancellationToken);
                _logger.LogInformation("API key re-issued for machine {MachineId} in tenant {TenantId}", machine.Id, token.TenantId);
            }
            else
            {
                _logger.LogError("Failed to re-issue API key for machine {MachineId} — machine is active but has no credentials", machine.Id);
            }
        }

        return (RegistrationStatus.RegistrationActive, machine.Id, plaintextKey);
    }

    /// <summary>
    /// Decrypts a pending API key value pulled from Redis. Returns <c>null</c> on decryption
    /// failure rather than throwing — the caller will fall through to the reissue path which is
    /// the recovery mechanism for corrupted/rotated key material.
    /// </summary>
    private string? TryUnprotectPendingKey(string protectedValue, long machineId)
    {
        try
        {
            return _pendingApiKeyProtector.Unprotect(protectedValue);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(
                ex,
                "Pending API key for machine {MachineId} failed Unprotect; will reissue",
                machineId);

            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<(long? machineId, string? apiKey, string errorMessage)> RegisterSystemAsync(RegisterSystemRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.RegistrationToken))
        {
            return (null, null, "Registration token is required");
        }

        // Validate the registration token
        string tokenHash = ComputeSha256Hash(request.RegistrationToken);

        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        IRegistrationTokenRepository tokenRepo = scope.ServiceProvider.GetRequiredService<IRegistrationTokenRepository>();

        RegistrationToken? token = await tokenRepo.GetTokenByHashAsync(tokenHash, cancellationToken);

        if (token is null)
        {
            _logger.LogWarning("Registration attempt with invalid token hash");

            return (null, null, "Invalid registration token");
        }

        if (token.IsRevoked)
        {
            _logger.LogWarning("Registration attempt with revoked token {TokenId}", token.Id);

            return (null, null, "Registration token has been revoked");
        }

        if (IsTokenExpired(token, DateTimeOffset.UtcNow))
        {
            _logger.LogWarning("Registration attempt with expired token {TokenId}", token.Id);

            return (null, null, "Registration token has expired");
        }

        IMachineRepository machineRepository = scope.ServiceProvider.GetRequiredService<IMachineRepository>();

        // Normalize case-sensitive fields to lowercase for consistent index usage.
        string normalizedSerial = request.SerialNumber.ToLowerInvariant();
        string normalizedSystemId = request.SystemId.ToLowerInvariant();

        // Check if we have a machine already with these IDs
        bool machineExists = await machineRepository.DoesMachineExistAsync(normalizedSerial, normalizedSystemId, request.AssetTag ?? string.Empty, token.TenantId, cancellationToken);
        if (machineExists)
        {
            return (null, null, "Machine already exists");
        }

        // Check subscription machine limit from tier defaults + overrides
        ISubscriptionService subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
        TenantSubscription? subscription = await subscriptionService.GetSubscriptionForTenantAsync(token.TenantId, cancellationToken);
        EffectiveLimits effectiveLimits = await subscriptionService.GetEffectiveLimitsForTenantAsync(token.TenantId, cancellationToken);
        int? machineLimit = effectiveLimits.MachineLimit;

        _logger.LogInformation("Creating Machine for {SerialNumber} with token {TokenId}", request.SerialNumber, token.Id);
        Machine machine = new()
        {
            ApiKeyHash = string.Empty, // Will be set by CreateMachineWithKeyAsync
            Name = request.Hostname,
            SerialNumber = normalizedSerial,
            SystemId = normalizedSystemId,
            AssetTagNumber = request.AssetTag,
            MachineType = ConvertRpcMachineTypeToDatabaseMachineType(request.MachineType),
            OperatingSystem = ConvertRpcOsTypeToDatabaseOsType(request.Os),
            RegistrationTokenId = token.Id,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = token.TenantId,
        };

        (Machine? createdMachine, string? plaintextApiKey) = await machineRepository.CreateMachineWithKeyAsync(machine, machineLimit, cancellationToken);

        if (createdMachine is null)
        {
            _logger.LogWarning("Machine limit exceeded for tenant {TenantId}", token.TenantId);

            return (null, null, "Machine limit exceeded");
        }

        // Pre-create summary and detail rows so all subsequent telemetry writes are pure UPDATEs.
        IMachineStateRepository machineStateRepo = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();
        await machineStateRepo.InsertSummaryAsync(new MachineStateSummary
        {
            MachineId = createdMachine.Id,
            TenantId = token.TenantId,
            Name = machine.Name,
            OperatingSystem = (byte)machine.OperatingSystem,
            MachineType = (byte)machine.MachineType,
            HealthStatus = 0,
        }, cancellationToken);

        await machineStateRepo.InsertDetailAsync(new MachineStateDetail
        {
            MachineId = createdMachine.Id,
        }, cancellationToken);

        // Cache the encrypted key in Redis for recovery via GetRegistrationStatus.
        // IDataProtector ensures Redis snapshots/MONITOR sessions cannot extract live API keys.
        IDatabase redisDb = _redis.GetDatabase();
        string cacheKey = $"pending_api_key:{createdMachine.Id}";
        string protectedApiKey = _pendingApiKeyProtector.Protect(plaintextApiKey!);
        await redisDb.StringSetAsync(cacheKey, protectedApiKey, ApiKeyCacheTtl);

        _logger.LogInformation("Machine created with ID {MachineId} for {SerialNumber}", createdMachine.Id, request.SerialNumber);

        // Report usage to billing for metered billing (best effort, hourly heartbeat provides the safety net)
        try
        {
            // Only report usage for paid tiers; Free tier has no Stripe subscription
            if ((subscription is not null) && (subscription.Tier != SubscriptionTier.Free))
            {
                ITenantRepository tenantRepository = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                Tenant? tenant = await tenantRepository.GetTenantByIdAsync(token.TenantId, cancellationToken);
                if (tenant is not null)
                {
                    int machineCount = await machineRepository.GetActiveMachineCountAsync(token.TenantId, cancellationToken);
                    await _billingApiClient.ReportMachineUsageAsync(tenant.ExternalId, machineCount, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report machine usage to billing for tenant {TenantId}", token.TenantId);
        }

        return (createdMachine.Id, plaintextApiKey, string.Empty);
    }

    /// <summary>
    /// Determines whether a registration token has expired as of the supplied instant.
    /// A token is expired when the current time is at or past its expiry timestamp.
    /// </summary>
    /// <param name="token">The registration token to evaluate.</param>
    /// <param name="now">The instant to compare against, in UTC.</param>
    /// <returns><c>true</c> if the token has expired; otherwise <c>false</c>.</returns>
    internal static bool IsTokenExpired(RegistrationToken token, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(token);

        return token.ExpiresAt <= now;
    }

    private static string ComputeSha256Hash(string input)
    {
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        return Convert.ToHexStringLower(hashBytes);
    }

    private static MachineTypes ConvertRpcMachineTypeToDatabaseMachineType(Framlux.FleetManagement.Grpc.AgentRegistration.MachineType input)
        => input switch
        {
            Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.BareMetalServerType => MachineTypes.BareMetalServer,
            Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.DesktopType => MachineTypes.Desktop,
            Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.LaptopType => MachineTypes.Laptop,
            Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.UnknownType => MachineTypes.Unknown,
            Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.VirtualMachineType => MachineTypes.VirtualMachine,
            _ => MachineTypes.Unknown,
        };

    private static OperatingSystems ConvertRpcOsTypeToDatabaseOsType(Framlux.FleetManagement.Grpc.AgentRegistration.OperatingSystemType input)
        => input switch
        {
            Framlux.FleetManagement.Grpc.AgentRegistration.OperatingSystemType.DebianOs => OperatingSystems.Debian,
            Framlux.FleetManagement.Grpc.AgentRegistration.OperatingSystemType.FedoraOs => OperatingSystems.Fedora,
            Framlux.FleetManagement.Grpc.AgentRegistration.OperatingSystemType.MacOs => OperatingSystems.MacOS,
            Framlux.FleetManagement.Grpc.AgentRegistration.OperatingSystemType.RedhatOs => OperatingSystems.RedHat,
            Framlux.FleetManagement.Grpc.AgentRegistration.OperatingSystemType.UbuntuOs => OperatingSystems.Ubuntu,
            Framlux.FleetManagement.Grpc.AgentRegistration.OperatingSystemType.UnknownOs => OperatingSystems.Unknown,
            Framlux.FleetManagement.Grpc.AgentRegistration.OperatingSystemType.WindowsOs => OperatingSystems.Windows,
            _ => OperatingSystems.Unknown,
        };
}
