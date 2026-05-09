// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;
using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

/// <summary>
/// Validates the <see cref="MemberRoleChangeRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class MemberRoleChangeValidator : Validator<MemberRoleChangeRequest>
{
    /// <summary>
    /// Initializes validation rules for the member role change request.
    /// </summary>
    public MemberRoleChangeValidator()
    {
        RuleFor(x => x.UserId)
            .GreaterThan(0)
            .WithMessage("User ID must be greater than zero");

        RuleFor(x => x.Role)
            .NotEmpty()
            .WithMessage("Role is required");

        RuleFor(x => x.Role)
            .Must(role => IsAssignableRole(role))
            .WithMessage("Role must be one of: TenantAdmin, MachineAdmin, Viewer")
            .When(x => string.IsNullOrEmpty(x.Role) == false);
    }

    private static bool IsAssignableRole(string? role)
    {
        if (Enum.TryParse<UserAccountRoles>(role, true, out UserAccountRoles parsed) == false)
        {
            return false;
        }

        return parsed is UserAccountRoles.TenantAdmin or UserAccountRoles.MachineAdmin or UserAccountRoles.Viewer;
    }
}
