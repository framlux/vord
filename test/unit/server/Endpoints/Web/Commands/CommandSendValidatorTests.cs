// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Services.Core.Models.Commands;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Commands;

/// <summary>
/// Unit tests for <see cref="CommandSendValidator"/>.
/// </summary>
public sealed class CommandSendValidatorTests
{
    private readonly CommandSendValidator _validator = new();

    private static CommandSendRequest ValidRequest()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return new CommandSendRequest
        {
            CommandId = Guid.NewGuid().ToString(),
            MachineId = 42,
            SigningKeyId = 1,
            CommandType = "reboot",
            Nonce = "0123456789abcdef0123456789abcdef",
            Signature = Convert.ToBase64String(new byte[64]),
            CanonicalPayload = "{\"action\":\"reboot\"}",
            Timestamp = now,
            ExpiresAt = now.AddMinutes(5),
        };
    }

    [Test]
    public async Task ValidRequest_PassesValidation()
    {
        ValidationResult result = await _validator.ValidateAsync(ValidRequest());

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task EmptyCommandId_FailsValidation()
    {
        CommandSendRequest request = ValidRequest();
        request.CommandId = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Command ID is required")).IsTrue();
    }

    [Test]
    public async Task InvalidUuidCommandId_FailsValidation()
    {
        CommandSendRequest request = ValidRequest();
        request.CommandId = "not-a-uuid";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Command ID must be a valid UUID")).IsTrue();
    }

    [Test]
    public async Task ZeroMachineId_FailsValidation()
    {
        CommandSendRequest request = ValidRequest();
        request.MachineId = 0;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Machine ID must be greater than zero")).IsTrue();
    }

    [Test]
    public async Task NegativeMachineId_FailsValidation()
    {
        CommandSendRequest request = ValidRequest();
        request.MachineId = -1;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Machine ID must be greater than zero")).IsTrue();
    }

    [Test]
    public async Task ZeroSigningKeyId_FailsValidation()
    {
        CommandSendRequest request = ValidRequest();
        request.SigningKeyId = 0;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Signing key ID must be greater than zero")).IsTrue();
    }

    [Test]
    public async Task EmptyCommandType_FailsValidation()
    {
        CommandSendRequest request = ValidRequest();
        request.CommandType = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Command type is required")).IsTrue();
    }

    [Test]
    public async Task EmptyNonce_FailsValidation()
    {
        CommandSendRequest request = ValidRequest();
        request.Nonce = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Nonce is required")).IsTrue();
    }

    [Test]
    public async Task InvalidNonce_TooShort_FailsValidation()
    {
        CommandSendRequest request = ValidRequest();
        request.Nonce = "abcdef12";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Nonce must be 32 hex characters")).IsTrue();
    }

    [Test]
    public async Task InvalidNonce_NonHex_FailsValidation()
    {
        CommandSendRequest request = ValidRequest();
        request.Nonce = "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Nonce must be 32 hex characters")).IsTrue();
    }

    [Test]
    public async Task EmptySignature_FailsValidation()
    {
        CommandSendRequest request = ValidRequest();
        request.Signature = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Signature is required")).IsTrue();
    }

    [Test]
    public async Task InvalidBase64Signature_FailsValidation()
    {
        CommandSendRequest request = ValidRequest();
        request.Signature = "not!!valid##base64";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Signature must be valid Base64")).IsTrue();
    }

    [Test]
    public async Task EmptyCanonicalPayload_FailsValidation()
    {
        CommandSendRequest request = ValidRequest();
        request.CanonicalPayload = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Canonical payload is required")).IsTrue();
    }

    [Test]
    public async Task ExpiresAtBeforeTimestamp_FailsValidation()
    {
        CommandSendRequest request = ValidRequest();
        request.Timestamp = DateTimeOffset.UtcNow;
        request.ExpiresAt = request.Timestamp.AddMinutes(-1);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Expiry must be after timestamp")).IsTrue();
    }

    [Test]
    public async Task ExpiresAtEqualsTimestamp_FailsValidation()
    {
        CommandSendRequest request = ValidRequest();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        request.Timestamp = now;
        request.ExpiresAt = now;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Expiry must be after timestamp")).IsTrue();
    }
}
