// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Validates the <see cref="MachineAuthorizedKeyAddRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class MachineAuthorizedKeyAddValidator : Validator<MachineAuthorizedKeyAddRequest>
{
    /// <summary>
    /// Initializes validation rules for the authorized key add request.
    /// </summary>
    public MachineAuthorizedKeyAddValidator()
    {
        RuleFor(x => x.SigningKeyId)
            .GreaterThan(0)
            .WithMessage("Signing key ID must be a positive integer");
    }
}
