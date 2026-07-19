// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints;

/// <summary>
/// Shared helper for writing <see cref="ApiResponse{T}"/> error envelopes from endpoint
/// handlers and pre-processors. Centralizes the status-code + JSON body idiom that was
/// previously hand-written per endpoint, so serializer behavior stays consistent.
/// </summary>
public static class EndpointErrorExtensions
{
    /// <summary>
    /// Writes <paramref name="statusCode"/> and a failed <see cref="ApiResponse{T}"/>
    /// envelope containing <paramref name="message"/> to the response.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="statusCode">The HTTP status code to set.</param>
    /// <param name="message">The human-readable error message.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task SendApiErrorAsync(this HttpContext httpContext, int statusCode, string message, CancellationToken ct)
    {
        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(ApiResponse<object>.Error(message), ct);
    }
}
