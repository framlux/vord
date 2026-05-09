// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// Validates the <see cref="DowngradeSubscriptionRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class DowngradeSubscriptionValidator : Validator<DowngradeSubscriptionRequest>
{
    /// <summary>
    /// Initializes validation rules for the downgrade subscription request.
    /// </summary>
    public DowngradeSubscriptionValidator()
    {
        RuleFor(x => x.TargetTier)
            .NotEmpty()
            .WithMessage("Target tier is required")
            .Must(tier => IsValidTargetTier(tier))
            .WithMessage("Target tier must be 'free' or 'pro'.");
    }

    private static bool IsValidTargetTier(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier))
        {
            return false;
        }

        string normalized = tier.ToLowerInvariant();

        return string.Equals(normalized, "free", StringComparison.Ordinal) ||
               string.Equals(normalized, "pro", StringComparison.Ordinal);
    }
}
