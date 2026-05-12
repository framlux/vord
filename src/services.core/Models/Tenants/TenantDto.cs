// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Tenants;

/// <summary>
/// Tenant data returned to the UI.
/// </summary>
public sealed class TenantDto
{
    /// <summary>
    /// The tenant's unique identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The tenant's name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The tenant's logo URL.
    /// </summary>
    public string LogoUrl { get; set; } = string.Empty;

    /// <summary>
    /// Whether the tenant is active.
    /// </summary>
    public bool IsActive { get; set; }
}
