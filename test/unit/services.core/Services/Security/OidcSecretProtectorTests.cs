// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Unit tests for <see cref="OidcSecretProtector"/>.
/// Uses EphemeralDataProtectionProvider for real encryption without file persistence.
/// </summary>
public sealed class OidcSecretProtectorTests
{
    private static OidcSecretProtector CreateProtector()
    {
        EphemeralDataProtectionProvider provider = new();

        return new OidcSecretProtector(provider);
    }

    [Test]
    public async Task Protect_ThenUnprotect_ReturnsOriginalSecret()
    {
        OidcSecretProtector protector = CreateProtector();
        string original = "my-super-secret-client-secret";

        string encrypted = protector.Protect(original);
        string decrypted = protector.Unprotect(encrypted);

        await Assert.That(decrypted).IsEqualTo(original);
    }

    [Test]
    public async Task Protect_ProducesCiphertext_DifferentFromPlaintext()
    {
        OidcSecretProtector protector = CreateProtector();
        string plaintext = "another-secret-value";

        string ciphertext = protector.Protect(plaintext);

        await Assert.That(ciphertext).IsNotEqualTo(plaintext);
    }

    [Test]
    public async Task Unprotect_TamperedInput_ThrowsException()
    {
        OidcSecretProtector protector = CreateProtector();
        string original = "secret-to-tamper";
        string encrypted = protector.Protect(original);

        // Tamper with the ciphertext by replacing a character
        string tampered = encrypted + "TAMPERED";

        await Assert.That(() => protector.Unprotect(tampered)).ThrowsException();
    }

    [Test]
    public async Task Protect_EmptyString_RoundTripsCorrectly()
    {
        OidcSecretProtector protector = CreateProtector();
        string empty = "";

        string encrypted = protector.Protect(empty);
        string decrypted = protector.Unprotect(encrypted);

        await Assert.That(decrypted).IsEqualTo(empty);
    }

    [Test]
    public async Task Unprotect_CompletelyInvalidInput_ThrowsException()
    {
        OidcSecretProtector protector = CreateProtector();
        string garbage = "this-is-not-valid-ciphertext-at-all-!!@@##";

        await Assert.That(() => protector.Unprotect(garbage)).ThrowsException();
    }
}
