// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using System.Globalization;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Adds the gRPC status of a call as a tag on the built-in <c>http.server.request.duration</c>
/// metric, so a failing gRPC surface moves a rate-of-errors series instead of nothing at all.
/// </summary>
/// <remarks>
/// gRPC runs as ordinary Kestrel endpoints, so every call is already timed by the ASP.NET Core
/// request instrument — but a failed call still completes with HTTP 200 and reports its failure in
/// the <c>grpc-status</c> trailer. Without this tag a 500-storm on telemetry ingest or a systematic
/// permission-denied on the internal control plane is indistinguishable from perfect health in
/// every series an error-rate rule can read.
/// </remarks>
public sealed class GrpcStatusMetricTagMiddleware
{
    /// <summary>
    /// The tag name written onto the request duration metric. This is the OpenTelemetry semantic
    /// convention attribute for a gRPC status, deliberately rather than a vord-specific name, so
    /// the series reads the same as it would under any other gRPC instrumentation.
    /// </summary>
    public const string TagName = "rpc.grpc.status_code";

    /// <summary>
    /// The header the gRPC protocol carries a call's status in.
    /// </summary>
    private const string GrpcStatusHeader = "grpc-status";

    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of <see cref="GrpcStatusMetricTagMiddleware"/>.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    public GrpcStatusMetricTagMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        _next = next;
    }

    /// <summary>
    /// Runs the rest of the pipeline and, once the response is complete, tags the request duration
    /// metric with the call's gRPC status.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <remarks>
    /// The status is only readable after the response has been written, so the tag is added on the
    /// way out. That is still before the hosting layer records the measurement, which happens once
    /// the whole pipeline has unwound.
    /// </remarks>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await _next(context);

        // The feature exists only while something is collecting the instrument, so an unobserved
        // deployment pays nothing for this.
        IHttpMetricsTagsFeature? tagsFeature = context.Features.Get<IHttpMetricsTagsFeature>();
        if (tagsFeature == null)
        {
            return;
        }

        int? status = ReadGrpcStatus(context);
        if (status.HasValue == false)
        {
            return;
        }

        tagsFeature.Tags.Add(new KeyValuePair<string, object?>(TagName, status.Value));
    }

    /// <summary>
    /// Reads the gRPC status of the completed response, or null when the request was not a gRPC
    /// call that reached a status.
    /// </summary>
    /// <param name="context">The completed HTTP context.</param>
    /// <returns>The numeric gRPC status code, or null.</returns>
    /// <remarks>
    /// Two places have to be looked at. A call that produced any response body reports its status
    /// in a trailer, which is the success case and the mid-stream failure case. A call that failed
    /// before the body started is sent as a trailers-only response, which puts the very same status
    /// in the response headers instead — and that is the shape of most unary failures, so reading
    /// trailers alone would miss the errors this exists to surface.
    /// </remarks>
    private static int? ReadGrpcStatus(HttpContext context)
    {
        StringValues raw = default;

        IHttpResponseTrailersFeature? trailersFeature = context.Features.Get<IHttpResponseTrailersFeature>();
        if (trailersFeature?.Trailers != null)
        {
            trailersFeature.Trailers.TryGetValue(GrpcStatusHeader, out raw);
        }

        if (StringValues.IsNullOrEmpty(raw) == true)
        {
            context.Response.Headers.TryGetValue(GrpcStatusHeader, out raw);
        }

        if (StringValues.IsNullOrEmpty(raw) == true)
        {
            return null;
        }

        if (int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) == false)
        {
            return null;
        }

        // The tag value has to come from a closed set. Anything outside the gRPC status enum is
        // dropped rather than tagged, so a malformed or forged header cannot grow the series count.
        if (Enum.IsDefined(typeof(StatusCode), code) == false)
        {
            return null;
        }

        return code;
    }
}
