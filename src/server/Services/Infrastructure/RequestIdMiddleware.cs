// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Middleware that propagates or generates a unique request correlation ID.
/// If the incoming request contains an <c>X-Request-ID</c> header, that value is reused;
/// otherwise a new GUID is generated. The ID is stored in <see cref="HttpContext.Items"/>
/// and echoed back in the response <c>X-Request-ID</c> header. A Serilog
/// <c>RequestId</c> log-context property is pushed for the duration of the request.
/// </summary>
public sealed class RequestIdMiddleware
{
    /// <summary>
    /// The header name used for request correlation.
    /// </summary>
    public const string HeaderName = "X-Request-ID";

    /// <summary>
    /// The key used to store the request ID in <see cref="HttpContext.Items"/>.
    /// </summary>
    public const string ItemsKey = "RequestId";

    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of <see cref="RequestIdMiddleware"/>.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    public RequestIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// Processes the HTTP request, ensuring a request ID is present.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        string requestId = ResolveRequestId(context);
        context.Items[ItemsKey] = requestId;
        context.Response.Headers[HeaderName] = requestId;
        using (LogContext.PushProperty(ItemsKey, requestId))
        {
            await _next(context);
        }
    }

    /// <summary>
    /// Extracts the request ID from the incoming header, falling back to a new GUID
    /// if the header is missing or contains only whitespace.
    /// </summary>
    internal static string ResolveRequestId(HttpContext context)
    {
        string? headerValue = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(headerValue) == false)
        {
            return headerValue;
        }

        return Guid.NewGuid().ToString("N");
    }
}
