// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Authorization policy names shared between the registrations in Program.cs and every
/// endpoint's Configure() call, so a misspelled policy is a compile error instead of a
/// runtime authorization failure.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>Global administrators (system-wide admin flag).</summary>
    public const string Admin = "Admin";

    /// <summary>Tenant administrators.</summary>
    public const string TenantAdmin = "TenantAdmin";

    /// <summary>Machine administrators (includes tenant administrators).</summary>
    public const string MachineAdmin = "MachineAdmin";

    /// <summary>Any tenant role with read access (viewer and above).</summary>
    public const string ViewOnly = "ViewOnly";
}
