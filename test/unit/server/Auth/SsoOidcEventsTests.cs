// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using StackExchange.Redis;
using OidcMessage = Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectMessage;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="SsoOidcEvents"/> URL safety validation.
/// </summary>
public sealed class SsoOidcEventsTests
{
    // --- IsConfigUsable Tests ---

    [Test]
    public async Task IsConfigUsable_DisabledConfig_ReturnsFalse()
    {
        TenantOidcConfiguration config = TestDataBuilder.BuildTenantOidcConfiguration(isEnabled: false);
        bool result = SsoOidcEvents.IsConfigUsable(config);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsConfigUsable_EnabledConfig_ReturnsTrue()
    {
        TenantOidcConfiguration config = TestDataBuilder.BuildTenantOidcConfiguration(isEnabled: true);
        bool result = SsoOidcEvents.IsConfigUsable(config);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsConfigUsable_NullConfig_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsConfigUsable(null);
        await Assert.That(result).IsFalse();
    }

    // --- IsUrlSafe Tests ---

    [Test]
    public async Task IsUrlSafe_ValidHttpsPublicUrl_ReturnsTrue()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://login.example.com/.well-known/openid-configuration");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsUrlSafe_HttpUrl_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("http://login.example.com/.well-known/openid-configuration");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_Localhost_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://localhost/.well-known/openid-configuration");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_LoopbackIp127_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://127.0.0.1/.well-known/openid-configuration");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_ZeroIp_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://0.0.0.0/.well-known/openid-configuration");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_Ipv6Loopback_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://[::1]/.well-known/openid-configuration");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_PrivateIp10_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://10.0.0.1/.well-known/openid-configuration");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_PrivateIp192_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://192.168.1.1/.well-known/openid-configuration");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_PrivateIp172_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://172.16.0.1/.well-known/openid-configuration");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_NullOrEmpty_ReturnsFalse()
    {
        bool resultEmpty = SsoOidcEvents.IsUrlSafe(string.Empty);

        await Assert.That(resultEmpty).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_MalformedUrl_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("not-a-url");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_PublicIpAddress_ReturnsTrue()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://8.8.8.8/endpoint");

        await Assert.That(result).IsTrue();
    }

    // --- IsUrlSafeAsync Tests ---

    [Test]
    public async Task IsUrlSafeAsync_HttpUrl_ReturnsFalse()
    {
        bool result = await SsoOidcEvents.IsUrlSafeAsync("http://example.com");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafeAsync_MalformedUrl_ReturnsFalse()
    {
        bool result = await SsoOidcEvents.IsUrlSafeAsync("not-a-url");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafeAsync_Localhost_ReturnsFalse()
    {
        bool result = await SsoOidcEvents.IsUrlSafeAsync("https://localhost/test");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafeAsync_PublicIpAddress_ReturnsTrue()
    {
        bool result = await SsoOidcEvents.IsUrlSafeAsync("https://8.8.8.8/endpoint");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsUrlSafeAsync_PrivateIp10_ReturnsFalse()
    {
        bool result = await SsoOidcEvents.IsUrlSafeAsync("https://10.0.0.1/test");

        await Assert.That(result).IsFalse();
    }

    // --- FetchDiscoveryDocumentAsync Tests ---

    [Test]
    public async Task FetchDiscoveryDocumentAsync_UsesMetadataAddressWhenProvided()
    {
        TenantOidcConfiguration config = TestDataBuilder.BuildTenantOidcConfiguration();
        config.MetadataAddress = "https://custom-idp.example.com/.well-known/openid-configuration";
        config.Authority = "https://ignored-authority.example.com";

        IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"issuer\":\"https://custom-idp.example.com\",\"authorization_endpoint\":\"https://custom-idp.example.com/authorize\",\"token_endpoint\":\"https://custom-idp.example.com/token\",\"jwks_uri\":\"https://custom-idp.example.com/jwks\"}")
        });
        HttpClient httpClient = new(handler);
        httpClientFactory.CreateClient("OidcDiscovery").Returns(httpClient);

        // The discovery document fetch should use MetadataAddress, not Authority.
        // ConfigurationManager may throw when parsing incomplete OIDC metadata (missing JWKS keys),
        // but we only need to verify the correct URL was requested.
        try
        {
            await SsoOidcEvents.FetchDiscoveryDocumentAsync(config, httpClientFactory, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // ConfigurationManager throws when JWKS fetch fails — expected with mock
        }

        await Assert.That(handler.Requests.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(handler.Requests[0].RequestUri?.ToString().Contains("custom-idp.example.com") ?? false).IsTrue();
    }

    [Test]
    public async Task FetchDiscoveryDocumentAsync_ConstructsDefaultWhenMetadataAddressNull()
    {
        TenantOidcConfiguration config = TestDataBuilder.BuildTenantOidcConfiguration();
        config.MetadataAddress = null;
        config.Authority = "https://idp.example.com/";

        IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"issuer\":\"https://idp.example.com\",\"authorization_endpoint\":\"https://idp.example.com/authorize\",\"token_endpoint\":\"https://idp.example.com/token\",\"jwks_uri\":\"https://idp.example.com/jwks\"}")
        });
        HttpClient httpClient = new(handler);
        httpClientFactory.CreateClient("OidcDiscovery").Returns(httpClient);

        // ConfigurationManager will throw when parsing incomplete OIDC metadata.
        // We verify the constructed default URL was used.
        try
        {
            await SsoOidcEvents.FetchDiscoveryDocumentAsync(config, httpClientFactory, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // ConfigurationManager throws when JWKS fetch fails — expected with mock
        }

        await Assert.That(handler.Requests.Count).IsGreaterThanOrEqualTo(1);
        string requestUrl = handler.Requests[0].RequestUri?.ToString() ?? string.Empty;
        await Assert.That(requestUrl).Contains("idp.example.com/.well-known/openid-configuration");
    }

    // --- Additional IsUrlSafe edge cases ---

    [Test]
    public async Task IsUrlSafe_Ipv4MappedIpv6_PrivateIp_ReturnsFalse()
    {
        // ::ffff:127.0.0.1 should be detected as private via the IPv4-mapped check
        bool result = SsoOidcEvents.IsUrlSafe("https://[::ffff:127.0.0.1]/endpoint");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_Ipv4MappedIpv6_PrivateIp10_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://[::ffff:10.0.0.1]/endpoint");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_NonAbsoluteUri_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("/relative/path");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_NullUrl_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe(null!);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_PrivateIp172_31_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://172.31.255.254/endpoint");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_ValidPublicHttps_ReturnsTrue()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://login.microsoftonline.com/tenant/v2.0/.well-known/openid-configuration");

        await Assert.That(result).IsTrue();
    }

    // --- Additional IsUrlSafeAsync edge cases ---

    [Test]
    public async Task IsUrlSafeAsync_DnsResolutionFails_ReturnsFalse()
    {
        // A non-existent domain should fail DNS resolution and return false
        bool result = await SsoOidcEvents.IsUrlSafeAsync("https://this-domain-does-not-exist-xyzzy123456.example.invalid/endpoint");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafeAsync_EmptyString_ReturnsFalse()
    {
        bool result = await SsoOidcEvents.IsUrlSafeAsync(string.Empty);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafeAsync_Ipv4MappedIpv6PrivateAddress_ReturnsFalse()
    {
        // ::ffff:10.0.0.1 resolved from DNS should be detected as private
        bool result = await SsoOidcEvents.IsUrlSafeAsync("https://[::ffff:10.0.0.1]/endpoint");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafeAsync_Ipv4MappedIpv6Loopback_ReturnsFalse()
    {
        bool result = await SsoOidcEvents.IsUrlSafeAsync("https://[::ffff:127.0.0.1]/endpoint");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_Ipv4MappedIpv6_PublicIp_ReturnsTrue()
    {
        // ::ffff:8.8.8.8 is a public IP mapped to IPv6 — should be allowed
        bool result = SsoOidcEvents.IsUrlSafe("https://[::ffff:8.8.8.8]/endpoint");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsUrlSafe_Ipv4MappedIpv6_192168_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://[::ffff:192.168.0.1]/endpoint");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_LinkLocalIp169254_ReturnsFalse()
    {
        // 169.254.x.x is link-local / cloud metadata — should be blocked
        bool result = SsoOidcEvents.IsUrlSafe("https://169.254.169.254/latest/meta-data/");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUrlSafe_FtpScheme_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("ftp://example.com/file");

        await Assert.That(result).IsFalse();
    }

    // --- FetchDiscoveryDocumentAsync Tests ---

    [Test]
    public async Task FetchDiscoveryDocumentAsync_UnsafeAddress_ThrowsInvalidOperation()
    {
        TenantOidcConfiguration config = TestDataBuilder.BuildTenantOidcConfiguration();
        config.MetadataAddress = "http://unsafe-idp.example.com/.well-known/openid-configuration";
        config.Authority = "http://unsafe-idp.example.com";

        IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await SsoOidcEvents.FetchDiscoveryDocumentAsync(config, httpClientFactory, CancellationToken.None);
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("disallowed");
    }

    // --- RedirectToIdentityProvider Tests ---

    /// <summary>
    /// Creates a <see cref="RedirectContext"/> with the supplied tenant ID and protocol message.
    /// </summary>
    private static RedirectContext BuildRedirectContext(
        string? tenantId,
        ITenantRepository? tenantRepo = null,
        IHttpClientFactory? httpClientFactory = null,
        ILogger<SsoOidcEvents>? logger = null)
    {
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ILogger<SsoOidcEvents>))
            .Returns(logger ?? Substitute.For<ILogger<SsoOidcEvents>>());
        if (tenantRepo is not null)
        {
            serviceProvider.GetService(typeof(ITenantRepository)).Returns(tenantRepo);
        }
        if (httpClientFactory is not null)
        {
            serviceProvider.GetService(typeof(IHttpClientFactory)).Returns(httpClientFactory);
        }

        DefaultHttpContext httpContext = new();
        httpContext.RequestServices = serviceProvider;

        OpenIdConnectOptions options = new();
        AuthenticationProperties properties = new();
        if (tenantId is not null)
        {
            properties.Items["tenantId"] = tenantId;
        }

        OidcMessage message = new();

        RedirectContext context = new(
            httpContext,
            new AuthenticationScheme("sso-oidc", "SSO OIDC", typeof(OpenIdConnectHandler)),
            options,
            properties)
        {
            ProtocolMessage = message
        };

        return context;
    }

    [Test]
    public async Task RedirectToIdentityProvider_MissingTenantId_Returns400()
    {
        SsoOidcEvents events = new();
        RedirectContext context = BuildRedirectContext(tenantId: null);

        await events.RedirectToIdentityProvider(context);

        await Assert.That(context.HttpContext.Response.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task RedirectToIdentityProvider_EmptyTenantId_Returns400()
    {
        SsoOidcEvents events = new();
        RedirectContext context = BuildRedirectContext(tenantId: "");

        await events.RedirectToIdentityProvider(context);

        await Assert.That(context.HttpContext.Response.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task RedirectToIdentityProvider_NonNumericTenantId_Returns400()
    {
        SsoOidcEvents events = new();
        RedirectContext context = BuildRedirectContext(tenantId: "abc");

        await events.RedirectToIdentityProvider(context);

        await Assert.That(context.HttpContext.Response.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task RedirectToIdentityProvider_OidcConfigNotFound_Returns400()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(null));

        SsoOidcEvents events = new();
        RedirectContext context = BuildRedirectContext(
            tenantId: "42",
            tenantRepo: tenantRepo);

        await events.RedirectToIdentityProvider(context);

        await Assert.That(context.HttpContext.Response.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task RedirectToIdentityProvider_DisabledConfig_Returns400()
    {
        TenantOidcConfiguration disabledConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 42, isEnabled: false);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(disabledConfig));

        SsoOidcEvents events = new();
        RedirectContext context = BuildRedirectContext(
            tenantId: "42",
            tenantRepo: tenantRepo);

        await events.RedirectToIdentityProvider(context);

        await Assert.That(context.HttpContext.Response.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task RedirectToIdentityProvider_EnabledConfig_PassesGuardAndProceeds()
    {
        TenantOidcConfiguration enabledConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 42, isEnabled: true);
        enabledConfig.MetadataAddress = "https://idp.example.com/.well-known/openid-configuration";

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(enabledConfig));

        // A usable config must pass the IsConfigUsable guard and advance to discovery; the mock
        // discovery handler returns a valid authorization endpoint so the redirect step is reached.
        IHttpClientFactory httpClientFactory = BuildMockHttpClientFactory(
            tokenEndpointUrl: "https://idp.example.com/token",
            tokenExchangeResponse: new HttpResponseMessage(HttpStatusCode.OK));

        SsoOidcEvents events = new();
        RedirectContext context = BuildRedirectContext(
            tenantId: "42",
            tenantRepo: tenantRepo,
            httpClientFactory: httpClientFactory);

        await events.RedirectToIdentityProvider(context);

        // The guard was passed: no 400 short-circuit, and the request advanced to the discovery step,
        // setting the issuer address and client id on the per-request protocol message.
        await Assert.That(context.HttpContext.Response.StatusCode).IsNotEqualTo(400);
        await Assert.That(context.ProtocolMessage.IssuerAddress).IsEqualTo("https://idp.example.com/authorize");
        await Assert.That(context.ProtocolMessage.ClientId).IsEqualTo(enabledConfig.ClientId);
    }

    // --- AuthorizationCodeReceived Tests ---

    /// <summary>
    /// Creates an <see cref="AuthorizationCodeReceivedContext"/> with the supplied tenant ID and auth code,
    /// wired to a mock <see cref="IServiceProvider"/> that returns the given services.
    /// </summary>
    private static AuthorizationCodeReceivedContext BuildCodeReceivedContext(
        string? tenantId,
        string authCode,
        ITenantRepository? tenantRepo = null,
        IHttpClientFactory? httpClientFactory = null,
        IOidcSecretProtector? secretProtector = null,
        ILogger<SsoOidcEvents>? logger = null,
        IConnectionMultiplexer? redis = null,
        IOptionsMonitor<OpenIdConnectOptions>? optionsMonitor = null,
        IDictionary<string, string>? cookies = null)
    {
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ILogger<SsoOidcEvents>))
            .Returns(logger ?? Substitute.For<ILogger<SsoOidcEvents>>());
        if (tenantRepo is not null)
        {
            serviceProvider.GetService(typeof(ITenantRepository)).Returns(tenantRepo);
        }
        if (httpClientFactory is not null)
        {
            serviceProvider.GetService(typeof(IHttpClientFactory)).Returns(httpClientFactory);
        }
        if (secretProtector is not null)
        {
            serviceProvider.GetService(typeof(IOidcSecretProtector)).Returns(secretProtector);
        }
        IConnectionMultiplexer redisConnection = redis ?? FakeRedisConnection.Create();
        serviceProvider.GetService(typeof(IConnectionMultiplexer)).Returns(redisConnection);
        IOptionsMonitor<OpenIdConnectOptions> resolvedOptionsMonitor =
            optionsMonitor ?? Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        serviceProvider.GetService(typeof(IOptionsMonitor<OpenIdConnectOptions>)).Returns(resolvedOptionsMonitor);

        DefaultHttpContext httpContext = new();
        httpContext.RequestServices = serviceProvider;
        if (cookies is not null && cookies.Count > 0)
        {
            string cookieHeader = string.Join("; ", cookies.Select(kvp => $"{kvp.Key}={kvp.Value}"));
            httpContext.Request.Headers.Cookie = cookieHeader;
        }

        OpenIdConnectOptions options = new();
        AuthenticationProperties properties = new();
        if (tenantId is not null)
        {
            properties.Items["tenantId"] = tenantId;
        }
        properties.Items[OpenIdConnectDefaults.RedirectUriForCodePropertiesKey] = "https://app.example.com/signin-oidc";

        OidcMessage message = new() { Code = authCode };

        AuthorizationCodeReceivedContext context = new(
            httpContext,
            new AuthenticationScheme("sso-oidc", "SSO OIDC", typeof(OpenIdConnectHandler)),
            options,
            properties)
        {
            ProtocolMessage = message
        };

        return context;
    }

    /// <summary>
    /// Builds an <see cref="IHttpClientFactory"/> that returns mock discovery and token exchange clients.
    /// The discovery client returns a valid OIDC discovery document with a token endpoint pointing to
    /// the supplied URL and a JWKS endpoint that returns an empty key set.
    /// </summary>
    private static IHttpClientFactory BuildMockHttpClientFactory(
        string tokenEndpointUrl,
        HttpResponseMessage tokenExchangeResponse)
    {
        string discoveryJson = JsonSerializer.Serialize(new
        {
            issuer = "https://idp.example.com",
            authorization_endpoint = "https://idp.example.com/authorize",
            token_endpoint = tokenEndpointUrl,
            jwks_uri = "https://idp.example.com/jwks",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
        });

        string jwksJson = JsonSerializer.Serialize(new { keys = Array.Empty<object>() });

        MockHttpMessageHandler discoveryHandler = new();
        discoveryHandler.WithDefaultResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(discoveryJson, System.Text.Encoding.UTF8, "application/json")
        });
        discoveryHandler.WithResponse("https://idp.example.com/jwks", new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jwksJson, System.Text.Encoding.UTF8, "application/json")
        });

        MockHttpMessageHandler tokenHandler = new();
        tokenHandler.WithDefaultResponse(tokenExchangeResponse);

        IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("OidcDiscovery").Returns(new HttpClient(discoveryHandler));
        httpClientFactory.CreateClient("OidcTokenExchange").Returns(new HttpClient(tokenHandler));

        return httpClientFactory;
    }

    [Test]
    public async Task AuthorizationCodeReceived_MissingTenantId_FailsWithMissingTenantMessage()
    {
        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: null,
            authCode: "test-code");

        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("Missing tenant ID");
    }

    [Test]
    public async Task AuthorizationCodeReceived_EmptyTenantId_FailsWithMissingTenantMessage()
    {
        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "",
            authCode: "test-code");

        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("Missing tenant ID");
    }

    [Test]
    public async Task AuthorizationCodeReceived_NonNumericTenantId_FailsWithMissingTenantMessage()
    {
        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "not-a-number",
            authCode: "test-code");

        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("Missing tenant ID");
    }

    [Test]
    public async Task AuthorizationCodeReceived_OidcConfigNotFound_FailsWithConfigNotFoundMessage()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(null));

        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "42",
            authCode: "test-code",
            tenantRepo: tenantRepo);

        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("Tenant OIDC configuration not found");
    }

    [Test]
    public async Task AuthorizationCodeReceived_DisabledConfig_FailsWithConfigNotFoundMessage()
    {
        TenantOidcConfiguration disabledConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 42, isEnabled: false);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(disabledConfig));

        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "42",
            authCode: "test-code",
            tenantRepo: tenantRepo);

        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("Tenant OIDC configuration not found");
    }

    [Test]
    public async Task AuthorizationCodeReceived_UnsafeTokenEndpoint_FailsWithDisallowedMessage()
    {
        TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 42);
        oidcConfig.MetadataAddress = "https://idp.example.com/.well-known/openid-configuration";

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(oidcConfig));

        // Discovery returns a token endpoint pointing to a private IP, which IsUrlSafe should reject
        IHttpClientFactory httpClientFactory = BuildMockHttpClientFactory(
            tokenEndpointUrl: "http://10.0.0.1/token",
            tokenExchangeResponse: new HttpResponseMessage(HttpStatusCode.OK));

        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "42",
            authCode: "test-code",
            tenantRepo: tenantRepo,
            httpClientFactory: httpClientFactory);

        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("disallowed destination");
    }

    [Test]
    public async Task AuthorizationCodeReceived_TokenExchangeNonSuccessStatus_FailsWithTokenExchangeMessage()
    {
        TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 42);
        oidcConfig.MetadataAddress = "https://idp.example.com/.well-known/openid-configuration";

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(oidcConfig));

        IOidcSecretProtector secretProtector = Substitute.For<IOidcSecretProtector>();
        secretProtector.IsProtected(Arg.Any<string?>()).Returns(true);
        secretProtector.Unprotect(Arg.Any<string>()).Returns("plain-secret");

        IHttpClientFactory httpClientFactory = BuildMockHttpClientFactory(
            tokenEndpointUrl: "https://idp.example.com/token",
            tokenExchangeResponse: new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"invalid_grant\"}")
            });

        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "42",
            authCode: "test-code",
            tenantRepo: tenantRepo,
            httpClientFactory: httpClientFactory,
            secretProtector: secretProtector);

        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("Token exchange failed");
    }

    [Test]
    public async Task AuthorizationCodeReceived_ProtectedClientSecret_UnprotectsBeforeTokenExchange()
    {
        TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 42);
        oidcConfig.MetadataAddress = "https://idp.example.com/.well-known/openid-configuration";

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(oidcConfig));

        // Mark the stored secret as encrypted so secret resolution takes the IsProtected -> Unprotect
        // path. An unprotected secret is rejected outright, so a protected secret is required to reach
        // the token exchange.
        IOidcSecretProtector secretProtector = Substitute.For<IOidcSecretProtector>();
        secretProtector.IsProtected(Arg.Any<string?>()).Returns(true);
        secretProtector.Unprotect(Arg.Any<string>()).Returns("decrypted-secret");

        // A non-success token exchange is sufficient: secret resolution happens before the token POST,
        // so the flow reaches Unprotect regardless of how the exchange itself resolves.
        IHttpClientFactory httpClientFactory = BuildMockHttpClientFactory(
            tokenEndpointUrl: "https://idp.example.com/token",
            tokenExchangeResponse: new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"invalid_grant\"}")
            });

        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "42",
            authCode: "test-code",
            tenantRepo: tenantRepo,
            httpClientFactory: httpClientFactory,
            secretProtector: secretProtector);

        await events.AuthorizationCodeReceived(context);

        secretProtector.Received(1).Unprotect(oidcConfig.ClientSecret);
    }

    [Test]
    public async Task AuthorizationCodeReceived_UnprotectedClientSecret_FailsContext()
    {
        TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 42);
        oidcConfig.MetadataAddress = "https://idp.example.com/.well-known/openid-configuration";

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(oidcConfig));

        // The stored secret is plaintext (not protected): the caller must fail the context rather than
        // throw into the pipeline, and must never reach the token exchange.
        IOidcSecretProtector secretProtector = Substitute.For<IOidcSecretProtector>();
        secretProtector.IsProtected(Arg.Any<string?>()).Returns(false);

        IHttpClientFactory httpClientFactory = BuildMockHttpClientFactory(
            tokenEndpointUrl: "https://idp.example.com/token",
            tokenExchangeResponse: new HttpResponseMessage(HttpStatusCode.OK));

        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "42",
            authCode: "test-code",
            tenantRepo: tenantRepo,
            httpClientFactory: httpClientFactory,
            secretProtector: secretProtector);

        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("not protected at rest");
        secretProtector.DidNotReceive().Unprotect(Arg.Any<string>());
    }

    [Test]
    public async Task AuthorizationCodeReceived_NoIdTokenInResponse_FailsWithNoIdTokenMessage()
    {
        TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 42);
        oidcConfig.MetadataAddress = "https://idp.example.com/.well-known/openid-configuration";

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(oidcConfig));

        IOidcSecretProtector secretProtector = Substitute.For<IOidcSecretProtector>();
        secretProtector.IsProtected(Arg.Any<string?>()).Returns(true);
        secretProtector.Unprotect(Arg.Any<string>()).Returns("plain-secret");

        // Return a token response with access_token but no id_token
        string tokenResponseJson = JsonSerializer.Serialize(new
        {
            access_token = "some-access-token",
            token_type = "Bearer",
        });

        IHttpClientFactory httpClientFactory = BuildMockHttpClientFactory(
            tokenEndpointUrl: "https://idp.example.com/token",
            tokenExchangeResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(tokenResponseJson, System.Text.Encoding.UTF8, "application/json")
            });

        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "42",
            authCode: "test-code",
            tenantRepo: tenantRepo,
            httpClientFactory: httpClientFactory,
            secretProtector: secretProtector);

        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("No id_token received");
    }

    [Test]
    public async Task AuthorizationCodeReceived_EmptyIdToken_FailsWithNoIdTokenMessage()
    {
        TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 42);
        oidcConfig.MetadataAddress = "https://idp.example.com/.well-known/openid-configuration";

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(oidcConfig));

        IOidcSecretProtector secretProtector = Substitute.For<IOidcSecretProtector>();
        secretProtector.IsProtected(Arg.Any<string?>()).Returns(true);
        secretProtector.Unprotect(Arg.Any<string>()).Returns("plain-secret");

        // Return a token response with an empty id_token string
        string tokenResponseJson = JsonSerializer.Serialize(new
        {
            id_token = "",
            access_token = "some-access-token",
        });

        IHttpClientFactory httpClientFactory = BuildMockHttpClientFactory(
            tokenEndpointUrl: "https://idp.example.com/token",
            tokenExchangeResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(tokenResponseJson, System.Text.Encoding.UTF8, "application/json")
            });

        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "42",
            authCode: "test-code",
            tenantRepo: tenantRepo,
            httpClientFactory: httpClientFactory,
            secretProtector: secretProtector);

        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("No id_token received");
    }

    [Test]
    public async Task AuthorizationCodeReceived_InvalidIdToken_FailsWithValidationFailedMessage()
    {
        TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 42);
        oidcConfig.MetadataAddress = "https://idp.example.com/.well-known/openid-configuration";

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(oidcConfig));

        IOidcSecretProtector secretProtector = Substitute.For<IOidcSecretProtector>();
        secretProtector.IsProtected(Arg.Any<string?>()).Returns(true);
        secretProtector.Unprotect(Arg.Any<string>()).Returns("plain-secret");

        // Return a well-formed but unsigned/invalid JWT as the id_token.
        // This will fail signature validation because the discovery document has no signing keys.
        string tokenResponseJson = JsonSerializer.Serialize(new
        {
            id_token = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ1c2VyMTIzIiwiYXVkIjoidGVzdC1jbGllbnQtaWQiLCJpc3MiOiJodHRwczovL2lkcC5leGFtcGxlLmNvbSIsImV4cCI6OTk5OTk5OTk5OX0.invalid-signature",
            access_token = "some-access-token",
        });

        IHttpClientFactory httpClientFactory = BuildMockHttpClientFactory(
            tokenEndpointUrl: "https://idp.example.com/token",
            tokenExchangeResponse: new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(tokenResponseJson, System.Text.Encoding.UTF8, "application/json")
            });

        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "42",
            authCode: "test-code",
            tenantRepo: tenantRepo,
            httpClientFactory: httpClientFactory,
            secretProtector: secretProtector);

        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("id_token validation failed");
    }

    [Test]
    public async Task AuthorizationCodeReceived_NullProperties_FailsWithMissingTenantMessage()
    {
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ILogger<SsoOidcEvents>))
            .Returns(Substitute.For<ILogger<SsoOidcEvents>>());

        DefaultHttpContext httpContext = new();
        httpContext.RequestServices = serviceProvider;

        OpenIdConnectOptions options = new();
        OidcMessage message = new() { Code = "test-code" };

        // Pass null properties to trigger the null-check branch
        AuthorizationCodeReceivedContext context = new(
            httpContext,
            new AuthenticationScheme("sso-oidc", "SSO OIDC", typeof(OpenIdConnectHandler)),
            options,
            null!)
        {
            ProtocolMessage = message
        };

        SsoOidcEvents events = new();
        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("Missing tenant ID");
    }

    [Test]
    public async Task AuthorizationCodeReceived_TokenEndpointIsLocalhost_FailsWithDisallowedMessage()
    {
        TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 42);
        oidcConfig.MetadataAddress = "https://idp.example.com/.well-known/openid-configuration";

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(oidcConfig));

        // Discovery returns a token endpoint pointing to localhost
        IHttpClientFactory httpClientFactory = BuildMockHttpClientFactory(
            tokenEndpointUrl: "https://localhost/token",
            tokenExchangeResponse: new HttpResponseMessage(HttpStatusCode.OK));

        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "42",
            authCode: "test-code",
            tenantRepo: tenantRepo,
            httpClientFactory: httpClientFactory);

        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("disallowed destination");
    }

    [Test]
    public async Task AuthorizationCodeReceived_TokenExchangeServerError_FailsWithStatusCode()
    {
        TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 42);
        oidcConfig.MetadataAddress = "https://idp.example.com/.well-known/openid-configuration";

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(oidcConfig));

        IOidcSecretProtector secretProtector = Substitute.For<IOidcSecretProtector>();
        secretProtector.IsProtected(Arg.Any<string?>()).Returns(true);
        secretProtector.Unprotect(Arg.Any<string>()).Returns("plain-secret");

        IHttpClientFactory httpClientFactory = BuildMockHttpClientFactory(
            tokenEndpointUrl: "https://idp.example.com/token",
            tokenExchangeResponse: new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Internal Server Error")
            });

        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "42",
            authCode: "test-code",
            tenantRepo: tenantRepo,
            httpClientFactory: httpClientFactory,
            secretProtector: secretProtector);

        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("Token exchange failed");
        await Assert.That(context.Result?.Failure?.Message).Contains("InternalServerError");
    }

    // --- AuthorizationCodeReceived nonce branch tests ---

    private const string TestNonce = "test-nonce-value";

    private const string TestSigningKid = "test-signing-key";

    /// <summary>
    /// Builds an <see cref="IHttpClientFactory"/> whose discovery document advertises a JWKS containing
    /// the public half of <paramref name="signingKey"/>, so that an id_token signed by that key passes
    /// signature validation in <see cref="SsoOidcEvents.AuthorizationCodeReceived"/>.
    /// </summary>
    private static IHttpClientFactory BuildMockHttpClientFactoryWithJwks(
        RsaSecurityKey signingKey,
        HttpResponseMessage tokenExchangeResponse)
    {
        string discoveryJson = JsonSerializer.Serialize(new
        {
            issuer = "https://idp.example.com",
            authorization_endpoint = "https://idp.example.com/authorize",
            token_endpoint = "https://idp.example.com/token",
            jwks_uri = "https://idp.example.com/jwks",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
        });

        RSAParameters publicParams = signingKey.Rsa!.ExportParameters(false);
        string jwksJson = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = TestSigningKid,
                    n = Base64UrlEncoder.Encode(publicParams.Modulus),
                    e = Base64UrlEncoder.Encode(publicParams.Exponent),
                },
            },
        });

        MockHttpMessageHandler discoveryHandler = new();
        discoveryHandler.WithDefaultResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(discoveryJson, System.Text.Encoding.UTF8, "application/json")
        });
        discoveryHandler.WithResponse("https://idp.example.com/jwks", new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jwksJson, System.Text.Encoding.UTF8, "application/json")
        });

        MockHttpMessageHandler tokenHandler = new();
        tokenHandler.WithDefaultResponse(tokenExchangeResponse);

        IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("OidcDiscovery").Returns(new HttpClient(discoveryHandler));
        httpClientFactory.CreateClient("OidcTokenExchange").Returns(new HttpClient(tokenHandler));

        return httpClientFactory;
    }

    /// <summary>
    /// Creates a signed RS256 id_token carrying the supplied nonce and issuer/audience that match the
    /// mock discovery document. The token's lifetime is set far in the future so validation never
    /// depends on wall-clock proximity.
    /// </summary>
    private static string BuildSignedIdToken(RsaSecurityKey signingKey, string nonce)
    {
        signingKey.KeyId = TestSigningKid;
        SigningCredentials credentials = new(signingKey, SecurityAlgorithms.RsaSha256);

        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = "https://idp.example.com",
            Audience = "test-client-id",
            Claims = new Dictionary<string, object> { ["nonce"] = nonce },
            Expires = DateTime.UtcNow.AddYears(10),
            NotBefore = DateTime.UtcNow.AddYears(-1),
            IssuedAt = DateTime.UtcNow.AddYears(-1),
            SigningCredentials = credentials,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>
    /// Builds an <see cref="IOptionsMonitor{T}"/> for the tenant-oidc scheme whose
    /// <see cref="OpenIdConnectOptions.StringDataFormat"/> unprotects the supplied protected cookie
    /// value back to the expected nonce, mirroring the handler's challenge-time nonce binding.
    /// </summary>
    private static IOptionsMonitor<OpenIdConnectOptions> BuildOptionsMonitorWithNonce(
        string protectedCookieValue,
        string nonce)
    {
        OpenIdConnectOptions options = new()
        {
            StringDataFormat = new StubStringDataFormat(protectedCookieValue, nonce),
        };

        IOptionsMonitor<OpenIdConnectOptions> monitor = Substitute.For<IOptionsMonitor<OpenIdConnectOptions>>();
        monitor.Get("tenant-oidc").Returns(options);

        return monitor;
    }

    [Test]
    public async Task AuthorizationCodeReceived_ValidMatchingNonceFirstUse_DoesNotFailAtNonceStep()
    {
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa);

        TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 42);
        oidcConfig.MetadataAddress = "https://idp.example.com/.well-known/openid-configuration";

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(oidcConfig));

        IOidcSecretProtector secretProtector = Substitute.For<IOidcSecretProtector>();
        secretProtector.IsProtected(Arg.Any<string?>()).Returns(true);
        secretProtector.Unprotect(Arg.Any<string>()).Returns("plain-secret");

        string idToken = BuildSignedIdToken(signingKey, TestNonce);
        string tokenResponseJson = JsonSerializer.Serialize(new { id_token = idToken });

        IHttpClientFactory httpClientFactory = BuildMockHttpClientFactoryWithJwks(
            signingKey,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(tokenResponseJson, System.Text.Encoding.UTF8, "application/json")
            });

        string protectedCookieValue = "protected-nonce-cookie";
        IOptionsMonitor<OpenIdConnectOptions> optionsMonitor = BuildOptionsMonitorWithNonce(protectedCookieValue, TestNonce);
        Dictionary<string, string> cookies = new()
        {
            [$"{OidcNonceValidator.NonceCookiePrefix}abc"] = protectedCookieValue,
        };

        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "42",
            authCode: "test-code",
            tenantRepo: tenantRepo,
            httpClientFactory: httpClientFactory,
            secretProtector: secretProtector,
            redis: FakeRedisConnection.Create(),
            optionsMonitor: optionsMonitor,
            cookies: cookies);

        await events.AuthorizationCodeReceived(context);

        // The id_token carries no subject, so the flow stops at user provisioning — but it must
        // get past nonce validation first, proving the matching first-use nonce is accepted.
        await Assert.That(context.Result?.Failure?.Message ?? string.Empty).DoesNotContain("nonce validation failed");
    }

    [Test]
    public async Task AuthorizationCodeReceived_ReplayedNonce_FailsWithNonceValidationFailed()
    {
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa);

        TenantOidcConfiguration oidcConfig = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 42);
        oidcConfig.MetadataAddress = "https://idp.example.com/.well-known/openid-configuration";

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantOidcConfigurationAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantOidcConfiguration?>(oidcConfig));

        IOidcSecretProtector secretProtector = Substitute.For<IOidcSecretProtector>();
        secretProtector.IsProtected(Arg.Any<string?>()).Returns(true);
        secretProtector.Unprotect(Arg.Any<string>()).Returns("plain-secret");

        string idToken = BuildSignedIdToken(signingKey, TestNonce);
        string tokenResponseJson = JsonSerializer.Serialize(new { id_token = idToken });

        IHttpClientFactory httpClientFactory = BuildMockHttpClientFactoryWithJwks(
            signingKey,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(tokenResponseJson, System.Text.Encoding.UTF8, "application/json")
            });

        string protectedCookieValue = "protected-nonce-cookie";
        IOptionsMonitor<OpenIdConnectOptions> optionsMonitor = BuildOptionsMonitorWithNonce(protectedCookieValue, TestNonce);
        Dictionary<string, string> cookies = new()
        {
            [$"{OidcNonceValidator.NonceCookiePrefix}abc"] = protectedCookieValue,
        };

        // Redis reports the nonce has already been consumed (replay): StringSetAsync When.NotExists -> false.
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redisDb.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(), When.NotExists, Arg.Any<CommandFlags>())
            .Returns(false);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        SsoOidcEvents events = new();
        AuthorizationCodeReceivedContext context = BuildCodeReceivedContext(
            tenantId: "42",
            authCode: "test-code",
            tenantRepo: tenantRepo,
            httpClientFactory: httpClientFactory,
            secretProtector: secretProtector,
            redis: redis,
            optionsMonitor: optionsMonitor,
            cookies: cookies);

        await events.AuthorizationCodeReceived(context);

        await Assert.That(context.Result?.Failure?.Message).Contains("nonce validation failed");
    }
}
