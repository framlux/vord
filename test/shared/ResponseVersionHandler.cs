// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Delegating handler that sets the response HTTP version to match the request version.
/// Required for gRPC functional tests because the in-memory test server returns HTTP/1.1
/// responses, but gRPC clients expect HTTP/2.
/// </summary>
public sealed class ResponseVersionHandler : DelegatingHandler
{
    /// <summary>
    /// Sends the request and adjusts the response version to match the request version.
    /// </summary>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
        response.Version = request.Version;

        return response;
    }
}
