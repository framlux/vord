// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.AspNetCore.Builder;

namespace Framlux.FleetManagement.Services.Core.Extensions;

/// <summary>
/// Pipeline registrations for the observability instruments that can only be fed from inside a
/// request.
/// </summary>
public static class ObservabilityApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the middleware that tags the built-in request duration metric with each gRPC call's
    /// status.
    /// </summary>
    /// <param name="app">The application pipeline builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// Place this early, ahead of authentication and routing. Anything it does not wrap answers
    /// requests whose gRPC status it will never see, and a rejection issued by middleware is
    /// exactly the systemic failure an error-rate rule is meant to catch.
    /// </remarks>
    public static IApplicationBuilder UseGrpcStatusMetricTag(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<GrpcStatusMetricTagMiddleware>();
    }
}
