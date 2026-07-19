// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace Framlux.FleetManagement.Test.Endpoints;

/// <summary>
/// Unit tests for <see cref="EndpointErrorExtensions"/>.
/// </summary>
public sealed class EndpointErrorExtensionsTests
{
    [Test]
    [Arguments(400, "Bad input")]
    [Arguments(401, "Unauthorized")]
    [Arguments(404, "Not found")]
    [Arguments(502, "Upstream failure")]
    public async Task SendApiErrorAsync_WritesStatusCodeAndErrorEnvelope(int statusCode, string message)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Response.Body = new MemoryStream();

        await httpContext.SendApiErrorAsync(statusCode, message, CancellationToken.None);

        httpContext.Response.Body.Position = 0;
        ApiResponse<object>? body = await JsonSerializer.DeserializeAsync<ApiResponse<object>>(
            httpContext.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(statusCode);
        await Assert.That(body).IsNotNull();
        await Assert.That(body?.Success).IsFalse();
        await Assert.That(body?.Message).IsEqualTo(message);
        await Assert.That(body?.Data).IsNull();
    }
}
