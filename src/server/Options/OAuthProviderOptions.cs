// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// OAuth provider credentials for a single provider.
/// </summary>
public sealed class OAuthProviderOptions
{
    /// <summary>
    /// The OAuth client ID.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// The OAuth client secret.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;
}
