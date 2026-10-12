// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Kubernetes;

/// <summary>
/// Validates the <see cref="RenameClusterRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class RenameClusterValidator : Validator<RenameClusterRequest>
{
    private const int DisplayNameMaxLength = 100;

    /// <summary>
    /// Initializes validation rules for the rename cluster request.
    /// </summary>
    public RenameClusterValidator()
    {
        RuleFor(x => x.DisplayName)
            .Must(name => (name?.Trim().Length ?? 0) <= DisplayNameMaxLength)
            .WithMessage("Display name must be 100 characters or fewer");
    }
}
