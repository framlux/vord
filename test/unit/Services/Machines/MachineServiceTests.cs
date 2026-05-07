// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Grpc.AgentRegistration;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

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

    private static async Task<RegistrationToken> SeedValidRegistrationToken(TestDatabaseFactory dbFactory, int tenantId = 1)
    {
        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = ComputeTokenHash(TestTokenValue),
            Name = "Test Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = false,
        };
        token.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        return token;
    }

    private static TestServiceScopeFactory CreateScopeFactory(TestDatabaseFactory dbFactory, IMachineRepository machineRepo, ITenantRepository? tenantRepo = null, ISubscriptionService? subscriptionService = null)
    {
        Dictionary<Type, object> services = new()
        {
            { typeof(IMachineRepository), machineRepo },
        };

        if (tenantRepo is not null)
        {
            services[typeof(ITenantRepository)] = tenantRepo;
        }

        // RegisterSystemAsync resolves ISubscriptionService from the scope; provide a default mock
        // that returns a Free subscription and default limits so tests that do not care about billing still pass
        if (subscriptionService is null)
        {
            ISubscriptionService defaultSubService = Substitute.For<ISubscriptionService>();
            defaultSubService.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new TenantSubscription
                {
                    TenantId = 1,
                    Tier = SubscriptionTier.Free,
                    Status = SubscriptionStatus.Active,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            defaultSubService.GetEffectiveLimitsForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new EffectiveLimits { MachineLimit = 3, RetentionDays = 1 });
            subscriptionService = defaultSubService;
        }

        services[typeof(ISubscriptionService)] = subscriptionService;

        return new TestServiceScopeFactory(dbFactory.Context, services);
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
            await service.GetRegistrationStatusAsync("UNKNOWN-SN", "UNKNOWN-SID", "", true, CancellationToken.None);

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
            await service.GetRegistrationStatusAsync("NON-EXISTENT-SN", "NON-EXISTENT-SID", TestTokenValue, true, CancellationToken.None);

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
            await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, true, CancellationToken.None);

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
            await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, false, CancellationToken.None);

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
            await service.GetRegistrationStatusAsync("SN-001", "SID-001", "invalid-token", true, CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.UnknownRegistration);
        await Assert.That(result.id).IsNull();
    }

    [Test]
    public async Task GetRegistrationStatus_NeedsApiKey_CacheExpired_ReissuesNewKey()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineBySerialAndSystemIdAsync(machine.SerialNumber, machine.SystemId, token.TenantId, Arg.Any<CancellationToken>())
            .Returns(machine);
        machineRepo.ReissueApiKeyAsync(machine.Id, Arg.Any<CancellationToken>())
            .Returns("reissued-plaintext-key");

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, true, CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.RegistrationActive);
        await Assert.That(result.id).IsEqualTo(machine.Id);
        await Assert.That(result.apiKey).IsEqualTo("reissued-plaintext-key");
        await machineRepo.Received(1).ReissueApiKeyAsync(machine.Id, Arg.Any<CancellationToken>());
        await redisDb.Received(1).StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Is<RedisValue>(v => v == "reissued-plaintext-key"),
            Arg.Is<Expiration>(e => e.Equals(new Expiration(TimeSpan.FromHours(24)))));
    }

    [Test]
    public async Task GetRegistrationStatus_NeedsApiKeyFalse_NoCachedKey_ReturnsNullKey()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, false, CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.RegistrationActive);
        await Assert.That(result.id).IsEqualTo(machine.Id);
        await Assert.That(result.apiKey).IsNull();
    }

    [Test]
    public async Task GetRegistrationStatus_RevokedToken_NeedsApiKey_ReturnsUnknown()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory);
        await dbFactory.Context.RegistrationTokens
            .Where(t => t.Id == token.Id)
            .Set(t => t.IsRevoked, true)
            .Set(t => t.RevokedAt, DateTimeOffset.UtcNow)
            .UpdateAsync();

        Machine machine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, true, CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.UnknownRegistration);
        await Assert.That(result.apiKey).IsNull();
    }

    // ========== RegisterSystem - Token Validation tests ==========

    [Test]
    public async Task RegisterSystem_NoToken_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
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
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
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

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
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
    public async Task RegisterSystem_ValidToken_ReturnsMachineIdAndApiKey()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, tenantId: 5);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: 5, registrationTokenId: token.Id);
        createdMachine.Id = 100;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "plaintext-api-key-123"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
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
    public async Task RegisterSystem_ValidToken_CreatesMachineWithTokenTenantId()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, tenantId: 5);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: 5, registrationTokenId: token.Id);
        createdMachine.Id = 100;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "plaintext-api-key-123"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
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

        await machineRepo.Received(1).CreateMachineWithKeyAsync(
            Arg.Is<Machine>(m => m.TenantId == 5),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_ExistingMachine_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedValidRegistrationToken(dbFactory);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
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

    // ========== M1: ReportMachineUsage called on machine registration ==========

    [Test]
    public async Task RegisterSystem_PaidTier_CallsReportMachineUsageWithCorrectCount()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, tenantId: 1);

        // Seed a tenant with an external ID so the billing client can be called
        Tenant tenant = TestDataBuilder.BuildTenant(externalId: "ext-tenant-billing");
        tenant.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Seed a Pro subscription so the billing path is triggered
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        // Seed tier feature limits so GetEffectiveLimitsForTenantAsync works
        await SeedTierFeatureLimitsAsync(dbFactory);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: 1, registrationTokenId: token.Id);
        createdMachine.Id = 200;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "api-key-billing-test"));
        machineRepo.GetActiveMachineCountAsync(1, Arg.Any<CancellationToken>())
            .Returns(7);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(tenant);

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(sub);
        subscriptionService.GetEffectiveLimitsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(
            new EffectiveLimits { MachineLimit = 1000, RetentionDays = 60, AlertRuleLimit = 10, WebhookLimit = 5 });

        Dictionary<Type, object> services = new()
        {
            { typeof(IMachineRepository), machineRepo },
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ISubscriptionService), subscriptionService },
        };
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context, services);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        billingApiClient.ReportMachineUsageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);

        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-BILLING",
            SystemId = "SID-BILLING",
            Hostname = "billing-test-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(request, CancellationToken.None);

        await Assert.That(result.machineId).IsEqualTo(200L);
        await Assert.That(result.errorMessage).IsEqualTo(string.Empty);

        // Verify billing was called with the tenant external ID and the correct active machine count
        await billingApiClient.Received(1).ReportMachineUsageAsync(
            "ext-tenant-billing", 7, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_FreeTier_DoesNotCallReportMachineUsage()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, tenantId: 1);

        // Seed a Free subscription so billing is skipped
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        await SeedTierFeatureLimitsAsync(dbFactory);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: 1, registrationTokenId: token.Id);
        createdMachine.Id = 201;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "api-key-free-test"));

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(sub);
        subscriptionService.GetEffectiveLimitsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(
            new EffectiveLimits { MachineLimit = 3, RetentionDays = 1 });

        Dictionary<Type, object> services = new()
        {
            { typeof(IMachineRepository), machineRepo },
            { typeof(ISubscriptionService), subscriptionService },
        };
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context, services);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();

        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-FREE",
            SystemId = "SID-FREE",
            Hostname = "free-test-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(request, CancellationToken.None);

        await Assert.That(result.machineId).IsEqualTo(201L);

        // Free tier should NOT call billing
        await billingApiClient.DidNotReceive().ReportMachineUsageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_BillingFailure_DoesNotPreventRegistration()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, tenantId: 1);

        Tenant tenant = TestDataBuilder.BuildTenant(externalId: "ext-tenant-fail");
        tenant.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        await SeedTierFeatureLimitsAsync(dbFactory);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: 1, registrationTokenId: token.Id);
        createdMachine.Id = 202;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "api-key-fail-test"));
        machineRepo.GetActiveMachineCountAsync(1, Arg.Any<CancellationToken>())
            .Returns(3);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(tenant);

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(sub);
        subscriptionService.GetEffectiveLimitsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(
            new EffectiveLimits { MachineLimit = 1000, RetentionDays = 60, AlertRuleLimit = 10, WebhookLimit = 5 });

        Dictionary<Type, object> services = new()
        {
            { typeof(IMachineRepository), machineRepo },
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ISubscriptionService), subscriptionService },
        };
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context, services);
        ILogger<MachineService> logger = new NullLogger<MachineService>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());

        // Simulate billing client throwing an exception
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        billingApiClient.ReportMachineUsageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("Billing service unavailable"));

        MachineService service = new(scopeFactory, logger, redis, billingApiClient);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-FAIL",
            SystemId = "SID-FAIL",
            Hostname = "fail-test-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        // Registration should succeed even when billing fails (best-effort pattern)
        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(request, CancellationToken.None);

        await Assert.That(result.machineId).IsEqualTo(202L);
        await Assert.That(result.apiKey).IsEqualTo("api-key-fail-test");
        await Assert.That(result.errorMessage).IsEqualTo(string.Empty);
    }

    private static async Task SeedTierFeatureLimitsAsync(TestDatabaseFactory dbFactory)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await dbFactory.Context.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Free,
            MachineLimit = 3,
            RetentionDays = 1,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            UpdatedAt = now,
        });

        await dbFactory.Context.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Pro,
            MachineLimit = 1000,
            RetentionDays = 60,
            AlertRuleLimit = 10,
            WebhookLimit = 5,
            UpdatedAt = now,
        });

        await dbFactory.Context.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Team,
            MachineLimit = 10000,
            RetentionDays = 365,
            AlertRuleLimit = 25,
            WebhookLimit = 15,
            UpdatedAt = now,
        });
    }
}
