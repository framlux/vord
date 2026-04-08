// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Auth;
using System.Net.Http;
using System.Net;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="SsrfSafeSocketsHttpHandler"/> IP validation logic.
/// Verifies that the handler correctly blocks connections to private, reserved,
/// and loopback addresses that could be exploited via SSRF.
/// </summary>
public class SsrfSafeSocketsHttpHandlerTests
{
    // --- Loopback addresses must be blocked ---

    [Test]
    public async Task LoopbackIpv4_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("127.0.0.1"));

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task LoopbackIpv4NonStandard_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("127.0.0.2"));

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task LoopbackIpv6_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Loopback);

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task Ipv6Loopback_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.IPv6Loopback);

        await Assert.That(result).IsEqualTo(true);
    }

    // --- Wildcard/any addresses must be blocked ---

    [Test]
    public async Task Ipv4Any_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Any);

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task Ipv6Any_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.IPv6Any);

        await Assert.That(result).IsEqualTo(true);
    }

    // --- RFC 1918 private ranges must be blocked ---

    [Test]
    public async Task PrivateRange10_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("10.0.0.1"));

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task PrivateRange10UpperBound_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("10.255.255.255"));

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task PrivateRange172Lower_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("172.16.0.1"));

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task PrivateRange172Upper_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("172.31.255.255"));

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task NonPrivate172_IsAllowed()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("172.32.0.1"));

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task PrivateRange192168_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("192.168.1.1"));

        await Assert.That(result).IsEqualTo(true);
    }

    // --- Cloud metadata / link-local must be blocked ---

    [Test]
    public async Task CloudMetadataEndpoint_IsBlocked()
    {
        // AWS/GCP/Azure metadata endpoint lives at 169.254.169.254
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("169.254.169.254"));

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task LinkLocalRange_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("169.254.0.1"));

        await Assert.That(result).IsEqualTo(true);
    }

    // --- IPv6 private ranges must be blocked ---

    [Test]
    public async Task Ipv6UniqueLocalFc_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("fc00::1"));

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task Ipv6UniqueLocalFd_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("fd12:3456:789a::1"));

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task Ipv6LinkLocal_IsBlocked()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("fe80::1"));

        await Assert.That(result).IsEqualTo(true);
    }

    // --- IPv4-mapped IPv6 with private embedded address must be blocked ---

    [Test]
    public async Task Ipv4MappedIpv6WithPrivateAddress_IsBlocked()
    {
        // ::ffff:10.0.0.1 — IPv4-mapped form of a private address
        IPAddress mapped = IPAddress.Parse("::ffff:10.0.0.1");

        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(mapped);

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task Ipv4MappedIpv6WithCloudMetadata_IsBlocked()
    {
        // ::ffff:169.254.169.254 — cloud metadata via IPv4-mapped IPv6
        IPAddress mapped = IPAddress.Parse("::ffff:169.254.169.254");

        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(mapped);

        await Assert.That(result).IsEqualTo(true);
    }

    // --- Public addresses must be allowed ---

    [Test]
    public async Task PublicIpv4_IsAllowed()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("8.8.8.8"));

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task PublicIpv4_93_IsAllowed()
    {
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("93.184.216.34"));

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task PublicIpv6_IsAllowed()
    {
        // Google Public DNS IPv6
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("2001:4860:4860::8888"));

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task Ipv4MappedIpv6WithPublicAddress_IsAllowed()
    {
        IPAddress mapped = IPAddress.Parse("::ffff:8.8.8.8");

        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(mapped);

        await Assert.That(result).IsEqualTo(false);
    }

    // --- Boundary tests for 172.16-31 range ---

    [Test]
    public async Task PrivateRange172_16_Boundary_IsBlocked()
    {
        // Start of 172.16.0.0/12 range
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("172.16.0.1"));

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task PrivateRange172_31_Boundary_IsBlocked()
    {
        // End of 172.16.0.0/12 range
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("172.31.255.254"));

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task NonPrivate172_15_IsAllowed()
    {
        // Just below the private range
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("172.15.255.254"));

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task NonPrivate172_32_IsAllowed()
    {
        // Just above the private range
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("172.32.0.1"));

        await Assert.That(result).IsEqualTo(false);
    }

    // --- IPv6 link-local second byte boundary ---

    [Test]
    public async Task Ipv6LinkLocal_FeBf_IsBlocked()
    {
        // fe80::/10 includes fe80:: through febf::
        bool result = SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(IPAddress.Parse("febf::1"));

        await Assert.That(result).IsEqualTo(true);
    }

    // --- ConnectAsync callback integration via HttpClient ---
    // These tests verify the full SSRF protection works end-to-end through the HTTP stack.

    [Test]
    public async Task HttpClient_ConnectionToLoopback_IsBlocked()
    {
        using System.Net.Http.SocketsHttpHandler handler = SsrfSafeSocketsHttpHandler.Create();
        using HttpClient client = new(handler);

        // Attempting to connect to 127.0.0.1 should be blocked by the callback
        await Assert.That(async () =>
            await client.GetAsync("https://127.0.0.1/test"))
            .Throws<HttpRequestException>();
    }

    [Test]
    public async Task HttpClient_ConnectionToLinkLocal_IsBlocked()
    {
        using System.Net.Http.SocketsHttpHandler handler = SsrfSafeSocketsHttpHandler.Create();
        using HttpClient client = new(handler);

        // Cloud metadata endpoint must be blocked
        await Assert.That(async () =>
            await client.GetAsync("https://169.254.169.254/latest/meta-data/"))
            .Throws<HttpRequestException>();
    }

    [Test]
    public async Task HttpClient_ConnectionToPrivate10_IsBlocked()
    {
        using System.Net.Http.SocketsHttpHandler handler = SsrfSafeSocketsHttpHandler.Create();
        using HttpClient client = new(handler);

        await Assert.That(async () =>
            await client.GetAsync("https://10.0.0.1/internal-api"))
            .Throws<HttpRequestException>();
    }

    [Test]
    public async Task HttpClient_ConnectionToPrivate192168_IsBlocked()
    {
        using System.Net.Http.SocketsHttpHandler handler = SsrfSafeSocketsHttpHandler.Create();
        using HttpClient client = new(handler);

        await Assert.That(async () =>
            await client.GetAsync("https://192.168.1.1/admin"))
            .Throws<HttpRequestException>();
    }

    [Test]
    public async Task HttpClient_ConnectionToNonexistentDomain_Throws()
    {
        using System.Net.Http.SocketsHttpHandler handler = SsrfSafeSocketsHttpHandler.Create();
        using HttpClient client = new(handler);

        // DNS resolution for a nonexistent domain should throw (not hang)
        await Assert.That(async () =>
            await client.GetAsync("https://this-domain-does-not-exist-zzzz.invalid/"))
            .Throws<HttpRequestException>();
    }
}
