// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Configuration options for internal gRPC authentication. Supports a single legacy key (the
/// <see cref="Key"/> property — implicitly bound to key id <c>default</c>) plus an optional
/// rotation map of <see cref="Keys"/> keyed by kid. The validator consults <see cref="Keys"/>
/// first; if the request carries no <c>x-internal-kid</c> header it falls back to
/// <see cref="Key"/>.
/// </summary>
public sealed class InternalApiOptions
{
    /// <summary>
    /// The default shared key (kid = <c>default</c>). Retained as a stand-alone property so
    /// existing single-key configurations remain valid without restructuring.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Rotation map: <c>kid → secret</c>. Adding an entry enables hitless rotation — both the
    /// outgoing-retiring key and the new key can be valid simultaneously, then the retiring key
    /// is removed in a follow-up deploy. When the request supplies <c>x-internal-kid</c> the
    /// matching secret is looked up here; otherwise the validator falls through to
    /// <see cref="Key"/>.
    /// </summary>
    public Dictionary<string, string> Keys { get; set; } = new();
}
