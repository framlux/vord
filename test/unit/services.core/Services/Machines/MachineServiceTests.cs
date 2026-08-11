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
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="MachineService"/>.
/// </summary>
public class MachineServiceTests
{
    private const string TestTokenValue = "test-reg-token";

    /// <summary>
    /// Fixed deterministic instant used by tests that do not otherwise inject their own clock,
    /// so seeded token timestamps never depend on wall-clock time.
    /// </summary>
    private static readonly DateTimeOffset FixedNow = new(2026, 06, 26, 12, 0, 0, TimeSpan.Zero);

    private static string ComputeTokenHash(string token)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static async Task<RegistrationToken> SeedValidRegistrationToken(TestDatabaseFactory dbFactory, DateTimeOffset now, int tenantId = 1)
    {
        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = ComputeTokenHash(TestTokenValue),
            Name = "Test Token",
            CreatedByUserId = 1,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7),
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
                    CreatedAt = FixedNow,
                    UpdatedAt = FixedNow,
                });
            defaultSubService.GetEffectiveLimitsForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new EffectiveLimits { MachineLimit = 3, RetentionDays = 1 });
            subscriptionService = defaultSubService;
        }

        services[typeof(ISubscriptionService)] = subscriptionService;

        return new TestServiceScopeFactory(dbFactory.Context, services);
    }

    /// <summary>
    /// Builds a <see cref="MachineService"/> with sensible defaults for every dependency except
    /// <paramref name="scopeFactory"/>, which is always test-specific. The default redis substitute
    /// stubs <c>GetDatabase</c> so callers that do not care about Redis behavior still get a usable
    /// <see cref="IDatabase"/>; tests that assert on Redis calls pass their own <paramref name="redis"/>.
    /// </summary>
    private static MachineService BuildService(
        IServiceScopeFactory scopeFactory,
        IConnectionMultiplexer? redis = null,
        IBillingApiClient? billingApiClient = null,
        IDataProtectionProvider? dataProtectionProvider = null,
        TimeProvider? timeProvider = null,
        IApiKeyCacheInvalidator? apiKeyCacheInvalidator = null,
        ILogger<MachineService>? logger = null)
    {
        return new MachineService(
            scopeFactory,
            logger ?? new NullLogger<MachineService>(),
            redis ?? CreateDefaultRedis(),
            billingApiClient ?? Substitute.For<IBillingApiClient>(),
            dataProtectionProvider ?? new EphemeralDataProtectionProvider(),
            timeProvider ?? new FakeTimeProvider(FixedNow),
            apiKeyCacheInvalidator ?? Substitute.For<IApiKeyCacheInvalidator>());
    }

    private static IConnectionMultiplexer CreateDefaultRedis()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Substitute.For<IDatabase>());

        return redis;
    }

    // ========== GetRegistrationStatus tests ==========

    [Test]
    public async Task GetRegistrationStatus_EmptyToken_ReturnsUnknownRegistration()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineService service = BuildService(scopeFactory);

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
        await SeedValidRegistrationToken(dbFactory, FixedNow);
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineService service = BuildService(scopeFactory);

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
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        // The cached value is encrypted at rest; pre-protect with the same purpose used by
        // MachineService so the Unprotect path in the SUT returns the original plaintext.
        EphemeralDataProtectionProvider provider = new();
        IDataProtector seedingProtector = provider.CreateProtector("MachineService.PendingApiKey");
        string encryptedCachedKey = seedingProtector.Protect("test-api-key-plaintext");
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(encryptedCachedKey));
        redisDb.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(true);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineService service = BuildService(scopeFactory, redis: redis, dataProtectionProvider: provider);

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
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineService service = BuildService(scopeFactory);

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
        MachineService service = BuildService(scopeFactory);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync("SN-001", "SID-001", "invalid-token", true, CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.UnknownRegistration);
        await Assert.That(result.id).IsNull();
    }

    [Test]
    public async Task GetRegistrationStatus_NeedsApiKey_CacheExpired_ReissuesNewKey()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

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
            .Returns(("reissued-plaintext-key", "old-key-hash"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory, redis: redis);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, true, CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.RegistrationActive);
        await Assert.That(result.id).IsEqualTo(machine.Id);
        await Assert.That(result.apiKey).IsEqualTo("reissued-plaintext-key");
        await machineRepo.Received(1).ReissueApiKeyAsync(machine.Id, Arg.Any<CancellationToken>());
        // The reissued key is encrypted at rest in Redis (post-C3) and TTL is now 1 hour.
        await redisDb.Received(1).StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Is<RedisValue>(v => (v != "reissued-plaintext-key") && v.HasValue),
            Arg.Is<Expiration>(e => e.Equals(new Expiration(TimeSpan.FromHours(1)))));
    }

    [Test]
    public async Task GetRegistrationStatus_KeyAlreadyDelivered_RefusesReissueAndReportsUnknown()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        // KeyDeliveredAt is the one-time-delivery latch: null means the initial key was never
        // claimed, non-null means an agent picked it up and is authenticating with it. A second
        // caller presenting the same serial and system id must not be able to re-key it out from
        // under that agent. The null case deliberately still re-issues — that is what lets an agent
        // recover when the Redis entry was lost before it ever claimed its key — and is covered by
        // GetRegistrationStatus_NeedsApiKey_CacheExpired_ReissuesNewKey.
        Machine machine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        machine.KeyDeliveredAt = FixedNow;
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineBySerialAndSystemIdAsync(machine.SerialNumber, machine.SystemId, token.TenantId, Arg.Any<CancellationToken>())
            .Returns(machine);

        IApiKeyCacheInvalidator invalidator = Substitute.For<IApiKeyCacheInvalidator>();

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory, redis: redis, apiKeyCacheInvalidator: invalidator);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, true, CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.UnknownRegistration);
        await Assert.That(result.id).IsNull();
        await Assert.That(result.apiKey).IsNull();
        await machineRepo.DidNotReceive().ReissueApiKeyAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        // The incumbent agent's credentials must survive untouched — no reissue, so no invalidation.
        await invalidator.DidNotReceive().InvalidateByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetRegistrationStatus_Reissue_InvalidatesOldKeyAuthCache()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

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
        // The repo returns the hash of the key being replaced; the service must invalidate exactly it.
        machineRepo.ReissueApiKeyAsync(machine.Id, Arg.Any<CancellationToken>())
            .Returns(("reissued-plaintext-key", "deadbeefoldhash"));

        IApiKeyCacheInvalidator invalidator = Substitute.For<IApiKeyCacheInvalidator>();

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory, redis: redis, apiKeyCacheInvalidator: invalidator);

        await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, true, CancellationToken.None);

        await invalidator.Received(1).InvalidateByHashAsync("deadbeefoldhash", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetRegistrationStatus_ReissueReturnsNullOldHash_DoesNotInvalidate()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

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
        // A reissued key with no recoverable old hash must not trigger a stray invalidation.
        machineRepo.ReissueApiKeyAsync(machine.Id, Arg.Any<CancellationToken>())
            .Returns(("reissued-plaintext-key", (string?)null));

        IApiKeyCacheInvalidator invalidator = Substitute.For<IApiKeyCacheInvalidator>();

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory, redis: redis, apiKeyCacheInvalidator: invalidator);

        await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, true, CancellationToken.None);

        await invalidator.DidNotReceive().InvalidateByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetRegistrationStatus_NeedsApiKeyFalse_NoCachedKey_ReturnsNullKey()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));
        MachineService service = BuildService(scopeFactory, redis: redis);

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
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);
        await dbFactory.Context.RegistrationTokens
            .Where(t => t.Id == token.Id)
            .Set(t => t.IsRevoked, true)
            .Set(t => t.RevokedAt, FixedNow)
            .UpdateAsync();

        Machine machine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineService service = BuildService(scopeFactory);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, true, CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.UnknownRegistration);
        await Assert.That(result.apiKey).IsNull();
    }

    [Test]
    public async Task GetRegistrationStatus_ExpiredToken_NeedsApiKey_ReturnsUnknown()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);
        // Force the token to be expired in the past while leaving it un-revoked.
        await dbFactory.Context.RegistrationTokens
            .Where(t => t.Id == token.Id)
            .Set(t => t.ExpiresAt, FixedNow.AddDays(-1))
            .UpdateAsync();

        Machine machine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        MachineService service = BuildService(scopeFactory);

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
        MachineService service = BuildService(scopeFactory);

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
        MachineService service = BuildService(scopeFactory);

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
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);
        await dbFactory.Context.RegistrationTokens
            .Where(t => t.Id == token.Id)
            .Set(t => t.IsRevoked, true)
            .Set(t => t.RevokedAt, FixedNow)
            .UpdateAsync();

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory);

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
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);
        // Force the token to be expired while leaving it un-revoked.
        await dbFactory.Context.RegistrationTokens
            .Where(t => t.Id == token.Id)
            .Set(t => t.ExpiresAt, FixedNow.AddDays(-1))
            .UpdateAsync();

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory);

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
    public async Task RegisterSystem_ValidToken_ReturnsMachineIdAndApiKey()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow, tenantId: 5);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: 5, registrationTokenId: token.Id);
        createdMachine.Id = 100;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "plaintext-api-key-123"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory);

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
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow, tenantId: 5);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: 5, registrationTokenId: token.Id);
        createdMachine.Id = 100;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "plaintext-api-key-123"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory);

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
            Arg.Any<long>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_ExistingMachine_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedValidRegistrationToken(dbFactory, FixedNow);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory);

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

    // ========== RegisterSystem — single-use token validation (FakeTimeProvider) ==========

    private const string SingleUseTokenValue = "single-use-token";

    /// <summary>
    /// Seeds a registration token whose availability is expressed relative to the supplied
    /// <paramref name="now"/> so single-use validation can be exercised deterministically without
    /// wall-clock time. <paramref name="consumed"/> stamps ConsumedAt; <paramref name="revoked"/>
    /// marks it revoked; <paramref name="expired"/> dates ExpiresAt before <paramref name="now"/>.
    /// </summary>
    private static async Task SeedSingleUseToken(
        TestDatabaseFactory dbFactory,
        DateTimeOffset now,
        bool consumed = false,
        bool revoked = false,
        bool expired = false)
    {
        RegistrationToken token = new()
        {
            TenantId = 1,
            TokenHash = ComputeTokenHash(SingleUseTokenValue),
            Name = "Single Use Token",
            CreatedByUserId = 1,
            CreatedAt = now.AddDays(-1),
            ExpiresAt = expired ? now.AddMinutes(-1) : now.AddDays(7),
            IsRevoked = revoked,
            RevokedAt = revoked ? now : null,
            ConsumedAt = consumed ? now : null,
            ConsumedByMachineId = consumed ? 5L : null,
        };
        await dbFactory.Context.InsertWithInt64IdentityAsync(token);
    }

    private static RegisterSystemRequest BuildSingleUseRequest()
    {
        return new RegisterSystemRequest
        {
            SerialNumber = "SN-SINGLE",
            SystemId = "SID-SINGLE",
            Hostname = "single-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = SingleUseTokenValue,
        };
    }

    [Test]
    public async Task RegisterSystem_ConsumedToken_ReturnsAlreadyUsedError()
    {
        DateTimeOffset now = new(2026, 06, 26, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(now);

        using TestDatabaseFactory dbFactory = new();
        await SeedSingleUseToken(dbFactory, now, consumed: true);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory, timeProvider: timeProvider);

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(BuildSingleUseRequest(), CancellationToken.None);

        await Assert.That(result.machineId).IsNull();
        await Assert.That(result.apiKey).IsNull();
        await Assert.That(result.errorMessage).IsEqualTo("Registration token has already been used");

        await machineRepo.DidNotReceive().CreateMachineWithKeyAsync(
            Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_ExpiredToken_ViaTimeProvider_ReturnsExpiredError()
    {
        DateTimeOffset now = new(2026, 06, 26, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(now);

        using TestDatabaseFactory dbFactory = new();
        await SeedSingleUseToken(dbFactory, now, expired: true);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory, timeProvider: timeProvider);

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(BuildSingleUseRequest(), CancellationToken.None);

        await Assert.That(result.machineId).IsNull();
        await Assert.That(result.errorMessage).IsEqualTo("Registration token has expired");
    }

    [Test]
    public async Task RegisterSystem_RevokedToken_ViaTimeProvider_ReturnsRevokedError()
    {
        DateTimeOffset now = new(2026, 06, 26, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(now);

        using TestDatabaseFactory dbFactory = new();
        await SeedSingleUseToken(dbFactory, now, revoked: true);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory, timeProvider: timeProvider);

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(BuildSingleUseRequest(), CancellationToken.None);

        await Assert.That(result.machineId).IsNull();
        await Assert.That(result.errorMessage).IsEqualTo("Registration token has been revoked");
    }

    [Test]
    public async Task RegisterSystem_FreshSingleUseToken_PassesValidation_AndConsumesTokenId()
    {
        DateTimeOffset now = new(2026, 06, 26, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(now);

        using TestDatabaseFactory dbFactory = new();
        await SeedSingleUseToken(dbFactory, now);

        RegistrationToken seeded = await dbFactory.Context.RegistrationTokens
            .FirstAsync(t => t.TokenHash == ComputeTokenHash(SingleUseTokenValue));

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);
        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: 1, registrationTokenId: seeded.Id);
        createdMachine.Id = 777;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "fresh-key"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory, timeProvider: timeProvider);

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(BuildSingleUseRequest(), CancellationToken.None);

        await Assert.That(result.machineId).IsEqualTo(777L);
        await Assert.That(result.errorMessage).IsEqualTo(string.Empty);

        await machineRepo.Received(1).CreateMachineWithKeyAsync(
            Arg.Any<Machine>(),
            seeded.Id,
            now,
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_RaceLostToConsume_ReturnsAlreadyUsedError()
    {
        DateTimeOffset now = new(2026, 06, 26, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(now);

        using TestDatabaseFactory dbFactory = new();
        await SeedSingleUseToken(dbFactory, now);

        RegistrationToken seeded = await dbFactory.Context.RegistrationTokens
            .FirstAsync(t => t.TokenHash == ComputeTokenHash(SingleUseTokenValue));

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                dbFactory.Context.RegistrationTokens
                    .Where(t => t.Id == seeded.Id)
                    .Set(t => t.ConsumedAt, now)
                    .Set(t => t.ConsumedByMachineId, 9999L)
                    .Update();

                return ((Machine?)null, (string?)null);
            });

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory, timeProvider: timeProvider);

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(BuildSingleUseRequest(), CancellationToken.None);

        await Assert.That(result.machineId).IsNull();
        await Assert.That(result.errorMessage).IsEqualTo("Registration token has already been used");
    }

    // ========== UpdateQuantity called on machine registration ==========

    [Test]
    public async Task RegisterSystem_PaidTier_CallsUpdateQuantityWithCorrectCount()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow, tenantId: 1);

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
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "api-key-billing-test"));
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(tenant);

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(sub);
        subscriptionService.GetEffectiveLimitsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(
            new EffectiveLimits { MachineLimit = 1000, RetentionDays = 60, AlertRuleLimit = 10, WebhookLimit = 5 });
        subscriptionService.GetBillableMachineCountAsync(1, SubscriptionTier.Pro, Arg.Any<CancellationToken>())
            .Returns(7);

        Dictionary<Type, object> services = new()
        {
            { typeof(IMachineRepository), machineRepo },
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ISubscriptionService), subscriptionService },
        };
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context, services);
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        billingApiClient.UpdateQuantityAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);

        MachineService service = BuildService(scopeFactory, billingApiClient: billingApiClient);

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

        // Verify billing was called with the tenant external ID and the correct billable quantity
        await billingApiClient.Received(1).UpdateQuantityAsync(
            "ext-tenant-billing", 7, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_FreeTier_DoesNotCallUpdateQuantity()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow, tenantId: 1);

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
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
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
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();

        MachineService service = BuildService(scopeFactory, billingApiClient: billingApiClient);

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
        await billingApiClient.DidNotReceive().UpdateQuantityAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_BillingFailure_DoesNotPreventRegistration()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow, tenantId: 1);

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
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "api-key-fail-test"));
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(tenant);

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(sub);
        subscriptionService.GetEffectiveLimitsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(
            new EffectiveLimits { MachineLimit = 1000, RetentionDays = 60, AlertRuleLimit = 10, WebhookLimit = 5 });
        subscriptionService.GetBillableMachineCountAsync(1, SubscriptionTier.Pro, Arg.Any<CancellationToken>())
            .Returns(3);

        Dictionary<Type, object> services = new()
        {
            { typeof(IMachineRepository), machineRepo },
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ISubscriptionService), subscriptionService },
        };
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context, services);

        // Simulate billing client throwing an exception
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        billingApiClient.UpdateQuantityAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("Billing service unavailable"));

        MachineService service = BuildService(scopeFactory, billingApiClient: billingApiClient);

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

    // ========== GetRegistrationStatus — concurrent delivery and null key reissue branches ==========

    // ========== Regression: API key cache deleted before DB update (bug fix) ==========

    [Test]
    public async Task GetRegistrationStatus_CacheDeleteLost_DoesNotDeliverKey()
    {
        // The atomic Redis delete is the one-time-delivery arbiter: a caller that did not remove the
        // entry did not win the race and must not be handed the key. This replaced an ordering
        // invariant between KeyDeleteAsync and a database mark, which no longer applies now that
        // acceptance is recorded at authentication rather than during delivery.
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        EphemeralDataProtectionProvider provider = new();
        IDataProtector seedingProtector = provider.CreateProtector("MachineService.PendingApiKey");
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(seedingProtector.Protect("cached-plaintext-key")));

        // This caller read the entry but another caller removed it first.
        redisDb.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(false);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineBySerialAndSystemIdAsync(machine.SerialNumber, machine.SystemId, token.TenantId, Arg.Any<CancellationToken>())
            .Returns(machine);
        machineRepo.ReissueApiKeyAsync(machine.Id, Arg.Any<CancellationToken>())
            .Returns(("reissued-plaintext-key", "old-key-hash"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory, redis: redis, dataProtectionProvider: provider);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, true, CancellationToken.None);

        // The cached key must not be handed out; this machine has never been accepted, so the
        // caller legitimately falls through to a re-issue rather than receiving the contested key.
        await Assert.That(result.apiKey).IsNotEqualTo("cached-plaintext-key");
        await Assert.That(result.apiKey).IsEqualTo("reissued-plaintext-key");
    }

    [Test]
    public async Task GetRegistrationStatus_Delivery_DoesNotStampAcceptance()
    {
        // Acceptance is recorded when the agent authenticates, never during delivery. If delivery
        // stamped it, a response lost in flight would leave the machine looking accepted and the
        // re-issue guard would strand the agent permanently.
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        EphemeralDataProtectionProvider provider = new();
        IDataProtector seedingProtector = provider.CreateProtector("MachineService.PendingApiKey");
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(seedingProtector.Protect("cached-plaintext-key")));
        redisDb.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(true);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineBySerialAndSystemIdAsync(machine.SerialNumber, machine.SystemId, token.TenantId, Arg.Any<CancellationToken>())
            .Returns(machine);

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory, redis: redis, dataProtectionProvider: provider);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, true, CancellationToken.None);

        await Assert.That(result.apiKey).IsEqualTo("cached-plaintext-key");
        await machineRepo.DidNotReceive().MarkKeyDeliveredAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetRegistrationStatus_CachedKey_ConcurrentDelivery_LogsWarningAndReissuesKey()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        // Simulate cached key exists but concurrent delivery already happened (MarkKeyDelivered returns 0)
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>("cached-key-value"));

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineBySerialAndSystemIdAsync(machine.SerialNumber, machine.SystemId, token.TenantId, Arg.Any<CancellationToken>())
            .Returns(machine);
        machineRepo.MarkKeyDeliveredAsync(machine.Id, Arg.Any<CancellationToken>())
            .Returns(0); // Concurrent delivery — key already delivered
        machineRepo.ReissueApiKeyAsync(machine.Id, Arg.Any<CancellationToken>())
            .Returns(("reissued-after-concurrent", "old-key-hash"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory, redis: redis);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, true, CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.RegistrationActive);
        await Assert.That(result.id).IsEqualTo(machine.Id);
        // After concurrent delivery, the service falls through to ReissueApiKeyAsync
        await Assert.That(result.apiKey).IsEqualTo("reissued-after-concurrent");
        await machineRepo.Received(1).ReissueApiKeyAsync(machine.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetRegistrationStatus_NoCacheAndReissueReturnsNull_ReturnsNullApiKey()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

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
            .Returns(((string?)null, (string?)null));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory, redis: redis);

        (RegistrationStatus status, long? id, string? apiKey) result =
            await service.GetRegistrationStatusAsync(machine.SerialNumber, machine.SystemId, TestTokenValue, true, CancellationToken.None);

        await Assert.That(result.status).IsEqualTo(RegistrationStatus.RegistrationActive);
        await Assert.That(result.apiKey).IsNull();
    }

    // ========== RegisterSystem — OS and MachineType conversion branches ==========

    [Test]
    public async Task RegisterSystem_DesktopMachineType_ConvertsCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        createdMachine.Id = 301;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "desktop-key"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-DESKTOP",
            SystemId = "SID-DESKTOP",
            Hostname = "desktop-host",
            MachineType = Grpc.AgentRegistration.MachineType.DesktopType,
            Os = OperatingSystemType.FedoraOs,
            RegistrationToken = TestTokenValue,
        };

        await service.RegisterSystemAsync(request, CancellationToken.None);

        await machineRepo.Received(1).CreateMachineWithKeyAsync(
            Arg.Is<Machine>(m => m.MachineType == MachineTypes.Desktop && m.OperatingSystem == OperatingSystems.Fedora),
            Arg.Any<long>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_LaptopMachineType_ConvertsCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        createdMachine.Id = 302;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "laptop-key"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-LAPTOP",
            SystemId = "SID-LAPTOP",
            Hostname = "laptop-host",
            MachineType = Grpc.AgentRegistration.MachineType.LaptopType,
            Os = OperatingSystemType.MacOs,
            RegistrationToken = TestTokenValue,
        };

        await service.RegisterSystemAsync(request, CancellationToken.None);

        await machineRepo.Received(1).CreateMachineWithKeyAsync(
            Arg.Is<Machine>(m => m.MachineType == MachineTypes.Laptop && m.OperatingSystem == OperatingSystems.MacOS),
            Arg.Any<long>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_VirtualMachineType_ConvertsCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        createdMachine.Id = 303;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "vm-key"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-VM",
            SystemId = "SID-VM",
            Hostname = "vm-host",
            MachineType = Grpc.AgentRegistration.MachineType.VirtualMachineType,
            Os = OperatingSystemType.RedhatOs,
            RegistrationToken = TestTokenValue,
        };

        await service.RegisterSystemAsync(request, CancellationToken.None);

        await machineRepo.Received(1).CreateMachineWithKeyAsync(
            Arg.Is<Machine>(m => m.MachineType == MachineTypes.VirtualMachine && m.OperatingSystem == OperatingSystems.RedHat),
            Arg.Any<long>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_WindowsOs_ConvertsCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        createdMachine.Id = 304;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "win-key"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-WIN",
            SystemId = "SID-WIN",
            Hostname = "win-host",
            MachineType = Grpc.AgentRegistration.MachineType.UnknownType,
            Os = OperatingSystemType.WindowsOs,
            RegistrationToken = TestTokenValue,
        };

        await service.RegisterSystemAsync(request, CancellationToken.None);

        await machineRepo.Received(1).CreateMachineWithKeyAsync(
            Arg.Is<Machine>(m => m.MachineType == MachineTypes.Unknown && m.OperatingSystem == OperatingSystems.Windows),
            Arg.Any<long>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_DebianOs_ConvertsCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        createdMachine.Id = 305;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "debian-key"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-DEBIAN",
            SystemId = "SID-DEBIAN",
            Hostname = "debian-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.DebianOs,
            RegistrationToken = TestTokenValue,
        };

        await service.RegisterSystemAsync(request, CancellationToken.None);

        await machineRepo.Received(1).CreateMachineWithKeyAsync(
            Arg.Is<Machine>(m => m.MachineType == MachineTypes.BareMetalServer && m.OperatingSystem == OperatingSystems.Debian),
            Arg.Any<long>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_MachineLimit_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(((Machine?)null, (string?)null));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        MachineService service = BuildService(scopeFactory);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-LIMIT",
            SystemId = "SID-LIMIT",
            Hostname = "limit-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(request, CancellationToken.None);

        await Assert.That(result.machineId).IsNull();
        await Assert.That(result.apiKey).IsNull();
        await Assert.That(result.errorMessage).IsEqualTo("Machine limit exceeded");
    }

    [Test]
    public async Task RegisterSystem_PaidTier_TenantNotFound_SkipsBillingGracefully()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow, tenantId: 1);

        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: 1, registrationTokenId: token.Id);
        createdMachine.Id = 305;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "api-key-no-tenant"));

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns((Tenant?)null);

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
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = BuildService(scopeFactory, billingApiClient: billingApiClient);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-NOTENANT",
            SystemId = "SID-NOTENANT",
            Hostname = "notenant-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(request, CancellationToken.None);

        // Registration succeeds; billing is skipped when tenant is null
        await Assert.That(result.machineId).IsEqualTo(305L);
        await Assert.That(result.errorMessage).IsEqualTo(string.Empty);
        await billingApiClient.DidNotReceive().UpdateQuantityAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterSystem_NullSubscription_SkipsBilling()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow, tenantId: 1);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);

        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: 1, registrationTokenId: token.Id);
        createdMachine.Id = 306;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "api-key-nosub"));

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns((TenantSubscription?)null);
        subscriptionService.GetEffectiveLimitsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(
            new EffectiveLimits { MachineLimit = 3, RetentionDays = 1 });

        Dictionary<Type, object> services = new()
        {
            { typeof(IMachineRepository), machineRepo },
            { typeof(ISubscriptionService), subscriptionService },
        };
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context, services);
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        MachineService service = BuildService(scopeFactory, billingApiClient: billingApiClient);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-NOSUB",
            SystemId = "SID-NOSUB",
            Hostname = "nosub-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        (long? machineId, string? apiKey, string errorMessage) result =
            await service.RegisterSystemAsync(request, CancellationToken.None);

        await Assert.That(result.machineId).IsEqualTo(306L);
        await Assert.That(result.errorMessage).IsEqualTo(string.Empty);
        await billingApiClient.DidNotReceive().UpdateQuantityAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private static async Task SeedTierFeatureLimitsAsync(TestDatabaseFactory dbFactory)
    {
        DateTimeOffset now = FixedNow;

        await dbFactory.Context.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Free,
            MachineLimit = 3,
            RetentionDays = 1,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            MemberLimit = 1,
            MinimumBillableMachines = 0,
            UpdatedAt = now,
        });

        await dbFactory.Context.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Pro,
            MachineLimit = 1000,
            RetentionDays = 60,
            AlertRuleLimit = 10,
            WebhookLimit = 5,
            MemberLimit = 5,
            MinimumBillableMachines = 1,
            UpdatedAt = now,
        });

        await dbFactory.Context.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Team,
            MachineLimit = 10000,
            RetentionDays = 365,
            AlertRuleLimit = 25,
            WebhookLimit = 15,
            MemberLimit = int.MaxValue,
            MinimumBillableMachines = 3,
            UpdatedAt = now,
        });
    }

    // ==========================================================================================
    // C3 regression tests: pending API key encryption at rest in Redis.
    // ==========================================================================================

    /// <summary>
    /// On registration the plaintext key generated by the repo must be encrypted before it
    /// is written to Redis. The Redis value MUST differ from the plaintext key.
    /// </summary>
    [Test]
    public async Task RegisterSystem_PendingApiKey_StoredEncrypted_NotPlaintext()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        const string plaintext = "raw-secret-not-in-redis";
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);
        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        createdMachine.Id = 999;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, plaintext));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        MachineService service = BuildService(scopeFactory, redis: redis);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-C3",
            SystemId = "SID-C3",
            Hostname = "c3-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        await service.RegisterSystemAsync(request, CancellationToken.None);

        IEnumerable<NSubstitute.Core.ICall> setCalls = redisDb.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "StringSetAsync");
        await Assert.That(setCalls.Count()).IsGreaterThanOrEqualTo(1);
        NSubstitute.Core.ICall first = setCalls.First();
        object?[] args = first.GetArguments();
        string stored = args[1]!.ToString()!;
        await Assert.That(stored).IsNotEqualTo(plaintext);
    }

    /// <summary>
    /// The TTL passed to Redis must be one hour (the new shorter window — was 24 hours).
    /// </summary>
    [Test]
    public async Task RegisterSystem_PendingApiKey_TtlIsOneHour()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);
        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        createdMachine.Id = 1000;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, "k"));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        MachineService service = BuildService(scopeFactory, redis: redis);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-C3-TTL",
            SystemId = "SID-C3-TTL",
            Hostname = "c3-ttl-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        await service.RegisterSystemAsync(request, CancellationToken.None);

        await redisDb.Received(1).StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Is<Expiration>(e => e.Equals(new Expiration(TimeSpan.FromHours(1)))));
    }

    /// <summary>
    /// The protected blob written to Redis at registration time must round-trip back to the
    /// original plaintext when decrypted via the same data-protection key.
    /// </summary>
    [Test]
    public async Task RegisterSystem_PendingApiKey_RoundTripsThroughProtector()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = await SeedValidRegistrationToken(dbFactory, FixedNow);

        const string plaintext = "round-trip-secret";
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.DoesMachineExistAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);
        Machine createdMachine = TestDataBuilder.BuildMachine(tenantId: token.TenantId, registrationTokenId: token.Id);
        createdMachine.Id = 1001;
        machineRepo.CreateMachineWithKeyAsync(Arg.Any<Machine>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((createdMachine, plaintext));

        TestServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory, machineRepo);
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        EphemeralDataProtectionProvider provider = new();
        MachineService service = BuildService(scopeFactory, redis: redis, dataProtectionProvider: provider);

        RegisterSystemRequest request = new()
        {
            SerialNumber = "SN-C3-RT",
            SystemId = "SID-C3-RT",
            Hostname = "c3-rt-host",
            MachineType = Grpc.AgentRegistration.MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
            RegistrationToken = TestTokenValue,
        };

        await service.RegisterSystemAsync(request, CancellationToken.None);

        NSubstitute.Core.ICall setCall = redisDb.ReceivedCalls()
            .First(c => c.GetMethodInfo().Name == "StringSetAsync");
        string stored = setCall.GetArguments()[1]!.ToString()!;
        // Recreate a protector with the SAME purpose used by MachineService to decrypt.
        IDataProtector verifier = provider.CreateProtector("MachineService.PendingApiKey");
        string roundTripped = verifier.Unprotect(stored);
        await Assert.That(roundTripped).IsEqualTo(plaintext);
    }

    /// <summary>
    /// Constructor null-arg check for the new IDataProtectionProvider parameter.
    /// </summary>
    [Test]
    public async Task Constructor_NullDataProtectionProvider_ThrowsArgumentNullException()
    {
        TestServiceScopeFactory scopeFactory = new(new TestDatabaseFactory().Context);
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IBillingApiClient billing = Substitute.For<IBillingApiClient>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            MachineService _ = new(
                scopeFactory,
                new NullLogger<MachineService>(),
                redis,
                billing,
                null!,
                new FakeTimeProvider(FixedNow),
                Substitute.For<IApiKeyCacheInvalidator>());

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("dataProtectionProvider");
    }

    // ========== IsTokenExpired tests ==========

    [Test]
    public async Task IsTokenExpired_BeforeExpiry_ReturnsFalse()
    {
        DateTimeOffset now = new(2026, 06, 15, 12, 0, 0, TimeSpan.Zero);
        RegistrationToken token = BuildTokenWithExpiry(now.AddHours(1));

        bool expired = MachineService.IsTokenExpired(token, now);

        await Assert.That(expired).IsFalse();
    }

    [Test]
    public async Task IsTokenExpired_AtExpiry_ReturnsTrue()
    {
        DateTimeOffset now = new(2026, 06, 15, 12, 0, 0, TimeSpan.Zero);
        RegistrationToken token = BuildTokenWithExpiry(now);

        bool expired = MachineService.IsTokenExpired(token, now);

        await Assert.That(expired).IsTrue();
    }

    [Test]
    public async Task IsTokenExpired_AfterExpiry_ReturnsTrue()
    {
        DateTimeOffset now = new(2026, 06, 15, 12, 0, 0, TimeSpan.Zero);
        RegistrationToken token = BuildTokenWithExpiry(now.AddMinutes(-1));

        bool expired = MachineService.IsTokenExpired(token, now);

        await Assert.That(expired).IsTrue();
    }

    private static RegistrationToken BuildTokenWithExpiry(DateTimeOffset expiresAt)
    {
        return new RegistrationToken
        {
            Id = 1,
            TenantId = 1,
            TokenHash = new string('a', 64),
            Name = "token",
            CreatedByUserId = 1,
            CreatedAt = expiresAt.AddDays(-7),
            ExpiresAt = expiresAt,
            IsRevoked = false,
        };
    }
}
