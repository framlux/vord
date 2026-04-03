// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Grpc.AgentRegistration;
using Framlux.FleetManagement.Server.Services.Billing;
using LinqToDB;
using LinqToDB.Async;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Service for managing machine registration, activation, and lifecycle operations.
/// Uses Redis for API key delivery cache to work across Kubernetes replicas.
/// </summary>
public sealed class MachineService : IMachineService
{
    private static readonly TimeSpan ApiKeyCacheTtl = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<MachineService> _logger;
    private readonly IConnectionMultiplexer _redis;
    private readonly IBillingApiClient _billingApiClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="MachineService"/> class.
    /// </summary>
    /// <param name="scopeFactory">Factory for creating service scopes for database operations.</param>
    /// <param name="logger">The logger instance for diagnostic logging.</param>
    /// <param name="redis">Redis connection for cross-replica API key delivery cache.</param>
    /// <param name="billingApiClient">Client for communicating billing updates to Stripe.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required parameter is null.</exception>
    public MachineService(IServiceScopeFactory scopeFactory, ILogger<MachineService> logger, IConnectionMultiplexer redis, IBillingApiClient billingApiClient)
    {
        _serviceScopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _billingApiClient = billingApiClient ?? throw new ArgumentNullException(nameof(billingApiClient));
    }

    /// <inheritdoc/>
    public async Task<(RegistrationStatus status, long? id, string? apiKey)> GetRegistrationStatusAsync(string serialNumber, string systemId, string registrationToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(registrationToken))
        {
            return (RegistrationStatus.UnknownRegistration, null, null);
        }

        string tokenHash = ComputeSha256Hash(registrationToken);

        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        // Validate the registration token exists
        RegistrationToken? token = await db.RegistrationTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        if (token is null)
        {
            return (RegistrationStatus.UnknownRegistration, null, null);
        }

        // Normalize to match how data is stored (lowercase).
        string normalizedSerial = serialNumber.ToLowerInvariant();
        string normalizedSystemId = systemId.ToLowerInvariant();

        // Look up the machine by serial number and system ID within the token's tenant
        Machine? machine = await db.Machines
            .FirstOrDefaultAsync(m => m.SerialNumber == normalizedSerial &&
                                      m.SystemId == normalizedSystemId &&
                                      m.TenantId == token.TenantId &&
                                      m.IsDeleted == false, cancellationToken);

        if (machine is null)
        {
            return (RegistrationStatus.UnknownRegistration, null, null);
        }

        // One-time key delivery: use atomic DB update to prevent concurrent delivery
        IDatabase redisDb = _redis.GetDatabase();
        string cacheKey = $"pending_api_key:{machine.Id}";
        string? plaintextKey = null;
        RedisValue cachedKey = await redisDb.StringGetAsync(cacheKey);

        if (cachedKey.HasValue)
        {
            // Atomic update: only deliver key if it hasn't been delivered yet
            int updated = await db.Machines
                .Where(m => m.Id == machine.Id && m.KeyDeliveredAt == null)
                .Set(m => m.KeyDeliveredAt, DateTimeOffset.UtcNow)
                .UpdateAsync(cancellationToken);

            if (updated > 0)
            {
                plaintextKey = cachedKey.ToString();
                await redisDb.KeyDeleteAsync(cacheKey);
                _logger.LogInformation("API key delivered to machine {MachineId}", machine.Id);
            }
            else
            {
                _logger.LogWarning("Concurrent API key delivery attempt for machine {MachineId}", machine.Id);
            }
        }

        return (RegistrationStatus.RegistrationActive, machine.Id, plaintextKey);
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
        DatabaseContext dbContext = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        RegistrationToken? token = await dbContext.RegistrationTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

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

        IDatabaseCache db = scope.ServiceProvider.GetRequiredService<IDatabaseCache>();

        // Normalize case-sensitive fields to lowercase for consistent index usage.
        string normalizedSerial = request.SerialNumber.ToLowerInvariant();
        string normalizedSystemId = request.SystemId.ToLowerInvariant();

        // Check if we have a machine already with these IDs
        bool machineExists = await db.DoesMachineExistAsync(normalizedSerial, normalizedSystemId, request.AssetTag ?? string.Empty, token.TenantId, cancellationToken);
        if (machineExists)
        {
            return (null, null, "Machine already exists");
        }

        // Check subscription machine limit
        TenantSubscription? subscription = await dbContext.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == token.TenantId, cancellationToken);
        int? machineLimit = subscription?.MachineLimit;

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

        (Machine? createdMachine, string? plaintextApiKey) = await db.CreateMachineWithKeyAsync(machine, machineLimit, cancellationToken);

        if (createdMachine is null)
        {
            _logger.LogWarning("Machine limit exceeded for tenant {TenantId}", token.TenantId);

            return (null, null, "Machine limit exceeded");
        }

        // Cache the plaintext key in Redis for recovery via GetRegistrationStatus
        IDatabase redisDb = _redis.GetDatabase();
        string cacheKey = $"pending_api_key:{createdMachine.Id}";
        await redisDb.StringSetAsync(cacheKey, plaintextApiKey, ApiKeyCacheTtl);

        _logger.LogInformation("Machine created with ID {MachineId} for {SerialNumber}", createdMachine.Id, request.SerialNumber);

        // Sync the updated machine count to Stripe (best effort, StripeSyncService provides the safety net)
        try
        {
            Tenant? tenant = await db.GetTenantByIdAsync(token.TenantId, cancellationToken);
            if (tenant is not null)
            {
                int machineCount = await dbContext.Machines
                    .Where(m => m.TenantId == token.TenantId && m.IsDeleted == false)
                    .CountAsync(cancellationToken);
                await _billingApiClient.UpdateQuantityAsync(tenant.ExternalId, machineCount, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync machine count to billing for tenant {TenantId}", token.TenantId);
        }

        return (createdMachine.Id, plaintextApiKey, string.Empty);
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
            Framlux.FleetManagement.Grpc.AgentRegistration.OperatingSystemType.FedoraOs => OperatingSystems.Fedora,
            Framlux.FleetManagement.Grpc.AgentRegistration.OperatingSystemType.MacOs => OperatingSystems.MacOS,
            Framlux.FleetManagement.Grpc.AgentRegistration.OperatingSystemType.RedhatOs => OperatingSystems.RedHat,
            Framlux.FleetManagement.Grpc.AgentRegistration.OperatingSystemType.UbuntuOs => OperatingSystems.Ubuntu,
            Framlux.FleetManagement.Grpc.AgentRegistration.OperatingSystemType.UnknownOs => OperatingSystems.Unknown,
            Framlux.FleetManagement.Grpc.AgentRegistration.OperatingSystemType.WindowsOs => OperatingSystems.Windows,
            _ => OperatingSystems.Unknown,
        };
}
