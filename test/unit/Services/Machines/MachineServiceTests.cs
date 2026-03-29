// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Grpc.AgentRegistration;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using System.Security.Cryptography;
using System.Text;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="MachineService"/>.
/// </summary>
public class MachineServiceTests
{
    private const string TestTokenValue = "test-reg-token";

    private static string ComputeTokenHash(string token)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static async Task<RegistrationToken> SeedValidRegistrationToken(TestDatabaseFactory dbFactory, int tenantId = 1, int maxUses = 100)
    {
        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = ComputeTokenHash(TestTokenValue),
            Name = "Test Token",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            MaxUses = maxUses,
            UsedCount = 0,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = false,
        };
        token.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        return token;
    }

    private static TestServiceScopeFactory CreateScopeFactory(TestDatabaseFactory dbFactory, IDatabaseCache dbCache)
    {
        return new TestServiceScopeFactory(dbFactory.Context, new Dictionary<Type, object>
        {
            { typeof(IDatabaseCache), dbCache },
        });
    }

    // ========== GetRegistrationStatus tests ==========

    [Test]
    public async Task GetRegistrationStatus_EmptyToken_ReturnsUnknownRegistration()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync("UNKNOWN-SN", "UNKNOWN-SID", "", CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.UnknownRegistration);
        await Assert.That(result.id).IsNull();
        await Assert.That(result.apiKey).IsNull();
    }

    [Test]
    public async Task GetRegistrationStatus_NoMachineFound_ReturnsUnknown()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedValidRegistrationToken(dbFactory);
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync("NON-EXISTENT-SN", "NON-EXISTENT-SID", TestTokenValue, CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.UnknownRegistration);
        await Assert.That(result.id).IsNull();
        await Assert.That(result.apiKey).IsNull();
    }

    [Test]
    public async Task GetRegistrationStatus_ActiveMachine_WithCachedKey_ReturnsActiveWithKey()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>("test-api-key-plaintext"));

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.RegistrationActive);
        await Assert.That(result.id).IsEqualTo(machine.Id);
        await Assert.That(result.apiKey).IsEqualTo("test-api-key-plaintext");
    }

    [Test]
    public async Task GetRegistrationStatus_ActiveMachine_NoCachedKey_ReturnsActiveWithNullKey()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.RegistrationActive);
        await Assert.That(result.id).IsEqualTo(machine.Id);
        await Assert.That(result.apiKey).IsNull();
    }

    [Test]
    public async Task GetRegistrationStatus_InvalidToken_ReturnsUnknown()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync("SN-001", "SID-001", "invalid-token", CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.UnknownRegistration);
        await Assert.That(result.id).IsNull();
    }

    // ========== RegisterSystem - Token Validation tests ==========

    [Test]
    public async Task RegisterSystem_NoToken_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();
        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, dbCache);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-TEST",
            SystemId = "SID-TEST",
            Hostname = "test-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = "",
        };

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(request, CancellationToken.None);

        await Assert.That(result.machineId).IsNull();
        await Assert.That(result.apiKey).IsNull();
        await Assert.That(result.errorMessage).IsEqualTo("Registration token is required");
    }

    [Test]
    public async Task RegisterSystem_InvalidToken_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();
        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, dbCache);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-TEST",
            SystemId = "SID-TEST",
            Hostname = "test-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = "invalid-token-that-does-not-exist",
        };

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(request, CancellationToken.None);

        await Assert.That(result.machineId).IsNull();
        await Assert.That(result.apiKey).IsNull();
        await Assert.That(result.errorMessage).IsEqualTo("Invalid registration token");
    }

    [Test]
    public async Task RegisterSystem_RevokedToken_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory);
        await dbFactory.Context.RegistrationTokens
            .Where(t => t.Id == token.Id)
            .Set(t => t.IsRevoked, true)
            .Set(t => t.RevokedAt, DateTimeOffset.UtcNow)
            .UpdateAsync();

        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();
        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, dbCache);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-TEST",
            SystemId = "SID-TEST",
            Hostname = "test-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(request, CancellationToken.None);

        await Assert.That(result.machineId).IsNull();
        await Assert.That(result.apiKey).IsNull();
        await Assert.That(result.errorMessage).IsEqualTo("Registration token has been revoked");
    }

    [Test]
    public async Task RegisterSystem_ExpiredToken_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory);
        await dbFactory.Context.RegistrationTokens
            .Where(t => t.Id == token.Id)
            .Set(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1))
            .UpdateAsync();

        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();
        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, dbCache);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-TEST",
            SystemId = "SID-TEST",
            Hostname = "test-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(request, CancellationToken.None);

        await Assert.That(result.machineId).IsNull();
        await Assert.That(result.apiKey).IsNull();
        await Assert.That(result.errorMessage).IsEqualTo("Registration token has expired");
    }

    [Test]
    public async Task RegisterSystem_ExhaustedToken_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, maxUses: 1);
        await dbFactory.Context.RegistrationTokens
            .Where(t => t.Id == token.Id)
            .Set(t => t.UsedCount, 1)
            .UpdateAsync();

        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();
        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, dbCache);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-TEST",
            SystemId = "SID-TEST",
            Hostname = "test-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(request, CancellationToken.None);

        await Assert.That(result.machineId).IsNull();
        await Assert.That(result.apiKey).IsNull();
        await Assert.That(result.errorMessage).IsEqualTo("Registration token usage limit exceeded");
    }

    [Test]
    public async Task RegisterSystem_ValidToken_ReturnsMachineIdAndApiKey()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, tenantId: 5);

        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();
        dbCache.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: 5, registrationTokenId: token.Id);
        createdMachine.Id = 100;
        dbCache.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "plaintext-api-key-123"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, dbCache);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-TEST",
            SystemId = "SID-TEST",
            Hostname = "test-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(request, CancellationToken.None);

        await Assert.That(result.machineId).IsEqualTo(100L);
        await Assert.That(result.apiKey).IsEqualTo("plaintext-api-key-123");
        await Assert.That(result.errorMessage).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task RegisterSystem_ValidToken_IncrementsTokenUsageCount()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, tenantId: 5);

        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();
        dbCache.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: 5, registrationTokenId: token.Id);
        createdMachine.Id = 100;
        dbCache.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "plaintext-api-key-123"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, dbCache);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-TEST",
            SystemId = "SID-TEST",
            Hostname = "test-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        await service.RegisterSystemAsync(request, CancellationToken.None);

        RegistrationToken? updatedToken = await dbFactory.Context.RegistrationTokens
            .FirstOrDefaultAsync(t => t.Id == token.Id);
        await Assert.That(updatedToken).IsNotNull();
        await Assert.That(updatedToken!.UsedCount).IsEqualTo(1);
    }

    [Test]
    public async Task RegisterSystem_ValidToken_CreatesMachineWithTokenTenantId()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, tenantId: 5);

        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();
        dbCache.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: 5, registrationTokenId: token.Id);
        createdMachine.Id = 100;
        dbCache.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "plaintext-api-key-123"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, dbCache);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-TEST",
            SystemId = "SID-TEST",
            Hostname = "test-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        await service.RegisterSystemAsync(request, CancellationToken.None);

        await dbCache.Received(1).CreateMachineWithKeyAsync(
            Arg.Is<Machine>(m => m.TenantId == 5),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_ExistingMachine_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedValidRegistrationToken(dbFactory);

        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();
        dbCache.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, dbCache);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-TEST",
            SystemId = "SID-TEST",
            Hostname = "test-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(request, CancellationToken.None);

        await Assert.That(result.machineId).IsNull();
        await Assert.That(result.apiKey).IsNull();
        await Assert.That(result.errorMessage).IsEqualTo("Machine already exists");
    }
}
