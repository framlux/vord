// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Users;

/// <summary>
/// User account data returned to admin views.
/// </summary>
public sealed class UserAccountDto
{
    /// <summary>
    /// The user's internal ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The user's username (email).
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Whether the user account is active.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Whether the user is a global administrator.
    /// </summary>
    public bool IsGlobalAdmin { get; set; }

    /// <summary>
    /// When the account was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The user's tenant roles.
    /// </summary>
    public List<UserTenantDto> Tenants { get; set; } = new();
}
