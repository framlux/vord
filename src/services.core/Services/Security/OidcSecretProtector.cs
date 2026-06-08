// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.AspNetCore.DataProtection;

namespace Framlux.FleetManagement.Services.Core.Security;

/// <summary>
/// Encrypts and decrypts OIDC client secrets using ASP.NET Data Protection API.
/// Adds a fixed marker prefix to every protected value so legacy plaintext values
/// written before encryption was enforced are detectable without catching
/// cryptographic exceptions.
/// </summary>
public sealed class OidcSecretProtector : IOidcSecretProtector
{
    private readonly IDataProtector _protector;

    /// <summary>
    /// Creates a new instance of the <see cref="OidcSecretProtector"/> class.
    /// </summary>
    /// <param name="provider">The data protection provider.</param>
    public OidcSecretProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector("OidcClientSecret");
    }

    /// <inheritdoc/>
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        return IOidcSecretProtector.ProtectedMarker + _protector.Protect(plaintext);
    }

    /// <inheritdoc/>
    public string Unprotect(string protectedText)
    {
        ArgumentNullException.ThrowIfNull(protectedText);

        if (protectedText.StartsWith(IOidcSecretProtector.ProtectedMarker, StringComparison.Ordinal) == false)
        {
            throw new InvalidOperationException(
                "Unprotect called on a value lacking the protected-marker prefix. "
                + "Callers must check IsProtected first and route legacy values to migration.");
        }

        string ciphertext = protectedText.Substring(IOidcSecretProtector.ProtectedMarker.Length);

        return _protector.Unprotect(ciphertext);
    }

    /// <inheritdoc/>
    public bool IsProtected(string? storedValue)
    {
        if (storedValue is null)
        {
            return false;
        }

        return storedValue.StartsWith(IOidcSecretProtector.ProtectedMarker, StringComparison.Ordinal);
    }
}
