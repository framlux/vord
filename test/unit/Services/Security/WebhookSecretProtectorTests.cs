// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Unit tests for <see cref="WebhookSecretProtector"/>.
/// Uses EphemeralDataProtectionProvider for real encryption without file persistence.
/// </summary>
public sealed class WebhookSecretProtectorTests
{
    private static WebhookSecretProtector CreateProtector()
    {
        EphemeralDataProtectionProvider provider = new();

        return new WebhookSecretProtector(provider);
    }

    [Test]
    public async Task Protect_Unprotect_RoundTrips()
    {
        WebhookSecretProtector protector = CreateProtector();
        string original = "whsec_my-webhook-signing-secret-12345";

        string encrypted = protector.Protect(original);
        string decrypted = protector.Unprotect(encrypted);

        await Assert.That(decrypted).IsEqualTo(original);
    }

    [Test]
    public async Task Protect_ProducesDifferentOutput()
    {
        WebhookSecretProtector protector = CreateProtector();
        string plaintext = "webhook-secret-that-must-be-encrypted";

        string ciphertext = protector.Protect(plaintext);

        await Assert.That(ciphertext).IsNotEqualTo(plaintext);
    }

    [Test]
    public async Task Protect_DifferentInputs_DifferentOutputs()
    {
        WebhookSecretProtector protector = CreateProtector();
        string secret1 = "first-webhook-secret";
        string secret2 = "second-webhook-secret";

        string encrypted1 = protector.Protect(secret1);
        string encrypted2 = protector.Protect(secret2);

        await Assert.That(encrypted1).IsNotEqualTo(encrypted2);
    }
}
