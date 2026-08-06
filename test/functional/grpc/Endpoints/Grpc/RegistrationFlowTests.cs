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
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
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
        // Arrange — the Free tier allows 3 machines (defined in TierFeatureLimits seed data).
        // Registration tokens are single-use, so each fill machine needs its own token; the
        // over-limit machine gets a fresh token too, isolating the failure to the limit guard.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenant(db);

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

        // Register 3 machines, each with its own single-use token, to fill the Free tier limit
        for (int i = 1; i <= 3; i++)
        {
            string fillToken = $"limit-fill-token-{i}";
            await SeedToken(db, tenantId, fillToken);
            RegisterSystemRequest fillRequest = new()
            {
                Hostname = $"fill-host-{i}",
                SerialNumber = $"sn-limit-{i:D3}",
                SystemId = $"sys-limit-{i:D3}",
                RegistrationToken = fillToken,
                MachineType = MachineType.BareMetalServerType,
                Os = OperatingSystemType.UbuntuOs
            };
            RegisterSystemResponse fillResponse = await client.RegisterSystemAsync(fillRequest);
            await Assert.That(fillResponse.MachineId).IsGreaterThan(0);
        }

        // Act — register a 4th machine with a fresh token (should fail because limit is 3)
        await SeedToken(db, tenantId, "limit-over-token");
        RegisterSystemRequest overLimitRequest = new()
        {
            Hostname = "over-limit-host",
            SerialNumber = "sn-limit-004",
            SystemId = "sys-limit-004",
            RegistrationToken = "limit-over-token",
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
    public async Task RegisterSystem_SingleUseToken_RegistersOneMachine_ThenRejectsReuse()
    {
        // Arrange — a registration token is single-use: it registers exactly one machine, then is
        // permanently consumed. A second registration with the same token must be rejected.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long _) = await SeedTenantWithToken(db);
        await SeedActiveSubscription(db, tenantId);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient client = new(channel);

        RegisterSystemRequest firstRequest = new()
        {
            Hostname = "single-use-host-1",
            SerialNumber = "sn-single-001",
            SystemId = "sys-single-001",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };

        // Act — first registration succeeds
        RegisterSystemResponse firstResponse = await client.RegisterSystemAsync(firstRequest);
        await Assert.That(firstResponse.MachineId).IsGreaterThan(0);

        // The token row must now be consumed by the created machine
        RegistrationToken consumed = await db.RegistrationTokens
            .FirstAsync(t => t.TokenHash == ComputeHash("test-registration-token"));
        await Assert.That(consumed.ConsumedAt).IsNotNull();
        await Assert.That(consumed.ConsumedByMachineId).IsEqualTo(firstResponse.MachineId);

        // Act & Assert — a second registration with the SAME token must be rejected
        RegisterSystemRequest secondRequest = new()
        {
            Hostname = "single-use-host-2",
            SerialNumber = "sn-single-002",
            SystemId = "sys-single-002",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };
        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await client.RegisterSystemAsync(secondRequest));
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(ex.Status.Detail).Contains("already been used");

        // Only one machine may exist for that token
        int machineCount = await db.Machines.CountAsync(m => m.RegistrationTokenId == consumed.Id);
        await Assert.That(machineCount).IsEqualTo(1);
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
    public async Task GetRegistrationStatus_ClaimedKey_SecondCallerRefusedAndIncumbentKeySurvives()
    {
        // Arrange — once an agent has claimed its initial key, a second caller presenting the same
        // serial and system id must not be able to re-key the machine. Before this guard existed
        // the second call re-issued, overwrote the stored hash and invalidated the incumbent's
        // credentials, taking a live machine permanently offline with no error on either side.
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

        string incumbentApiKey = registerResponse.ApiKey;

        // Mark the key as accepted, which in production the authentication handler does on the
        // agent's first successful call. Note the pending-key cache entry from registration is
        // deliberately left in place: auto-enrolment returns the key inline, so the agent never
        // claims that entry and it survives its full TTL. A second caller must not be handed it.
        await db.Machines
            .Where(m => m.Id == registerResponse.MachineId)
            .Set(m => m.KeyDeliveredAt, DateTimeOffset.UtcNow)
            .UpdateAsync();

        // Act — a second caller with the same identity asks for a key.
        SystemRegistrationStatusRequest secondCaller = new()
        {
            SerialNumber = "sn-reissue-auth-001",
            SystemId = "sys-reissue-auth-001",
            RegistrationToken = "test-registration-token",
            NeedsApiKey = true
        };
        SystemRegistrationStatusResponse secondResponse = await registrationClient.GetRegistrationStatusAsync(secondCaller);

        // Assert — refused, and no credential handed out.
        await Assert.That(secondResponse.Status).IsEqualTo(RegistrationStatus.UnknownRegistration);
        await Assert.That(secondResponse.ApiKey).IsEmpty();

        // The incumbent's key must still authenticate. This is the regression guard.
        Configuration.ConfigurationClient configClient = new(channel);
        Metadata headers = new() { { "x-api-key", incumbentApiKey } };
        GetConfigurationRequest configRequest = new() { MachineId = registerResponse.MachineId };

        GetConfigurationResponse configResponse = await configClient.GetConfigurationAsync(configRequest, headers: headers);
        await Assert.That(configResponse.TimeConfig).IsNotNull();
        await Assert.That(configResponse.TimeConfig.HeartbeatTimeInSeconds).IsGreaterThan(0);
    }

    [Test]
    public async Task GetRegistrationStatus_AcceptedKey_CachedCopyIsNotDisclosed()
    {
        // The pending-key cache entry seeded at registration is never claimed under auto-enrolment,
        // because the key is returned inline in the RegisterSystem response. It therefore sits in
        // Redis for its whole TTL holding the machine's LIVE credential. A second caller polling
        // status inside that window must not receive it: that would be silent credential disclosure
        // with no re-issue, nothing invalidated, and no trace on either host.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long tokenId) = await SeedTenantWithToken(db);
        await SeedActiveSubscription(db, tenantId);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient registrationClient = new(channel);

        RegisterSystemRequest registerRequest = new()
        {
            Hostname = "disclosure-host",
            SerialNumber = "sn-disclosure-001",
            SystemId = "sys-disclosure-001",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };
        RegisterSystemResponse registerResponse = await registrationClient.RegisterSystemAsync(registerRequest);
        string incumbentApiKey = registerResponse.ApiKey;

        // The agent authenticates, which records acceptance.
        Configuration.ConfigurationClient configClient = new(channel);
        Metadata incumbentHeaders = new() { { "x-api-key", incumbentApiKey } };
        await configClient.GetConfigurationAsync(
            new GetConfigurationRequest { MachineId = registerResponse.MachineId },
            headers: incumbentHeaders);

        // Act — a second caller asks for a key while the cache entry is still live.
        SystemRegistrationStatusResponse secondResponse = await registrationClient.GetRegistrationStatusAsync(
            new SystemRegistrationStatusRequest
            {
                SerialNumber = "sn-disclosure-001",
                SystemId = "sys-disclosure-001",
                RegistrationToken = "test-registration-token",
                NeedsApiKey = true
            });

        // Assert — nothing handed out, and above all not the incumbent's live key.
        await Assert.That(secondResponse.Status).IsEqualTo(RegistrationStatus.UnknownRegistration);
        await Assert.That(secondResponse.ApiKey).IsEmpty();
        await Assert.That(secondResponse.ApiKey).IsNotEqualTo(incumbentApiKey);

        // The incumbent is unaffected.
        GetConfigurationResponse configResponse = await configClient.GetConfigurationAsync(
            new GetConfigurationRequest { MachineId = registerResponse.MachineId },
            headers: incumbentHeaders);
        await Assert.That(configResponse.TimeConfig).IsNotNull();
    }

    [Test]
    public async Task GetRegistrationStatus_KeyNeverClaimed_StillReissuesForRecovery()
    {
        // Arrange — the guard keys on whether the initial key was ever CLAIMED, not on whether the
        // machine exists. A machine whose cached key was lost before any agent picked it up must
        // still be able to obtain one, otherwise a Redis eviction would strand it permanently.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long tokenId) = await SeedTenantWithToken(db);
        await SeedActiveSubscription(db, tenantId);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient registrationClient = new(channel);

        RegisterSystemRequest registerRequest = new()
        {
            Hostname = "never-claimed-host",
            SerialNumber = "sn-neverclaimed-001",
            SystemId = "sys-neverclaimed-001",
            RegistrationToken = "test-registration-token",
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs
        };
        RegisterSystemResponse registerResponse = await registrationClient.RegisterSystemAsync(registerRequest);
        await Assert.That(registerResponse.MachineId).IsGreaterThan(0);

        // Act — the very first claim still succeeds and yields a usable key.
        SystemRegistrationStatusRequest statusRequest = new()
        {
            SerialNumber = "sn-neverclaimed-001",
            SystemId = "sys-neverclaimed-001",
            RegistrationToken = "test-registration-token",
            NeedsApiKey = true
        };
        SystemRegistrationStatusResponse statusResponse = await registrationClient.GetRegistrationStatusAsync(statusRequest);

        await Assert.That(statusResponse.Status).IsEqualTo(RegistrationStatus.RegistrationActive);
        await Assert.That(statusResponse.MachineId).IsEqualTo(registerResponse.MachineId);
        await Assert.That(statusResponse.ApiKey).IsNotEmpty();

        Configuration.ConfigurationClient configClient = new(channel);
        Metadata headers = new() { { "x-api-key", statusResponse.ApiKey } };
        GetConfigurationRequest configRequest = new() { MachineId = registerResponse.MachineId };

        GetConfigurationResponse configResponse = await configClient.GetConfigurationAsync(configRequest, headers: headers);
        await Assert.That(configResponse.TimeConfig).IsNotNull();
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
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
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
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
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
        long tokenId = await SeedToken(db, tenantId, "test-registration-token");

        return (tenantId, tokenId);
    }

    private static async Task<long> SeedToken(DatabaseContext db, int tenantId, string tokenPlaintext)
    {
        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = ComputeHash(tokenPlaintext),
            Name = "Test Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false
        };

        return (long)await db.InsertWithIdentityAsync(token);
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
