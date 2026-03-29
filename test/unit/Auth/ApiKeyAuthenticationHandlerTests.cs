// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Text.Encodings.Web;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="ApiKeyAuthenticationHandler"/>.
/// </summary>
public class ApiKeyAuthenticationHandlerTests
{
    private static async Task<AuthenticateResult> RunHandlerAsync(IDatabaseCache dbCache, string? apiKeyHeader)
    {
        IOptionsMonitor<AuthenticationSchemeOptions> options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        ILoggerFactory loggerFactory = new NullLoggerFactory();
        UrlEncoder encoder = UrlEncoder.Default;

        ApiKeyAuthenticationHandler handler = new(options, loggerFactory, encoder, dbCache);

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
        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();

        AuthenticateResult result = await RunHandlerAsync(dbCache, null);

        await Assert.That(result.Succeeded).IsEqualTo(false);
    }

    [Test]
    public async Task InvalidKey_ReturnsFail()
    {
        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();
        dbCache.GetMachineByApiKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Machine?)null);

        AuthenticateResult result = await RunHandlerAsync(dbCache, "invalid-key");

        await Assert.That(result.Succeeded).IsEqualTo(false);
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
        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();
        dbCache.GetMachineByApiKeyAsync("valid-key", Arg.Any<CancellationToken>())
            .Returns(machine);

        AuthenticateResult result = await RunHandlerAsync(dbCache, "valid-key");

        await Assert.That(result.Succeeded).IsEqualTo(true);
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
        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();
        dbCache.GetMachineByApiKeyAsync("valid-key", Arg.Any<CancellationToken>())
            .Returns(machine);

        AuthenticateResult result = await RunHandlerAsync(dbCache, "valid-key");

        await Assert.That(result.Principal!.FindFirst("TenantId")!.Value).IsEqualTo("7");
    }

    [Test]
    public async Task EmptyHeaderValue_ReturnsFail()
    {
        // Header present but with empty string value
        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();

        AuthenticateResult result = await RunHandlerAsync(dbCache, "");

        await Assert.That(result.Succeeded).IsEqualTo(false);
    }

    [Test]
    public async Task InvalidKey_DoesNotLookUpDatabase_WhenHeaderEmpty()
    {
        // When the header is empty, the handler should fail before querying the database
        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();

        await RunHandlerAsync(dbCache, "");

        await dbCache.DidNotReceive().GetMachineByApiKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();
        dbCache.GetMachineByApiKeyAsync("key", Arg.Any<CancellationToken>())
            .Returns(machine);

        AuthenticateResult result = await RunHandlerAsync(dbCache, "key");

        // The scheme name should be set on the identity so authorization policies can match it
        await Assert.That(result.Principal!.Identity!.AuthenticationType).IsEqualTo(ApiKeyAuthenticationHandler.SchemeName);
    }
}
