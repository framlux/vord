// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using System.Text.Encodings.Web;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="ApiKeyAuthenticationHandler"/>.
/// </summary>
public class ApiKeyAuthenticationHandlerTests
{
    private static (IConnectionMultiplexer redis, IDatabase redisDb) CreateRedisMock()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);

        return (redis, redisDb);
    }

    private static async Task<AuthenticateResult> RunHandlerAsync(IMachineRepository machineRepository, string? apiKeyHeader)
    {
        (IConnectionMultiplexer redis, _) = CreateRedisMock();

        return await RunHandlerAsync(machineRepository, apiKeyHeader, redis);
    }

    private static async Task<AuthenticateResult> RunHandlerAsync(
        IMachineRepository machineRepository,
        string? apiKeyHeader,
        IConnectionMultiplexer redis)
    {
        IOptionsMonitor<AuthenticationSchemeOptions> options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        ILoggerFactory loggerFactory = new NullLoggerFactory();
        UrlEncoder encoder = UrlEncoder.Default;

        ApiKeyAuthenticationHandler handler = new(options, loggerFactory, encoder, machineRepository, redis);

        DefaultHttpContext httpContext = new();
        if (apiKeyHeader is not null)
        {
            httpContext.Request.Headers["x-api-key"] = apiKeyHeader;
        }

        AuthenticationScheme scheme = new(ApiKeyAuthenticationHandler.SchemeName, null, typeof(ApiKeyAuthenticationHandler));
        await handler.InitializeAsync(scheme, httpContext);

        return await handler.AuthenticateAsync();
    }

    private static ApiKeyAuthenticationHandler CreateHandler(IConnectionMultiplexer redis)
    {
        IOptionsMonitor<AuthenticationSchemeOptions> options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        ILoggerFactory loggerFactory = new NullLoggerFactory();
        UrlEncoder encoder = UrlEncoder.Default;
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();

        return new ApiKeyAuthenticationHandler(options, loggerFactory, encoder, machineRepo, redis);
    }

    [Test]
    public async Task NoHeader_ReturnsFail()
    {
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();

        AuthenticateResult result = await RunHandlerAsync(machineRepo, null);

        await Assert.That(result.Succeeded).IsFalse();
    }

    [Test]
    public async Task InvalidKey_ReturnsFail()
    {
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineByApiKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Machine?)null);

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "invalid-key");

        await Assert.That(result.Succeeded).IsFalse();
    }

    [Test]
    public async Task ValidKey_ReturnsSuccess()
    {
        Machine machine = new()
        {
            Id = 42,
            Name = "test-machine",
            ApiKeyHash = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            SerialNumber = "SN-TEST",
            SystemId = "SID-TEST",
            MachineType = Database.Enums.MachineTypes.BareMetalServer,
            OperatingSystem = Database.Enums.OperatingSystems.Ubuntu,
            RegistrationTokenId = 1,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = 1
        };
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineByApiKeyAsync("valid-key", Arg.Any<CancellationToken>())
            .Returns(machine);

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "valid-key");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Principal!.FindFirst("MachineId")!.Value).IsEqualTo("42");
    }

    [Test]
    public async Task ValidKey_NotYetAccepted_RecordsAcceptance()
    {
        // First successful authentication is the only proof the server gets that the agent received
        // and persisted its key. Without this stamp the re-issue guard in GetRegistrationStatusAsync
        // never fires and a live machine can be re-keyed out from under its agent.
        Machine machine = BuildMachine(keyDeliveredAt: null);
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineByApiKeyAsync("valid-key", Arg.Any<CancellationToken>())
            .Returns(machine);

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "valid-key");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(machine.KeyDeliveredAt).IsNull();
        await machineRepo.Received(1).MarkKeyDeliveredAsync(42, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidKey_AlreadyAccepted_DoesNotWriteAgain()
    {
        // Acceptance is once per key. Re-stamping on every cache miss would put a pointless write on
        // the authentication path for the entire life of the fleet.
        Machine machine = BuildMachine(keyDeliveredAt: DateTimeOffset.UtcNow);
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineByApiKeyAsync("valid-key", Arg.Any<CancellationToken>())
            .Returns(machine);

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "valid-key");

        await Assert.That(result.Succeeded).IsTrue();
        await machineRepo.DidNotReceive().MarkKeyDeliveredAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidKey_AcceptanceStampFails_StillAuthenticates()
    {
        // Recording acceptance is best-effort: a database fault must never reject a valid agent.
        // The machine simply stays eligible for a re-issue until its next authentication.
        Machine machine = BuildMachine(keyDeliveredAt: null);
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineByApiKeyAsync("valid-key", Arg.Any<CancellationToken>())
            .Returns(machine);
        machineRepo.MarkKeyDeliveredAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("database unavailable"));

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "valid-key");

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Principal!.FindFirst("MachineId")!.Value).IsEqualTo("42");
    }

    private static Machine BuildMachine(DateTimeOffset? keyDeliveredAt)
    {
        return new Machine
        {
            Id = 42,
            Name = "test-machine",
            ApiKeyHash = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            SerialNumber = "SN-TEST",
            SystemId = "SID-TEST",
            MachineType = Database.Enums.MachineTypes.BareMetalServer,
            OperatingSystem = Database.Enums.OperatingSystems.Ubuntu,
            RegistrationTokenId = 1,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = 1,
            KeyDeliveredAt = keyDeliveredAt,
        };
    }

    [Test]
    public async Task ValidKey_IncludesTenantIdClaim()
    {
        Machine machine = new()
        {
            Id = 42,
            Name = "test-machine",
            ApiKeyHash = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            SerialNumber = "SN-TEST",
            SystemId = "SID-TEST",
            MachineType = Database.Enums.MachineTypes.BareMetalServer,
            OperatingSystem = Database.Enums.OperatingSystems.Ubuntu,
            RegistrationTokenId = 1,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = 7
        };
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineByApiKeyAsync("valid-key", Arg.Any<CancellationToken>())
            .Returns(machine);

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "valid-key");

        await Assert.That(result.Principal!.FindFirst("TenantId")!.Value).IsEqualTo("7");
    }

    [Test]
    public async Task EmptyHeaderValue_ReturnsFail()
    {
        // Header present but with empty string value
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "");

        await Assert.That(result.Succeeded).IsFalse();
    }

    [Test]
    public async Task InvalidKey_DoesNotLookUpDatabase_WhenHeaderEmpty()
    {
        // When the header is empty, the handler should fail before querying the database
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();

        await RunHandlerAsync(machineRepo, "");

        await machineRepo.DidNotReceive().GetMachineByApiKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidKey_AuthenticationSchemeIsApiKey()
    {
        Machine machine = new()
        {
            Id = 1,
            Name = "test-machine",
            ApiKeyHash = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            SerialNumber = "SN-TEST",
            SystemId = "SID-TEST",
            MachineType = Database.Enums.MachineTypes.BareMetalServer,
            OperatingSystem = Database.Enums.OperatingSystems.Ubuntu,
            RegistrationTokenId = 1,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = 1
        };
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineByApiKeyAsync("key", Arg.Any<CancellationToken>())
            .Returns(machine);

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "key");

        // The scheme name should be set on the identity so authorization policies can match it
        await Assert.That(result.Principal!.Identity!.AuthenticationType).IsEqualTo(ApiKeyAuthenticationHandler.SchemeName);
    }

    [Test]
    public async Task CacheHit_ReturnsCachedResult_WithoutDatabaseLookup()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        // Simulate a cache hit with machineId=42, tenantId=7
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns((RedisValue)"42:7");

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "cached-key", redis);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Principal!.FindFirst("MachineId")!.Value).IsEqualTo("42");
        await Assert.That(result.Principal!.FindFirst("TenantId")!.Value).IsEqualTo("7");
        // Should not query the database when cache hit
        await machineRepo.DidNotReceive().GetMachineByApiKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CacheMiss_FallsThroughToDatabase()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        // Cache returns null
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(RedisValue.Null);

        Machine machine = new()
        {
            Id = 10,
            Name = "db-machine",
            ApiKeyHash = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            SerialNumber = "SN-DB",
            SystemId = "SID-DB",
            MachineType = Database.Enums.MachineTypes.BareMetalServer,
            OperatingSystem = Database.Enums.OperatingSystems.Ubuntu,
            RegistrationTokenId = 1,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = 3
        };
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineByApiKeyAsync("uncached-key", Arg.Any<CancellationToken>())
            .Returns(machine);

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "uncached-key", redis);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Principal!.FindFirst("MachineId")!.Value).IsEqualTo("10");
        await Assert.That(result.Principal!.FindFirst("TenantId")!.Value).IsEqualTo("3");
    }

    [Test]
    public async Task CacheMiss_ValidKey_CachesResultInRedis()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(RedisValue.Null);

        Machine machine = new()
        {
            Id = 10,
            Name = "db-machine",
            ApiKeyHash = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            SerialNumber = "SN-DB",
            SystemId = "SID-DB",
            MachineType = Database.Enums.MachineTypes.BareMetalServer,
            OperatingSystem = Database.Enums.OperatingSystems.Ubuntu,
            RegistrationTokenId = 1,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = 3
        };
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineByApiKeyAsync("new-key", Arg.Any<CancellationToken>())
            .Returns(machine);

        await RunHandlerAsync(machineRepo, "new-key", redis);

        // Verify the key was cached
        IEnumerable<NSubstitute.Core.ICall> calls = redisDb.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "StringSetAsync");
        await Assert.That(calls.Count()).IsGreaterThanOrEqualTo(1);

        NSubstitute.Core.ICall setCall = calls.First();
        object?[] args = setCall.GetArguments();
        string expectedHash = ApiKeyAuthenticationHandler.ComputeKeyHash("new-key");
        await Assert.That(args[0]!.ToString()).IsEqualTo("apikey:" + expectedHash);
        await Assert.That(args[1]!.ToString()).IsEqualTo("10:3");
    }

    [Test]
    public async Task MalformedCacheValue_FallsThroughToDatabase()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        // Malformed cache entry — not in "machineId:tenantId" format
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns((RedisValue)"malformed-data");

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineByApiKeyAsync("bad-cache-key", Arg.Any<CancellationToken>())
            .Returns((Machine?)null);

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "bad-cache-key", redis);

        await Assert.That(result.Succeeded).IsFalse();
        // Should fall through to database when cache value is malformed
        await machineRepo.Received(1).GetMachineByApiKeyAsync("bad-cache-key", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CacheValue_WithOnlyOnePart_FallsThroughToDatabase()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        // Only one value, missing the tenantId portion
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns((RedisValue)"42");

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineByApiKeyAsync("one-part-key", Arg.Any<CancellationToken>())
            .Returns((Machine?)null);

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "one-part-key", redis);

        await Assert.That(result.Succeeded).IsFalse();
        await machineRepo.Received(1).GetMachineByApiKeyAsync("one-part-key", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CacheValue_WithNonNumericParts_FallsThroughToDatabase()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns((RedisValue)"abc:def");

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineByApiKeyAsync("non-numeric-key", Arg.Any<CancellationToken>())
            .Returns((Machine?)null);

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "non-numeric-key", redis);

        await Assert.That(result.Succeeded).IsFalse();
        await machineRepo.Received(1).GetMachineByApiKeyAsync("non-numeric-key", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RedisGetException_FallsThroughToDatabase()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<RedisValue>(callInfo => throw new RedisException("Connection lost"));

        Machine machine = new()
        {
            Id = 5,
            Name = "fallback-machine",
            ApiKeyHash = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            SerialNumber = "SN-FB",
            SystemId = "SID-FB",
            MachineType = Database.Enums.MachineTypes.BareMetalServer,
            OperatingSystem = Database.Enums.OperatingSystems.Ubuntu,
            RegistrationTokenId = 1,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = 2
        };
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineByApiKeyAsync("redis-fail-key", Arg.Any<CancellationToken>())
            .Returns(machine);

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "redis-fail-key", redis);

        // Should succeed via database fallback even when Redis fails
        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(result.Principal!.FindFirst("MachineId")!.Value).IsEqualTo("5");
    }

    [Test]
    public async Task RedisSetException_DoesNotPreventAuthentication()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(RedisValue.Null);
        // Cache write fails
        redisDb.StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
            Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns<bool>(callInfo => throw new RedisException("Write failed"));

        Machine machine = new()
        {
            Id = 8,
            Name = "write-fail-machine",
            ApiKeyHash = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            SerialNumber = "SN-WF",
            SystemId = "SID-WF",
            MachineType = Database.Enums.MachineTypes.BareMetalServer,
            OperatingSystem = Database.Enums.OperatingSystems.Ubuntu,
            RegistrationTokenId = 1,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = 4
        };
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetMachineByApiKeyAsync("write-fail-key", Arg.Any<CancellationToken>())
            .Returns(machine);

        AuthenticateResult result = await RunHandlerAsync(machineRepo, "write-fail-key", redis);

        // Auth should succeed even if Redis cache write fails
        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task InvalidateCachedKeyAsync_DeletesCorrectRedisKey()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        ApiKeyAuthenticationHandler handler = CreateHandler(redis);

        await handler.InvalidateCachedKeyAsync("some-api-key");

        string expectedHash = ApiKeyAuthenticationHandler.ComputeKeyHash("some-api-key");
        await redisDb.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "apikey:" + expectedHash),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task InvalidateCachedKeyAsync_RedisException_DoesNotThrow()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        redisDb.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<bool>(callInfo => throw new RedisException("Delete failed"));

        ApiKeyAuthenticationHandler handler = CreateHandler(redis);

        // Should not throw — error is logged and swallowed
        await handler.InvalidateCachedKeyAsync("failing-key");
    }

    [Test]
    public async Task InvalidateCachedKeyByHashAsync_DeletesKeyedByHash()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        ApiKeyAuthenticationHandler handler = CreateHandler(redis);

        // The stored Machine.ApiKeyHash IS the hash the cache key is built from, so passing it
        // through must delete exactly the same Redis key the write path produced.
        string keyHash = ApiKeyAuthenticationHandler.ComputeKeyHash("some-api-key");

        await handler.InvalidateCachedKeyByHashAsync(keyHash);

        await redisDb.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "apikey:" + keyHash),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task InvalidateCachedKeyAsync_DelegatesToHashOverload_DeletingSameKey()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        ApiKeyAuthenticationHandler handler = CreateHandler(redis);

        // Plaintext invalidation must target the same Redis key as the hash a soft-delete would pass.
        await handler.InvalidateCachedKeyAsync("some-api-key");

        string expectedHash = ApiKeyAuthenticationHandler.ComputeKeyHash("some-api-key");
        await redisDb.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "apikey:" + expectedHash),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task InvalidateCachedKeyByHashAsync_RedisException_DoesNotThrow()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        redisDb.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<bool>(callInfo => throw new RedisException("Delete failed"));

        ApiKeyAuthenticationHandler handler = CreateHandler(redis);

        // Should not throw — error is logged and swallowed
        await handler.InvalidateCachedKeyByHashAsync("deadbeef");
    }

    [Test]
    public async Task InvalidateCachedKeyByHashAsync_NullOrWhitespace_Throws()
    {
        (IConnectionMultiplexer redis, _) = CreateRedisMock();
        ApiKeyAuthenticationHandler handler = CreateHandler(redis);

        await Assert.That(async () => await handler.InvalidateCachedKeyByHashAsync(null!))
            .Throws<ArgumentException>();
        await Assert.That(async () => await handler.InvalidateCachedKeyByHashAsync("   "))
            .Throws<ArgumentException>();
    }

}
