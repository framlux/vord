// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using System.Text.Encodings.Web;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// C3 regression tests: confirms that <see cref="ApiKeyAuthenticationHandler"/> never writes the
/// raw plaintext API key into Redis (Redis snapshots, MONITOR, or backups cannot extract a live
/// agent credential). The cache key is the SHA-256 hex hash of the token.
/// </summary>
public sealed class ApiKeyCacheKeyHashingTests
{
    private const string Sample = "vord_agent_eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.payload.signature";

    private static (IConnectionMultiplexer redis, IDatabase redisDb) CreateRedisMock()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        return (redis, redisDb);
    }

    private static async Task RunHandlerAsync(IMachineRepository machineRepository, string apiKey, IConnectionMultiplexer redis)
    {
        IOptionsMonitor<AuthenticationSchemeOptions> options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        ApiKeyAuthenticationHandler handler = new(options, new NullLoggerFactory(), UrlEncoder.Default, machineRepository, redis);
        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers["x-api-key"] = apiKey;
        AuthenticationScheme scheme = new(ApiKeyAuthenticationHandler.SchemeName, null, typeof(ApiKeyAuthenticationHandler));
        await handler.InitializeAsync(scheme, httpContext);
        await handler.AuthenticateAsync();
    }

    /// <summary>
    /// ComputeKeyHash must produce a stable 64-character lower-case hex SHA-256 digest of the
    /// UTF-8 bytes of the input.
    /// </summary>
    [Test]
    public async Task ComputeKeyHash_ProducesStable64CharLowercaseHex()
    {
        string hash = ApiKeyAuthenticationHandler.ComputeKeyHash("abc");

        await Assert.That(hash.Length).IsEqualTo(64);
        await Assert.That(hash).IsEqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    /// <summary>
    /// Different inputs must produce different hashes.
    /// </summary>
    [Test]
    public async Task ComputeKeyHash_DifferentInputs_DifferentHashes()
    {
        string h1 = ApiKeyAuthenticationHandler.ComputeKeyHash("key-one");
        string h2 = ApiKeyAuthenticationHandler.ComputeKeyHash("key-two");

        await Assert.That(h1).IsNotEqualTo(h2);
    }

    /// <summary>
    /// ComputeKeyHash with null throws — defensive validation.
    /// </summary>
    [Test]
    public async Task ComputeKeyHash_NullInput_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            ApiKeyAuthenticationHandler.ComputeKeyHash(null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    /// <summary>
    /// Regression: Redis StringGetAsync must be invoked with the hashed cache key, never the raw
    /// API key. This is the cache-read path.
    /// </summary>
    [Test]
    public async Task CacheRead_UsesHashedKey_NotPlaintext()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        IMachineRepository repo = Substitute.For<IMachineRepository>();
        repo.GetMachineByApiKeyAsync(Sample, Arg.Any<CancellationToken>()).Returns((Machine?)null);

        await RunHandlerAsync(repo, Sample, redis);

        string expectedHash = ApiKeyAuthenticationHandler.ComputeKeyHash(Sample);
        await redisDb.Received(1).StringGetAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "apikey:" + expectedHash),
            Arg.Any<CommandFlags>());
    }

    /// <summary>
    /// Regression: every Redis call recorded across cache read AND cache write must NOT contain
    /// the raw plaintext API key anywhere in its key or value.
    /// </summary>
    [Test]
    public async Task Plaintext_NeverAppearsInAnyRedisCall()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        Machine machine = new()
        {
            Id = 99,
            Name = "test",
            ApiKeyHash = new string('a', 64),
            SerialNumber = "SN",
            SystemId = "SID",
            MachineType = Database.Enums.MachineTypes.BareMetalServer,
            OperatingSystem = Database.Enums.OperatingSystems.Ubuntu,
            RegistrationTokenId = 1,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = 1,
        };
        IMachineRepository repo = Substitute.For<IMachineRepository>();
        repo.GetMachineByApiKeyAsync(Sample, Arg.Any<CancellationToken>()).Returns(machine);

        await RunHandlerAsync(repo, Sample, redis);

        foreach (NSubstitute.Core.ICall call in redisDb.ReceivedCalls())
        {
            foreach (object? arg in call.GetArguments())
            {
                if (arg is null)
                {
                    continue;
                }
                string asString = arg.ToString() ?? string.Empty;
                await Assert.That(asString.Contains(Sample, StringComparison.Ordinal)).IsFalse();
            }
        }
    }

    /// <summary>
    /// Invalidation must also hash the key.
    /// </summary>
    [Test]
    public async Task InvalidateCachedKeyAsync_UsesHashedKey()
    {
        (IConnectionMultiplexer redis, IDatabase redisDb) = CreateRedisMock();
        IOptionsMonitor<AuthenticationSchemeOptions> options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        ApiKeyAuthenticationHandler handler = new(options, new NullLoggerFactory(), UrlEncoder.Default, Substitute.For<IMachineRepository>(), redis);

        await handler.InvalidateCachedKeyAsync(Sample);

        string expectedHash = ApiKeyAuthenticationHandler.ComputeKeyHash(Sample);
        await redisDb.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "apikey:" + expectedHash),
            Arg.Any<CommandFlags>());
    }
}
