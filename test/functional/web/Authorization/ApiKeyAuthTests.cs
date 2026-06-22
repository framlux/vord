// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.FleetManagement.Grpc.AgentConfiguration;
using Grpc.Core;
using Grpc.Net.Client;
using LinqToDB;
using System.Security.Cryptography;
using System.Text;

namespace Framlux.FleetManagement.FunctionalTest.Authorization;

/// <summary>
/// Functional tests for API key authentication enforcement on gRPC endpoints.
/// Uses the Configuration service (which requires API key auth) to test auth behavior.
/// </summary>
public sealed class ApiKeyAuthTests
{
    [Test]
    public async Task GrpcCall_WithoutApiKey_ReturnsUnauthenticated()
    {
        // Arrange
        using FunctionalTestFactory factory = new();

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        GetConfigurationRequest request = new() { MachineId = 1 };

        // Act & Assert — calling without API key should fail
        RpcException? exception = null;
        try
        {
            await client.GetConfigurationAsync(request);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task GrpcCall_WithInvalidApiKey_ReturnsUnauthenticated()
    {
        // Arrange
        using FunctionalTestFactory factory = new();

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        Metadata headers = new() { { "x-api-key", "completely-invalid-api-key" } };
        GetConfigurationRequest request = new() { MachineId = 1 };

        // Act & Assert
        RpcException? exception = null;
        try
        {
            await client.GetConfigurationAsync(request, headers: headers);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task GrpcCall_WithValidApiKey_Succeeds()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "valid-test-api-key-for-auth-tests";
        long machineId = await SeedMachineWithApiKey(db, apiKey);

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        GetConfigurationRequest request = new() { MachineId = machineId };

        // Act
        GetConfigurationResponse response = await client.GetConfigurationAsync(request, headers: headers);

        // Assert — should get a valid configuration response
        await Assert.That(response.TimeConfig).IsNotNull();
        await Assert.That(response.TimeConfig.HeartbeatTimeInSeconds).IsGreaterThan(0);
        await Assert.That(response.TimeConfig.ConfigurationRefreshTimeInSeconds).IsGreaterThan(0);
    }

    [Test]
    public async Task GrpcCall_WithDeletedMachineApiKey_ReturnsUnauthenticated()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        string apiKey = "deleted-machine-api-key";
        await SeedMachineWithApiKey(db, apiKey, isDeleted: true);

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        GetConfigurationRequest request = new() { MachineId = 1 };

        // Act & Assert
        RpcException? exception = null;
        try
        {
            await client.GetConfigurationAsync(request, headers: headers);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
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

    private static async Task<long> SeedMachineWithApiKey(DatabaseContext db, string plaintextApiKey, bool isDeleted = false)
    {
        Tenant tenant = new()
        {
            Name = $"Auth Test Tenant {Guid.NewGuid():N}",
            ExternalId = $"ext-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        int tenantId = (int)(long)await db.InsertWithIdentityAsync(tenant);

        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Auth Test Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false
        };
        long tokenId = (long)await db.InsertWithIdentityAsync(token);

        string apiKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextApiKey)));
        Machine machine = new()
        {
            ApiKeyHash = apiKeyHash,
            Name = "auth-test-machine",
            SerialNumber = $"sn-auth-{Guid.NewGuid():N}",
            SystemId = $"sys-auth-{Guid.NewGuid():N}",
            AssetTagNumber = null,
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = tokenId,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = isDeleted,
            TenantId = tenantId
        };
        long machineId = (long)await db.InsertWithIdentityAsync(machine);

        return machineId;
    }
}
