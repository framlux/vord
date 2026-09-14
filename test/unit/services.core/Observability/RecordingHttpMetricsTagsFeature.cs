// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.AspNetCore.Http.Features;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// A stand-in for the request-duration tag feature ASP.NET Core exposes, capturing whatever the
/// middleware writes onto it. The framework's own implementation is internal, so a double is the
/// only way to read the tags back in a unit test.
/// </summary>
public sealed class RecordingHttpMetricsTagsFeature : IHttpMetricsTagsFeature
{
    /// <inheritdoc />
    public ICollection<KeyValuePair<string, object?>> Tags { get; } = new List<KeyValuePair<string, object?>>();

    /// <inheritdoc />
    public bool MetricsDisabled { get; set; }
}
