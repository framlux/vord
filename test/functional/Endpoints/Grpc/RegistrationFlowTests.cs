// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using Framlux.FleetManagement.Grpc.AgentRegistration;
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

        // Act
        RegisterSystemResponse response = await client.RegisterSystemAsync(request);

        // Assert
        await Assert.That(response.MachineId).IsEqualTo(0);
        await Assert.That(response.ApiKey).IsEmpty();
        await Assert.That(response.ErrorMessage).IsNotEmpty();
    }

    [Test]
    public async Task RegisterSystem_ExpiredToken_ReturnsError()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenant(db);
        await SeedActiveSubscription(db, tenantId);

        string tokenPlaintext = "expired-token-value";
        string tokenHash = ComputeHash(tokenPlaintext);
        RegistrationToken expiredToken = new()
        {
            TenantId = tenantId,
            TokenHash = tokenHash,
            Name = "Expired Token",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1),
            MaxUses = 10,
            UsedCount = 0,
            CreatedByUserId = 0,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            IsRevoked = false
        };
        await db.InsertAsync(expiredToken);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        RegisterSystemRequest request = new()
        {
            Hostname = "test-host-3",
            SerialNumber = "sn-expired-001",
            SystemId = "sys-expired-001",
            RegistrationToken = tokenPlaintext,
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };

        // Act
        RegisterSystemResponse response = await client.RegisterSystemAsync(request);

        // Assert
        await Assert.That(response.MachineId).IsEqualTo(0);
        await Assert.That(response.ErrorMessage).Contains("expired");
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
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            MaxUses = 10,
            UsedCount = 0,
            CreatedByUserId = 0,
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

        // Act
        RegisterSystemResponse response = await client.RegisterSystemAsync(request);

        // Assert
        await Assert.That(response.MachineId).IsEqualTo(0);
        await Assert.That(response.ErrorMessage).Contains("revoked");
    }

    [Test]
    public async Task RegisterSystem_MachineLimitExceeded_ReturnsError()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long tokenId) = await SeedTenantWithToken(db);

        // Create subscription with limit of 1 machine
        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            MachineLimit = 1,
            RetentionDays = 7,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(subscription);

        // Register the first machine (fills the limit)
        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        RegisterSystemRequest firstRequest = new()
        {
            Hostname = "first-host",
            SerialNumber = "sn-limit-001",
            SystemId = "sys-limit-001",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };
        RegisterSystemResponse firstResponse = await client.RegisterSystemAsync(firstRequest);
        await Assert.That(firstResponse.MachineId).IsGreaterThan(0);

        // Act — register a second machine (should fail)
        RegisterSystemRequest secondRequest = new()
        {
            Hostname = "second-host",
            SerialNumber = "sn-limit-002",
            SystemId = "sys-limit-002",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };

        RegisterSystemResponse secondResponse = await client.RegisterSystemAsync(secondRequest);

        // Assert
        await Assert.That(secondResponse.MachineId).IsEqualTo(0);
        await Assert.That(secondResponse.ErrorMessage).Contains("limit");
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

        // Act
        RegisterSystemResponse response = await client.RegisterSystemAsync(request);

        // Assert
        await Assert.That(response.MachineId).IsEqualTo(0);
        await Assert.That(response.ErrorMessage).Contains("Hostname");
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
            CreatedByUserId = 0,
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
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            MaxUses = 100,
            UsedCount = 0,
            CreatedByUserId = 0,
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
            MachineLimit = 10,
            RetentionDays = 7,
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
