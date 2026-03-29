// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Configuration options for OAuth authentication providers.
/// </summary>
public sealed class AuthenticationProviderOptions
{
    /// <summary>
    /// GitHub OAuth provider settings.
    /// </summary>
    public OAuthProviderOptions GitHub { get; set; } = new();

    /// <summary>
    /// Google OAuth provider settings.
    /// </summary>
    public OAuthProviderOptions Google { get; set; } = new();

    /// <summary>
    /// Microsoft OAuth provider settings.
    /// </summary>
    public OAuthProviderOptions Microsoft { get; set; } = new();
}
