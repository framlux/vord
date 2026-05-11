// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Validates the <see cref="TenantSwitchRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class TenantSwitchValidator : Validator<TenantSwitchRequest>
{
    /// <summary>
    /// Initializes validation rules for the tenant switch request.
    /// </summary>
    public TenantSwitchValidator()
    {
        RuleFor(x => x.TenantId)
            .GreaterThan(0)
            .WithMessage("Tenant ID must be a positive integer");
    }
}
