// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using Framlux.FleetManagement.Server.Auth;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="HttpAuditContextAccessor"/> — verifies that the client IP is
/// correctly extracted from an active HTTP context and that null is returned gracefully
/// when no HTTP context is present.
/// </summary>
public class HttpAuditContextAccessorTests
{
    // ========== Constructor ==========

    [Test]
    public async Task Constructor_NullHttpContextAccessor_ThrowsArgumentNullException()
    {
        await Assert.That(() => new HttpAuditContextAccessor(null!))
            .Throws<ArgumentNullException>();
    }

    // ========== GetClientIp — no HTTP context ==========

    [Test]
    public async Task GetClientIp_NullHttpContext_ReturnsNull()
    {
        HttpContextAccessor accessor = new()
        {
            HttpContext = null
        };
        HttpAuditContextAccessor sut = new(accessor);

        string? result = sut.GetClientIp();

        await Assert.That(result).IsNull();
    }

    // ========== GetClientIp — active HTTP context ==========

    [Test]
    public async Task GetClientIp_HttpContextWithRemoteIp_ReturnsIpString()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.42");

        HttpContextAccessor accessor = new()
        {
            HttpContext = httpContext
        };
        HttpAuditContextAccessor sut = new(accessor);

        string? result = sut.GetClientIp();

        await Assert.That(result).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task GetClientIp_HttpContextWithNullRemoteIp_ReturnsNull()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Connection.RemoteIpAddress = null;

        HttpContextAccessor accessor = new()
        {
            HttpContext = httpContext
        };
        HttpAuditContextAccessor sut = new(accessor);

        string? result = sut.GetClientIp();

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetClientIp_Ipv6RemoteAddress_ReturnsIpv6String()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("::1");

        HttpContextAccessor accessor = new()
        {
            HttpContext = httpContext
        };
        HttpAuditContextAccessor sut = new(accessor);

        string? result = sut.GetClientIp();

        await Assert.That(result).IsEqualTo("::1");
    }
}
