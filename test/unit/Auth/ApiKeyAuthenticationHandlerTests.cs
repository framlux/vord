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
    private static async Task<AuthenticateResult> RunHandlerAsync(IMachineRepository machineRepository, string? apiKeyHeader)
    {
        IOptionsMonitor<AuthenticationSchemeOptions> options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        ILoggerFactory loggerFactory = new NullLoggerFactory();
        UrlEncoder encoder = UrlEncoder.Default;

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);

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
}
