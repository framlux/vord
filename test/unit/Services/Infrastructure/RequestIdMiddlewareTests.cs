// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="RequestIdMiddleware"/>.
/// </summary>
public class RequestIdMiddlewareTests
{
    private static DefaultHttpContext CreateContext(string? requestIdHeader = null)
    {
        DefaultHttpContext context = new();
        if (requestIdHeader is not null)
        {
            context.Request.Headers[RequestIdMiddleware.HeaderName] = requestIdHeader;
        }

        return context;
    }

    private static RequestIdMiddleware CreateMiddleware(RequestDelegate? next = null)
    {
        return new RequestIdMiddleware(next ?? (_ => Task.CompletedTask));
    }

    // ───────────────────────────────────────────────
    // Request ID generation when no header is supplied
    // ───────────────────────────────────────────────

    /// <summary>
    /// When no X-Request-ID header is present, the middleware must generate one
    /// and store it in HttpContext.Items.
    /// </summary>
    [Test]
    public async Task InvokeAsync_NoHeader_GeneratesRequestIdInItems()
    {
        RequestIdMiddleware middleware = CreateMiddleware();
        DefaultHttpContext context = CreateContext();

        await middleware.InvokeAsync(context);

        string? storedId = context.Items[RequestIdMiddleware.ItemsKey] as string;
        await Assert.That(storedId).IsNotNull();
        await Assert.That(storedId!.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// When no header is present, the generated ID must be a 32-character hex string
    /// (GUID formatted with "N").
    /// </summary>
    [Test]
    public async Task InvokeAsync_NoHeader_GeneratesValidGuidFormat()
    {
        RequestIdMiddleware middleware = CreateMiddleware();
        DefaultHttpContext context = CreateContext();

        await middleware.InvokeAsync(context);

        string storedId = (string)context.Items[RequestIdMiddleware.ItemsKey]!;
        bool isValidGuid = Guid.TryParseExact(storedId, "N", out _);
        await Assert.That(isValidGuid).IsEqualTo(true);
    }

    /// <summary>
    /// Each request without a header must receive a unique generated ID,
    /// ensuring no collisions between independent requests.
    /// </summary>
    [Test]
    public async Task InvokeAsync_NoHeader_GeneratesUniqueIdsPerRequest()
    {
        RequestIdMiddleware middleware = CreateMiddleware();
        DefaultHttpContext context1 = CreateContext();
        DefaultHttpContext context2 = CreateContext();

        await middleware.InvokeAsync(context1);
        await middleware.InvokeAsync(context2);

        string id1 = (string)context1.Items[RequestIdMiddleware.ItemsKey]!;
        string id2 = (string)context2.Items[RequestIdMiddleware.ItemsKey]!;
        await Assert.That(id1).IsNotEqualTo(id2);
    }

    // ───────────────────────────────────────────────
    // Request ID propagation when header IS supplied
    // ───────────────────────────────────────────────

    /// <summary>
    /// When the caller supplies an X-Request-ID header, the middleware must
    /// propagate that exact value rather than generating a new one.
    /// </summary>
    [Test]
    public async Task InvokeAsync_WithHeader_UsesProvidedValue()
    {
        RequestIdMiddleware middleware = CreateMiddleware();
        DefaultHttpContext context = CreateContext("abc-123");

        await middleware.InvokeAsync(context);

        string storedId = (string)context.Items[RequestIdMiddleware.ItemsKey]!;
        await Assert.That(storedId).IsEqualTo("abc-123");
    }

    /// <summary>
    /// The middleware must accept arbitrary string formats for the header,
    /// not just GUIDs. Callers may use ULIDs, trace IDs, or custom formats.
    /// </summary>
    [Test]
    public async Task InvokeAsync_WithNonGuidHeader_UsesProvidedValue()
    {
        RequestIdMiddleware middleware = CreateMiddleware();
        DefaultHttpContext context = CreateContext("trace-01HXYZ-abc");

        await middleware.InvokeAsync(context);

        string storedId = (string)context.Items[RequestIdMiddleware.ItemsKey]!;
        await Assert.That(storedId).IsEqualTo("trace-01HXYZ-abc");
    }

    // ───────────────────────────────────────────────
    // Edge cases: whitespace and empty header values
    // ───────────────────────────────────────────────

    /// <summary>
    /// An empty-string header should be treated as absent and trigger generation.
    /// </summary>
    [Test]
    public async Task InvokeAsync_EmptyHeader_GeneratesNewId()
    {
        RequestIdMiddleware middleware = CreateMiddleware();
        DefaultHttpContext context = CreateContext("");

        await middleware.InvokeAsync(context);

        string storedId = (string)context.Items[RequestIdMiddleware.ItemsKey]!;
        bool isValidGuid = Guid.TryParseExact(storedId, "N", out _);
        await Assert.That(isValidGuid).IsEqualTo(true);
    }

    /// <summary>
    /// A whitespace-only header should be treated as absent and trigger generation.
    /// </summary>
    [Test]
    public async Task InvokeAsync_WhitespaceHeader_GeneratesNewId()
    {
        RequestIdMiddleware middleware = CreateMiddleware();
        DefaultHttpContext context = CreateContext("   ");

        await middleware.InvokeAsync(context);

        string storedId = (string)context.Items[RequestIdMiddleware.ItemsKey]!;
        bool isValidGuid = Guid.TryParseExact(storedId, "N", out _);
        await Assert.That(isValidGuid).IsEqualTo(true);
    }

    // ───────────────────────────────────────────────
    // Response header echoing
    // ───────────────────────────────────────────────

    /// <summary>
    /// The response must always contain the X-Request-ID header echoing the
    /// resolved (or generated) value back to the caller.
    /// </summary>
    [Test]
    public async Task InvokeAsync_NoHeader_SetsResponseHeader()
    {
        RequestIdMiddleware middleware = CreateMiddleware();
        DefaultHttpContext context = CreateContext();

        await middleware.InvokeAsync(context);

        string? responseHeader = context.Response.Headers[RequestIdMiddleware.HeaderName].FirstOrDefault();
        string storedId = (string)context.Items[RequestIdMiddleware.ItemsKey]!;
        await Assert.That(responseHeader).IsEqualTo(storedId);
    }

    /// <summary>
    /// When the caller provides a request ID, the response header must echo
    /// that same value back, not a newly generated one.
    /// </summary>
    [Test]
    public async Task InvokeAsync_WithHeader_EchoesValueInResponse()
    {
        RequestIdMiddleware middleware = CreateMiddleware();
        DefaultHttpContext context = CreateContext("caller-id-999");

        await middleware.InvokeAsync(context);

        string? responseHeader = context.Response.Headers[RequestIdMiddleware.HeaderName].FirstOrDefault();
        await Assert.That(responseHeader).IsEqualTo("caller-id-999");
    }

    // ───────────────────────────────────────────────
    // Items and response header consistency
    // ───────────────────────────────────────────────

    /// <summary>
    /// The value stored in HttpContext.Items and the value written to the
    /// response header must always be identical.
    /// </summary>
    [Test]
    public async Task InvokeAsync_Always_ItemsAndResponseHeaderMatch()
    {
        RequestIdMiddleware middleware = CreateMiddleware();
        DefaultHttpContext context = CreateContext("consistent-value");

        await middleware.InvokeAsync(context);

        string storedId = (string)context.Items[RequestIdMiddleware.ItemsKey]!;
        string? responseHeader = context.Response.Headers[RequestIdMiddleware.HeaderName].FirstOrDefault();
        await Assert.That(storedId).IsEqualTo(responseHeader);
    }

    // ───────────────────────────────────────────────
    // Pipeline delegation
    // ───────────────────────────────────────────────

    /// <summary>
    /// The middleware must always call the next delegate in the pipeline,
    /// ensuring downstream middleware and endpoints execute.
    /// </summary>
    [Test]
    public async Task InvokeAsync_Always_CallsNextDelegate()
    {
        bool nextCalled = false;
        RequestIdMiddleware middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;

            return Task.CompletedTask;
        });
        DefaultHttpContext context = CreateContext();

        await middleware.InvokeAsync(context);

        await Assert.That(nextCalled).IsEqualTo(true);
    }

    /// <summary>
    /// The request ID must be available in HttpContext.Items when the next
    /// delegate runs, so downstream components can use it for logging/correlation.
    /// </summary>
    [Test]
    public async Task InvokeAsync_WithHeader_RequestIdAvailableInNext()
    {
        string? capturedId = null;
        RequestIdMiddleware middleware = CreateMiddleware(ctx =>
        {
            capturedId = ctx.Items[RequestIdMiddleware.ItemsKey] as string;

            return Task.CompletedTask;
        });
        DefaultHttpContext context = CreateContext("downstream-check");

        await middleware.InvokeAsync(context);

        await Assert.That(capturedId).IsEqualTo("downstream-check");
    }

    /// <summary>
    /// The response header must already be set before the next delegate runs,
    /// so that even if downstream code reads it, the value is present.
    /// </summary>
    [Test]
    public async Task InvokeAsync_Always_ResponseHeaderSetBeforeNext()
    {
        string? capturedResponseHeader = null;
        RequestIdMiddleware middleware = CreateMiddleware(ctx =>
        {
            capturedResponseHeader = ctx.Response.Headers[RequestIdMiddleware.HeaderName].FirstOrDefault();

            return Task.CompletedTask;
        });
        DefaultHttpContext context = CreateContext("pre-set-check");

        await middleware.InvokeAsync(context);

        await Assert.That(capturedResponseHeader).IsEqualTo("pre-set-check");
    }

    // ───────────────────────────────────────────────
    // ResolveRequestId unit tests (isolated logic)
    // ───────────────────────────────────────────────

    /// <summary>
    /// ResolveRequestId must return the header value when it is a non-empty string.
    /// </summary>
    [Test]
    public async Task ResolveRequestId_WithValidHeader_ReturnsHeaderValue()
    {
        DefaultHttpContext context = CreateContext("resolve-test");

        string result = RequestIdMiddleware.ResolveRequestId(context);

        await Assert.That(result).IsEqualTo("resolve-test");
    }

    /// <summary>
    /// ResolveRequestId must generate a new GUID when no header is present.
    /// </summary>
    [Test]
    public async Task ResolveRequestId_NoHeader_ReturnsNewGuid()
    {
        DefaultHttpContext context = CreateContext();

        string result = RequestIdMiddleware.ResolveRequestId(context);

        bool isValidGuid = Guid.TryParseExact(result, "N", out _);
        await Assert.That(isValidGuid).IsEqualTo(true);
    }

    /// <summary>
    /// ResolveRequestId must generate a new GUID when the header is null
    /// (simulating a missing header at the StringValues level).
    /// </summary>
    [Test]
    public async Task ResolveRequestId_NullHeaderValue_ReturnsNewGuid()
    {
        DefaultHttpContext context = CreateContext(null);

        string result = RequestIdMiddleware.ResolveRequestId(context);

        bool isValidGuid = Guid.TryParseExact(result, "N", out _);
        await Assert.That(isValidGuid).IsEqualTo(true);
    }
}
