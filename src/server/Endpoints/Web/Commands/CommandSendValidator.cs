// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.RegularExpressions;
using FastEndpoints;
using FluentValidation;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Commands;

/// <summary>
/// Validates the <see cref="CommandSendRequest"/> before the endpoint handler executes.
/// </summary>
public sealed partial class CommandSendValidator : Validator<CommandSendRequest>
{
    [GeneratedRegex(@"^[0-9a-fA-F]{32}$")]
    private static partial Regex NoncePattern();

    /// <summary>
    /// Initializes validation rules for the command send request.
    /// </summary>
    public CommandSendValidator()
    {
        RuleFor(x => x.CommandId)
            .NotEmpty()
            .WithMessage("Command ID is required");

        RuleFor(x => x.CommandId)
            .Must(id => Guid.TryParse(id, out _))
            .WithMessage("Command ID must be a valid UUID")
            .When(x => string.IsNullOrEmpty(x.CommandId) == false);

        RuleFor(x => x.MachineId)
            .GreaterThan(0)
            .WithMessage("Machine ID must be greater than zero");

        RuleFor(x => x.SigningKeyId)
            .GreaterThan(0)
            .WithMessage("Signing key ID must be greater than zero");

        RuleFor(x => x.CommandType)
            .NotEmpty()
            .WithMessage("Command type is required");

        RuleFor(x => x.Nonce)
            .NotEmpty()
            .WithMessage("Nonce is required");

        RuleFor(x => x.Nonce)
            .Must(nonce => NoncePattern().IsMatch(nonce))
            .WithMessage("Nonce must be 32 hex characters")
            .When(x => string.IsNullOrEmpty(x.Nonce) == false);

        RuleFor(x => x.Signature)
            .NotEmpty()
            .WithMessage("Signature is required");

        RuleFor(x => x.Signature)
            .Must(sig => IsValidBase64(sig))
            .WithMessage("Signature must be valid Base64")
            .When(x => string.IsNullOrEmpty(x.Signature) == false);

        RuleFor(x => x.CanonicalPayload)
            .NotEmpty()
            .WithMessage("Canonical payload is required");

        RuleFor(x => x.ExpiresAt)
            .GreaterThan(x => x.Timestamp)
            .WithMessage("Expiry must be after timestamp");
    }

    private static bool IsValidBase64(string value)
    {
        try
        {
            Convert.FromBase64String(value);

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
