// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Server.Endpoints.Web.SigningKeys;

namespace Framlux.FleetManagement.Test.Endpoints.Web.SigningKeys;

/// <summary>
/// Unit tests for <see cref="SigningKeyRegisterValidator"/>.
/// </summary>
public sealed class SigningKeyRegisterValidatorTests
{
    private readonly SigningKeyRegisterValidator _validator = new();

    private static SigningKeyRegisterRequest ValidRequest()
    {
        return new SigningKeyRegisterRequest
        {
            Label = "Work MacBook",
            PublicKey = Convert.ToBase64String(new byte[32]),
        };
    }

    [Test]
    public async Task ValidRequest_PassesValidation()
    {
        ValidationResult result = await _validator.ValidateAsync(ValidRequest());

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task EmptyLabel_FailsValidation()
    {
        SigningKeyRegisterRequest request = ValidRequest();
        request.Label = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Label is required")).IsTrue();
    }

    [Test]
    public async Task LabelExceeds250Characters_FailsValidation()
    {
        SigningKeyRegisterRequest request = ValidRequest();
        request.Label = new string('A', 251);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Label must be 250 characters or fewer")).IsTrue();
    }

    [Test]
    public async Task EmptyPublicKey_FailsValidation()
    {
        SigningKeyRegisterRequest request = ValidRequest();
        request.PublicKey = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Public key is required")).IsTrue();
    }

    [Test]
    public async Task InvalidBase64PublicKey_FailsValidation()
    {
        SigningKeyRegisterRequest request = ValidRequest();
        request.PublicKey = "not!!valid##base64";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Public key must be a valid Base64-encoded 32-byte Ed25519 key")).IsTrue();
    }

    [Test]
    public async Task WrongSizePublicKey_FailsValidation()
    {
        SigningKeyRegisterRequest request = ValidRequest();
        request.PublicKey = Convert.ToBase64String(new byte[16]);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Public key must be a valid Base64-encoded 32-byte Ed25519 key")).IsTrue();
    }

    [Test]
    public async Task PublicKeyTooLarge_FailsValidation()
    {
        SigningKeyRegisterRequest request = ValidRequest();
        request.PublicKey = Convert.ToBase64String(new byte[64]);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Public key must be a valid Base64-encoded 32-byte Ed25519 key")).IsTrue();
    }

    [Test]
    public async Task ValidBase64CorrectSize_PassesValidation()
    {
        SigningKeyRegisterRequest request = ValidRequest();
        byte[] keyBytes = new byte[32];
        Random.Shared.NextBytes(keyBytes);
        request.PublicKey = Convert.ToBase64String(keyBytes);

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }
}
