// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.SigningKeys;

/// <summary>
/// Validates the <see cref="SigningKeyRegisterRequest"/> before the endpoint handler executes.
/// </summary>
public sealed class SigningKeyRegisterValidator : Validator<SigningKeyRegisterRequest>
{
    /// <summary>
    /// Initializes validation rules for the signing key registration request.
    /// </summary>
    public SigningKeyRegisterValidator()
    {
        RuleFor(x => x.Label)
            .NotEmpty()
            .WithMessage("Label is required")
            .MaximumLength(250)
            .WithMessage("Label must be 250 characters or fewer");

        RuleFor(x => x.PublicKey)
            .NotEmpty()
            .WithMessage("Public key is required");

        RuleFor(x => x.PublicKey)
            .Must(key => IsValidEd25519PublicKey(key))
            .WithMessage("Public key must be a valid Base64-encoded 32-byte Ed25519 key")
            .When(x => string.IsNullOrEmpty(x.PublicKey) == false);
    }

    private static bool IsValidEd25519PublicKey(string value)
    {
        try
        {
            byte[] decoded = Convert.FromBase64String(value);

            return decoded.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
