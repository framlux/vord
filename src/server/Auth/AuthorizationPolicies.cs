// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Auth;

internal static class AuthorizationPolicies
{
    public const string GlobalAdminPolicyName = "Admin";
    public const string TenantAdminPolicyName = "TenantAdmin";
    public const string MachineAdminPolicyName = "MachineAdmin";
    public const string MachineViewOnlyPolicyName = "ViewOnly";
}
