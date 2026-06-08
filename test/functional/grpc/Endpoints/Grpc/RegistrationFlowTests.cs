// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.FleetManagement.Grpc.AgentConfiguration;
using Framlux.FleetManagement.Grpc.AgentRegistration;
using Grpc.Core;
using Grpc.Net.Client;
using LinqToDB.Async;
using LinqToDB;
using System.Security.Cryptography;
using System.Text;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Grpc;

/// <summary>
/// Functional tests for the machine registration gRPC flow.
/// These tests exercise the full pipeline: gRPC request → RegistrationService → MachineService → database.
/// </summary>
public sealed class RegistrationFlowTests
{
    [Test]
    public async Task RegisterSystem_ValidToken_ReturnsMachineIdAndApiKey()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long tokenId) = await SeedTenantWithToken(db);
        await SeedActiveSubscription(db, tenantId);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        RegisterSystemRequest request = new()
        {
            Hostname = "test-host-1",
            SerialNumber = "sn-register-001",
            SystemId = "sys-register-001",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };

        // Act
        RegisterSystemResponse response = await client.RegisterSystemAsync(request);

        // Assert
        await Assert.That(response.MachineId).IsGreaterThan(0);
        await Assert.That(response.ApiKey).IsNotEmpty();
        await Assert.That(response.ErrorMessage).IsEmpty();

        // Verify machine was persisted
        Machine? machine = await db.Machines
            .FirstOrDefaultAsync(m => m.Id == response.MachineId);
        await Assert.That(machine).IsNotNull();
        await Assert.That(machine!.SerialNumber).IsEqualTo("sn-register-001");
    }

    [Test]
    public async Task RegisterSystem_InvalidToken_ReturnsError()
    {
        // Arrange
        using FunctionalTestFactory factory = new();

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        RegisterSystemRequest request = new()
        {
            Hostname = "test-host-2",
            SerialNumber = "sn-invalid-001",
            SystemId = "sys-invalid-001",
            RegistrationToken = "completely-invalid-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };

        // Act & Assert — should throw RpcException with InvalidArgument
        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await client.RegisterSystemAsync(request));
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(ex.Status.Detail).IsNotEmpty();
    }

    [Test]
    public async Task RegisterSystem_RevokedToken_ReturnsError()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenant(db);
        await SeedActiveSubscription(db, tenantId);

        string tokenPlaintext = "revoked-token-value";
        string tokenHash = ComputeHash(tokenPlaintext);
        RegistrationToken revokedToken = new()
        {
            TenantId = tenantId,
            TokenHash = tokenHash,
            Name = "Revoked Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = true,
            RevokedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };
        await db.InsertAsync(revokedToken);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        RegisterSystemRequest request = new()
        {
            Hostname = "test-host-4",
            SerialNumber = "sn-revoked-001",
            SystemId = "sys-revoked-001",
            RegistrationToken = tokenPlaintext,
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };

        // Act & Assert — should throw RpcException for revoked token
        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await client.RegisterSystemAsync(request));
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(ex.Status.Detail).Contains("revoked");
    }

    [Test]
    public async Task RegisterSystem_MachineLimitExceeded_ReturnsError()
    {
        // Arrange — the Free tier allows 3 machines (defined in TierFeatureLimits seed data)
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long tokenId) = await SeedTenantWithToken(db);

        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(subscription);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        // Register 3 machines to fill the Free tier limit
        for (int i = 1; i <= 3; i++)
        {
            RegisterSystemRequest fillRequest = new()
            {
                Hostname = $"fill-host-{i}",
                SerialNumber = $"sn-limit-{i:D3}",
                SystemId = $"sys-limit-{i:D3}",
                RegistrationToken = "test-registration-token",
                MachineType = MachineType.BareMetalServerType,
                Os = OperatingSystemType.UbuntuOs
            };
            RegisterSystemResponse fillResponse = await client.RegisterSystemAsync(fillRequest);
            await Assert.That(fillResponse.MachineId).IsGreaterThan(0);
        }

        // Act — register a 4th machine (should fail because limit is 3)
        RegisterSystemRequest overLimitRequest = new()
        {
            Hostname = "over-limit-host",
            SerialNumber = "sn-limit-004",
            SystemId = "sys-limit-004",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };

        // Assert — should throw RpcException for limit exceeded
        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await client.RegisterSystemAsync(overLimitRequest));
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(ex.Status.Detail).Contains("limit");
    }

    [Test]
    public async Task RegisterSystem_MissingHostname_ReturnsInvalidArgument()
    {
        // Arrange
        using FunctionalTestFactory factory = new();

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        RegisterSystemRequest request = new()
        {
            Hostname = "",
            SerialNumber = "sn-missing-001",
            SystemId = "sys-missing-001",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };

        // Act & Assert — should throw RpcException for missing hostname
        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await client.RegisterSystemAsync(request));
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(ex.Status.Detail).Contains("Hostname");
    }

    [Test]
    public async Task GetRegistrationStatus_UnknownMachine_ReturnsUnknown()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        await SeedTenantWithToken(db);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        SystemRegistrationStatusRequest request = new()
        {
            SerialNumber = "unknown-serial",
            SystemId = "unknown-system",
            RegistrationToken = "test-registration-token"
        };

        // Act
        SystemRegistrationStatusResponse response = await client.GetRegistrationStatusAsync(request);

        // Assert
        await Assert.That(response.Status).IsEqualTo(RegistrationStatus.UnknownRegistration);
    }

    [Test]
    public async Task GetRegistrationStatus_RegisteredMachine_ReturnsActive()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long tokenId) = await SeedTenantWithToken(db);
        await SeedActiveSubscription(db, tenantId);

        // Register a machine first
        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        RegisterSystemRequest registerRequest = new()
        {
            Hostname = "status-host",
            SerialNumber = "sn-status-001",
            SystemId = "sys-status-001",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };
        RegisterSystemResponse registerResponse = await client.RegisterSystemAsync(registerRequest);
        await Assert.That(registerResponse.MachineId).IsGreaterThan(0);

        // Act — check registration status
        SystemRegistrationStatusRequest statusRequest = new()
        {
            SerialNumber = "sn-status-001",
            SystemId = "sys-status-001",
            RegistrationToken = "test-registration-token"
        };
        SystemRegistrationStatusResponse statusResponse = await client.GetRegistrationStatusAsync(statusRequest);

        // Assert
        await Assert.That(statusResponse.Status).IsEqualTo(RegistrationStatus.RegistrationActive);
        await Assert.That(statusResponse.MachineId).IsEqualTo(registerResponse.MachineId);
    }

    [Test]
    public async Task GetRegistrationStatus_NeedsApiKey_CacheExpired_ReissuesNewKey()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long tokenId) = await SeedTenantWithToken(db);
        await SeedActiveSubscription(db, tenantId);

        // Register a machine first
        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        RegisterSystemRequest registerRequest = new()
        {
            Hostname = "reissue-host",
            SerialNumber = "sn-reissue-001",
            SystemId = "sys-reissue-001",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };
        RegisterSystemResponse registerResponse = await client.RegisterSystemAsync(registerRequest);
        await Assert.That(registerResponse.MachineId).IsGreaterThan(0);

        // Request re-issuance
        SystemRegistrationStatusRequest statusRequest = new()
        {
            SerialNumber = "sn-reissue-001",
            SystemId = "sys-reissue-001",
            RegistrationToken = "test-registration-token",
            NeedsApiKey = true
        };
        SystemRegistrationStatusResponse statusResponse = await client.GetRegistrationStatusAsync(statusRequest);

        // Assert — should get a new key
        await Assert.That(statusResponse.Status).IsEqualTo(RegistrationStatus.RegistrationActive);
        await Assert.That(statusResponse.MachineId).IsEqualTo(registerResponse.MachineId);
        await Assert.That(statusResponse.ApiKey).IsNotEmpty();
    }

    [Test]
    public async Task GetRegistrationStatus_NeedsApiKeyFalse_DoesNotReturnKey()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long tokenId) = await SeedTenantWithToken(db);
        await SeedActiveSubscription(db, tenantId);

        // Register a machine first
        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        RegisterSystemRequest registerRequest = new()
        {
            Hostname = "nokey-host",
            SerialNumber = "sn-nokey-001",
            SystemId = "sys-nokey-001",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };
        RegisterSystemResponse registerResponse = await client.RegisterSystemAsync(registerRequest);
        await Assert.That(registerResponse.MachineId).IsGreaterThan(0);

        // Act — status check without needing a key
        SystemRegistrationStatusRequest statusRequest = new()
        {
            SerialNumber = "sn-nokey-001",
            SystemId = "sys-nokey-001",
            RegistrationToken = "test-registration-token",
            NeedsApiKey = false
        };
        SystemRegistrationStatusResponse statusResponse = await client.GetRegistrationStatusAsync(statusRequest);

        // Assert — status active but no key returned
        await Assert.That(statusResponse.Status).IsEqualTo(RegistrationStatus.RegistrationActive);
        await Assert.That(statusResponse.MachineId).IsEqualTo(registerResponse.MachineId);
        await Assert.That(statusResponse.ApiKey).IsEmpty();
    }

    [Test]
    public async Task GetRegistrationStatus_ReissuedApiKey_AuthenticatesOnSubsequentGrpcCall()
    {
        // Arrange — register a machine, drain the cached initial key, then trigger a true
        // re-issuance and verify the newly generated key authenticates for API-key-protected calls
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long tokenId) = await SeedTenantWithToken(db);
        await SeedActiveSubscription(db, tenantId);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient registrationClient = new(channel);

        RegisterSystemRequest registerRequest = new()
        {
            Hostname = "reissue-auth-host",
            SerialNumber = "sn-reissue-auth-001",
            SystemId = "sys-reissue-auth-001",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };
        RegisterSystemResponse registerResponse = await registrationClient.RegisterSystemAsync(registerRequest);
        await Assert.That(registerResponse.MachineId).IsGreaterThan(0);

        // First NeedsApiKey call delivers the cached original key and clears the Redis entry
        SystemRegistrationStatusRequest drainCacheRequest = new()
        {
            SerialNumber = "sn-reissue-auth-001",
            SystemId = "sys-reissue-auth-001",
            RegistrationToken = "test-registration-token",
            NeedsApiKey = true
        };
        await registrationClient.GetRegistrationStatusAsync(drainCacheRequest);

        // Act — second NeedsApiKey call triggers a true re-issuance with a new key and hash
        SystemRegistrationStatusRequest reissueRequest = new()
        {
            SerialNumber = "sn-reissue-auth-001",
            SystemId = "sys-reissue-auth-001",
            RegistrationToken = "test-registration-token",
            NeedsApiKey = true
        };
        SystemRegistrationStatusResponse reissueResponse = await registrationClient.GetRegistrationStatusAsync(reissueRequest);
        await Assert.That(reissueResponse.ApiKey).IsNotEmpty();

        string reissuedApiKey = reissueResponse.ApiKey;

        // Assert — the re-issued key authenticates for an API-key-protected gRPC call
        Configuration.ConfigurationClient configClient = new(channel);
        Metadata headers = new() { { "x-api-key", reissuedApiKey } };
        GetConfigurationRequest configRequest = new() { MachineId = registerResponse.MachineId };

        GetConfigurationResponse configResponse = await configClient.GetConfigurationAsync(configRequest, headers: headers);
        await Assert.That(configResponse.TimeConfig).IsNotNull();
        await Assert.That(configResponse.TimeConfig.HeartbeatTimeInSeconds).IsGreaterThan(0);
    }

    [Test]
    public async Task GetRegistrationStatus_OldApiKeyInvalidatedAfterReissuance()
    {
        // Arrange — after re-issuance the original API key must no longer authenticate,
        // ensuring that key rotation actually revokes the compromised credential
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long tokenId) = await SeedTenantWithToken(db);
        await SeedActiveSubscription(db, tenantId);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient registrationClient = new(channel);

        RegisterSystemRequest registerRequest = new()
        {
            Hostname = "old-key-host",
            SerialNumber = "sn-oldkey-001",
            SystemId = "sys-oldkey-001",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };
        RegisterSystemResponse registerResponse = await registrationClient.RegisterSystemAsync(registerRequest);
        await Assert.That(registerResponse.MachineId).IsGreaterThan(0);

        string originalApiKey = registerResponse.ApiKey;

        // The first NeedsApiKey call delivers the original key from Redis cache
        // (stored during registration) and removes it. This simulates the agent
        // picking up its initial key.
        SystemRegistrationStatusRequest firstDelivery = new()
        {
            SerialNumber = "sn-oldkey-001",
            SystemId = "sys-oldkey-001",
            RegistrationToken = "test-registration-token",
            NeedsApiKey = true
        };
        SystemRegistrationStatusResponse firstResponse = await registrationClient.GetRegistrationStatusAsync(firstDelivery);
        await Assert.That(firstResponse.ApiKey).IsNotEmpty();

        // The second NeedsApiKey call finds no cached key and triggers a true
        // re-issuance that generates a new key and overwrites ApiKeyHash.
        SystemRegistrationStatusRequest reissueRequest = new()
        {
            SerialNumber = "sn-oldkey-001",
            SystemId = "sys-oldkey-001",
            RegistrationToken = "test-registration-token",
            NeedsApiKey = true
        };
        SystemRegistrationStatusResponse reissueResponse = await registrationClient.GetRegistrationStatusAsync(reissueRequest);
        await Assert.That(reissueResponse.ApiKey).IsNotEmpty();

        // Act — attempt to use the original API key for an authenticated call
        Configuration.ConfigurationClient configClient = new(channel);
        Metadata headers = new() { { "x-api-key", originalApiKey } };
        GetConfigurationRequest configRequest = new() { MachineId = registerResponse.MachineId };

        RpcException? exception = null;
        try
        {
            await configClient.GetConfigurationAsync(configRequest, headers: headers);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        // Assert — the old key must be rejected
        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task GetRegistrationStatus_DeletedMachine_CannotReissueApiKey()
    {
        // Arrange — soft-deleted machines must not be resurrectable via API key re-issuance
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long tokenId) = await SeedTenantWithToken(db);
        await SeedActiveSubscription(db, tenantId);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient registrationClient = new(channel);

        RegisterSystemRequest registerRequest = new()
        {
            Hostname = "deleted-host",
            SerialNumber = "sn-deleted-001",
            SystemId = "sys-deleted-001",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };
        RegisterSystemResponse registerResponse = await registrationClient.RegisterSystemAsync(registerRequest);
        await Assert.That(registerResponse.MachineId).IsGreaterThan(0);

        // Soft-delete the machine directly in the database
        await db.Machines
            .Where(m => m.Id == registerResponse.MachineId)
            .Set(m => m.IsDeleted, true)
            .UpdateAsync();

        // Act — attempt to re-issue API key for the deleted machine
        SystemRegistrationStatusRequest statusRequest = new()
        {
            SerialNumber = "sn-deleted-001",
            SystemId = "sys-deleted-001",
            RegistrationToken = "test-registration-token",
            NeedsApiKey = true
        };
        SystemRegistrationStatusResponse statusResponse = await registrationClient.GetRegistrationStatusAsync(statusRequest);

        // Assert — deleted machine should appear as unknown, preventing resurrection
        await Assert.That(statusResponse.Status).IsEqualTo(RegistrationStatus.UnknownRegistration);
    }

    [Test]
    public async Task GetRegistrationStatus_CrossTenantToken_CannotReissueKeyForOtherTenantMachine()
    {
        // Arrange — a registration token scoped to tenant B must not be usable to
        // look up or re-issue keys for a machine registered under tenant A
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Create tenant A with its own token and register a machine
        int tenantAId = await SeedTenant(db);
        await SeedActiveSubscription(db, tenantAId);
        string tenantATokenPlaintext = "tenant-a-token-value";
        string tenantATokenHash = ComputeHash(tenantATokenPlaintext);
        RegistrationToken tenantAToken = new()
        {
            TenantId = tenantAId,
            TokenHash = tenantATokenHash,
            Name = "Tenant A Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = false
        };
        await db.InsertWithIdentityAsync(tenantAToken);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient registrationClient = new(channel);

        RegisterSystemRequest registerRequest = new()
        {
            Hostname = "tenant-a-host",
            SerialNumber = "sn-cross-tenant-001",
            SystemId = "sys-cross-tenant-001",
            RegistrationToken = tenantATokenPlaintext,
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };
        RegisterSystemResponse registerResponse = await registrationClient.RegisterSystemAsync(registerRequest);
        await Assert.That(registerResponse.MachineId).IsGreaterThan(0);

        // Create tenant B with its own token
        int tenantBId = await SeedTenant(db);
        await SeedActiveSubscription(db, tenantBId);
        string tenantBTokenPlaintext = "tenant-b-token-value";
        string tenantBTokenHash = ComputeHash(tenantBTokenPlaintext);
        RegistrationToken tenantBToken = new()
        {
            TenantId = tenantBId,
            TokenHash = tenantBTokenHash,
            Name = "Tenant B Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = false
        };
        await db.InsertWithIdentityAsync(tenantBToken);

        // Act — use tenant B's token to look up tenant A's machine
        SystemRegistrationStatusRequest statusRequest = new()
        {
            SerialNumber = "sn-cross-tenant-001",
            SystemId = "sys-cross-tenant-001",
            RegistrationToken = tenantBTokenPlaintext,
            NeedsApiKey = true
        };
        SystemRegistrationStatusResponse statusResponse = await registrationClient.GetRegistrationStatusAsync(statusRequest);

        // Assert — tenant B's token must not resolve tenant A's machine
        await Assert.That(statusResponse.Status).IsEqualTo(RegistrationStatus.UnknownRegistration);
    }

    private static GrpcChannel CreateChannel(FunctionalTestFactory factory)
    {
        HttpMessageHandler handler = new ResponseVersionHandler
        {
            InnerHandler = factory.Server.CreateHandler()
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler
        });
    }

    private static async Task<int> SeedTenant(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            Name = $"Test Tenant {Guid.NewGuid():N}",
            ExternalId = $"ext-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };

        return (int)(long)await db.InsertWithIdentityAsync(tenant);
    }

    private static async Task<(int tenantId, long tokenId)> SeedTenantWithToken(DatabaseContext db)
    {
        int tenantId = await SeedTenant(db);

        string tokenHash = ComputeHash("test-registration-token");
        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = tokenHash,
            Name = "Test Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = false
        };
        long tokenId = (long)await db.InsertWithIdentityAsync(token);

        return (tenantId, tokenId);
    }

    private static async Task SeedActiveSubscription(DatabaseContext db, int tenantId)
    {
        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(subscription);
    }

    private static string ComputeHash(string input)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
