// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Users;

/// <summary>
/// A tenant and role assigned to a user.
/// </summary>
public sealed class UserTenantDto
{
    /// <summary>
    /// The unique identifier for the tenant.
    /// </summary>
    public required int TenantId { get; set; }

    /// <summary>
    /// The name of the tenant.
    /// </summary>
    public required string TenantName { get; set; }

    /// <summary>
    /// The role assigned to the user within the tenant.
    /// </summary>
    public required string Role { get; set; }
}
