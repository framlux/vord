// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests that verify the protected-marker prefix contract on
/// <see cref="OidcSecretProtector"/>. These guarantees let callers distinguish a properly
/// protected value from a legacy plaintext one without resorting to catching
/// <see cref="System.Security.Cryptography.CryptographicException"/>.
/// </summary>
public sealed class OidcSecretProtectorMarkerTests
{
    private static OidcSecretProtector CreateProtector()
    {
        return new OidcSecretProtector(new EphemeralDataProtectionProvider());
    }

    [Test]
    public async Task Protect_OutputStartsWithMarkerPrefix()
    {
        OidcSecretProtector protector = CreateProtector();

        string output = protector.Protect("hello");

        await Assert.That(output.StartsWith("prot1:", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task IsProtected_ReturnsTrueForMarkerPrefixedValue()
    {
        OidcSecretProtector protector = CreateProtector();
        string protectedValue = protector.Protect("secret");

        bool result = protector.IsProtected(protectedValue);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsProtected_ReturnsFalseForPlaintext()
    {
        OidcSecretProtector protector = CreateProtector();

        bool result = protector.IsProtected("just-a-plain-string");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsProtected_ReturnsFalseForNull()
    {
        OidcSecretProtector protector = CreateProtector();

        bool result = protector.IsProtected(null);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsProtected_ReturnsFalseForEmpty()
    {
        OidcSecretProtector protector = CreateProtector();

        bool result = protector.IsProtected(string.Empty);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task Unprotect_OnPlaintextValueWithoutMarker_ThrowsInvalidOperation()
    {
        OidcSecretProtector protector = CreateProtector();

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            protector.Unprotect("legacy-plain-text");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("protected-marker");
    }

    [Test]
    public async Task Protect_ThenUnprotect_RoundTripsCorrectly_WithMarker()
    {
        OidcSecretProtector protector = CreateProtector();
        string original = "my-super-secret";

        string protectedValue = protector.Protect(original);
        string roundTripped = protector.Unprotect(protectedValue);

        await Assert.That(roundTripped).IsEqualTo(original);
    }

    [Test]
    public async Task Constructor_NullProvider_ThrowsArgumentNullException()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            OidcSecretProtector _ = new(null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Protect_NullInput_ThrowsArgumentNullException()
    {
        OidcSecretProtector protector = CreateProtector();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            protector.Protect(null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Unprotect_NullInput_ThrowsArgumentNullException()
    {
        OidcSecretProtector protector = CreateProtector();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            protector.Unprotect(null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }
}
