// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Test.Infrastructure;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="SsoOidcEvents"/> URL safety validation.
/// </summary>
public sealed class SsoOidcEventsTests
{
    // --- IsUrlSafe Tests ---

    [Test]
    public async Task IsUrlSafe_ValidHttpsPublicUrl_ReturnsTrue()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://login.example.com/.well-known/openid-configuration");

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task IsUrlSafe_HttpUrl_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("http://login.example.com/.well-known/openid-configuration");

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsUrlSafe_Localhost_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://localhost/.well-known/openid-configuration");

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsUrlSafe_LoopbackIp127_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://127.0.0.1/.well-known/openid-configuration");

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsUrlSafe_ZeroIp_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://0.0.0.0/.well-known/openid-configuration");

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsUrlSafe_Ipv6Loopback_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://[::1]/.well-known/openid-configuration");

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsUrlSafe_PrivateIp10_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://10.0.0.1/.well-known/openid-configuration");

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsUrlSafe_PrivateIp192_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://192.168.1.1/.well-known/openid-configuration");

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsUrlSafe_PrivateIp172_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://172.16.0.1/.well-known/openid-configuration");

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsUrlSafe_NullOrEmpty_ReturnsFalse()
    {
        bool resultEmpty = SsoOidcEvents.IsUrlSafe(string.Empty);

        await Assert.That(resultEmpty).IsEqualTo(false);
    }

    [Test]
    public async Task IsUrlSafe_MalformedUrl_ReturnsFalse()
    {
        bool result = SsoOidcEvents.IsUrlSafe("not-a-url");

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsUrlSafe_PublicIpAddress_ReturnsTrue()
    {
        bool result = SsoOidcEvents.IsUrlSafe("https://8.8.8.8/endpoint");

        await Assert.That(result).IsEqualTo(true);
    }

    // --- IsUrlSafeAsync Tests ---

    [Test]
    public async Task IsUrlSafeAsync_HttpUrl_ReturnsFalse()
    {
        bool result = await SsoOidcEvents.IsUrlSafeAsync("http://example.com");

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsUrlSafeAsync_MalformedUrl_ReturnsFalse()
    {
        bool result = await SsoOidcEvents.IsUrlSafeAsync("not-a-url");

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsUrlSafeAsync_Localhost_ReturnsFalse()
    {
        bool result = await SsoOidcEvents.IsUrlSafeAsync("https://localhost/test");

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsUrlSafeAsync_PublicIpAddress_ReturnsTrue()
    {
        bool result = await SsoOidcEvents.IsUrlSafeAsync("https://8.8.8.8/endpoint");

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task IsUrlSafeAsync_PrivateIp10_ReturnsFalse()
    {
        bool result = await SsoOidcEvents.IsUrlSafeAsync("https://10.0.0.1/test");

        await Assert.That(result).IsEqualTo(false);
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

        // The discovery document fetch should use MetadataAddress, not Authority
        // We verify by checking the request URL captured by the handler
        try
        {
            await SsoOidcEvents.FetchDiscoveryDocumentAsync(config, httpClientFactory, CancellationToken.None);
        }
        catch
        {
            // The ConfigurationManager may fail parsing the mock response; that's expected.
            // We verify the metadata address was used via the handler's recorded requests.
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

        try
        {
            await SsoOidcEvents.FetchDiscoveryDocumentAsync(config, httpClientFactory, CancellationToken.None);
        }
        catch
        {
            // ConfigurationManager may fail parsing; we just verify the URL construction
        }

        await Assert.That(handler.Requests.Count).IsGreaterThanOrEqualTo(1);
        string requestUrl = handler.Requests[0].RequestUri?.ToString() ?? string.Empty;
        await Assert.That(requestUrl).Contains("idp.example.com/.well-known/openid-configuration");
    }

    [Test]
    public async Task FetchDiscoveryDocumentAsync_UnsafeAddress_ThrowsInvalidOperation()
    {
        TenantOidcConfiguration config = TestDataBuilder.BuildTenantOidcConfiguration();
        config.MetadataAddress = "http://unsafe-idp.example.com/.well-known/openid-configuration";
        config.Authority = "http://unsafe-idp.example.com";

        IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await SsoOidcEvents.FetchDiscoveryDocumentAsync(config, httpClientFactory, CancellationToken.None);
        });

        await Assert.That(ex.Message).Contains("disallowed");
    }
}
