// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Tenants;

/// <summary>
/// DTO for tenant OIDC configuration.
/// </summary>
public sealed class TenantOidcConfigDto
{
    /// <summary>The OIDC authority URL.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>The OIDC client ID.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The OIDC client secret (write-only, masked on read).</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Optional metadata address.</summary>
    public string? MetadataAddress { get; set; }

    /// <summary>The email domain for SSO discovery (e.g. "example.com").</summary>
    public string EmailDomain { get; set; } = string.Empty;

    /// <summary>Whether the configuration is enabled.</summary>
    public bool IsEnabled { get; set; }
}
