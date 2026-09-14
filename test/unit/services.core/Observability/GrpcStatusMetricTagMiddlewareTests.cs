// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Covers where the gRPC status is read from and, just as importantly, what is refused: the tag
/// value has to stay inside the gRPC status enum, because a value taken from a response header is
/// the one thing in this design that could grow the series count without bound.
/// </summary>
public sealed class GrpcStatusMetricTagMiddlewareTests
{
    [Test]
    public async Task StatusInATrailer_IsTaggedOntoTheRequest()
    {
        // A call that wrote any response body reports its status in a trailer. This is both the
        // success case and the failure that happened mid-stream.
        DefaultHttpContext context = BuildContext(out RecordingHttpMetricsTagsFeature tags);
        context.Features.Get<IHttpResponseTrailersFeature>()!.Trailers["grpc-status"] = "0";

        await Invoke(context);

        await Assert.That(tags.Tags.Count).IsEqualTo(1);
        await Assert.That(tags.Tags.First().Key).IsEqualTo("rpc.grpc.status_code");
        await Assert.That(tags.Tags.First().Value).IsEqualTo(0);
    }

    [Test]
    public async Task StatusInAResponseHeader_IsTaggedOntoTheRequest()
    {
        // A unary call that failed before writing a body is sent as a trailers-only response, which
        // puts the status in the headers. Reading trailers alone would miss most real failures.
        DefaultHttpContext context = BuildContext(out RecordingHttpMetricsTagsFeature tags);
        context.Response.Headers["grpc-status"] = ((int)StatusCode.PermissionDenied).ToString();

        await Invoke(context);

        await Assert.That(tags.Tags.Count).IsEqualTo(1);
        await Assert.That(tags.Tags.First().Value).IsEqualTo((int)StatusCode.PermissionDenied);
    }

    [Test]
    public async Task TrailerTakesPrecedenceOverAHeader()
    {
        // Where both exist the trailer is the authoritative one, because it is written last.
        DefaultHttpContext context = BuildContext(out RecordingHttpMetricsTagsFeature tags);
        context.Response.Headers["grpc-status"] = "0";
        context.Features.Get<IHttpResponseTrailersFeature>()!.Trailers["grpc-status"] =
            ((int)StatusCode.Unavailable).ToString();

        await Invoke(context);

        await Assert.That(tags.Tags.Count).IsEqualTo(1);
        await Assert.That(tags.Tags.First().Value).IsEqualTo((int)StatusCode.Unavailable);
    }

    [Test]
    public async Task RequestWithNoGrpcStatus_IsLeftUntagged()
    {
        // Ordinary web traffic must keep the series shape it already has.
        DefaultHttpContext context = BuildContext(out RecordingHttpMetricsTagsFeature tags);

        await Invoke(context);

        await Assert.That(tags.Tags.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyStatusValue_IsLeftUntagged()
    {
        DefaultHttpContext context = BuildContext(out RecordingHttpMetricsTagsFeature tags);
        context.Response.Headers["grpc-status"] = string.Empty;

        await Invoke(context);

        await Assert.That(tags.Tags.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("not-a-number")]
    [Arguments("17")]
    [Arguments("-1")]
    [Arguments("99999")]
    public async Task StatusOutsideTheGrpcEnum_IsRefused(string value)
    {
        // Sixteen is the highest defined gRPC status. Anything else is either malformed or forged,
        // and tagging it would let a caller mint an unbounded number of series.
        DefaultHttpContext context = BuildContext(out RecordingHttpMetricsTagsFeature tags);
        context.Response.Headers["grpc-status"] = value;

        await Invoke(context);

        await Assert.That(tags.Tags.Count).IsEqualTo(0);
    }

    [Test]
    public async Task HighestDefinedStatus_IsStillAccepted()
    {
        // The boundary the refusal test sits just above, so the two together pin the exact edge.
        DefaultHttpContext context = BuildContext(out RecordingHttpMetricsTagsFeature tags);
        context.Response.Headers["grpc-status"] = ((int)StatusCode.Unauthenticated).ToString();

        await Invoke(context);

        await Assert.That(tags.Tags.Count).IsEqualTo(1);
        await Assert.That(tags.Tags.First().Value).IsEqualTo((int)StatusCode.Unauthenticated);
    }

    [Test]
    public async Task NoTagFeature_IsNotAnError()
    {
        // The feature is absent whenever nothing is collecting the instrument, which is the normal
        // state of a deployment with no collector configured.
        DefaultHttpContext context = new();
        context.Features.Set<IHttpResponseTrailersFeature>(new StubHttpResponseTrailersFeature());
        context.Response.Headers["grpc-status"] = "0";

        await Invoke(context);

        await Assert.That(context.Features.Get<IHttpMetricsTagsFeature>()).IsNull();
    }

    [Test]
    public async Task NoTrailersFeature_FallsBackToTheHeaders()
    {
        // Not every server surfaces a trailers feature, and the status still has to be found.
        DefaultHttpContext context = new();
        RecordingHttpMetricsTagsFeature tags = new();
        context.Features.Set<IHttpMetricsTagsFeature>(tags);
        context.Response.Headers["grpc-status"] = ((int)StatusCode.Internal).ToString();

        await Invoke(context);

        await Assert.That(tags.Tags.Count).IsEqualTo(1);
        await Assert.That(tags.Tags.First().Value).IsEqualTo((int)StatusCode.Internal);
    }

    [Test]
    public async Task NullNext_IsRejected()
    {
        await Assert.That(() =>
        {
            _ = new GrpcStatusMetricTagMiddleware(null!);
        }).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task NullContext_IsRejected()
    {
        GrpcStatusMetricTagMiddleware middleware = new(_ => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await middleware.InvokeAsync(null!));
    }

    /// <summary>
    /// Runs the middleware over a context whose inner pipeline does nothing, which is what lets the
    /// test decide exactly what the response looks like by the time the tag is read.
    /// </summary>
    private static async Task Invoke(HttpContext context)
    {
        GrpcStatusMetricTagMiddleware middleware = new(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);
    }

    private static DefaultHttpContext BuildContext(out RecordingHttpMetricsTagsFeature tags)
    {
        tags = new RecordingHttpMetricsTagsFeature();

        DefaultHttpContext context = new();
        context.Features.Set<IHttpMetricsTagsFeature>(tags);
        context.Features.Set<IHttpResponseTrailersFeature>(new StubHttpResponseTrailersFeature());

        return context;
    }
}
