// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// A stand-in for the response trailers a real HTTP/2 response carries, so a unit test can place a
/// gRPC status where the server would have written one.
/// </summary>
public sealed class StubHttpResponseTrailersFeature : IHttpResponseTrailersFeature
{
    /// <inheritdoc />
    public IHeaderDictionary Trailers { get; set; } = new HeaderDictionary();
}
