// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Microsoft.AspNetCore.Authorization;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Authorization requirement specifying allowed user roles.
/// </summary>
public sealed class AllowedRolesRequirement : IAuthorizationRequirement
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AllowedRolesRequirement"/> class.
    /// </summary>
    /// <param name="allowedRoles">The user roles that are allowed.</param>
    public AllowedRolesRequirement(params UserAccountRoles[] allowedRoles)
        => Allowed = allowedRoles;

    /// <summary>
    /// Gets the collection of allowed user roles.
    /// </summary>
    public IReadOnlyCollection<UserAccountRoles> Allowed { get; }
}
