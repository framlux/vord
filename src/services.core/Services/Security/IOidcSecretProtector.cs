// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Security;

/// <summary>
/// Encrypts and decrypts OIDC client secrets for at-rest protection.
/// Protected values carry a marker prefix so legacy plaintext values written before
/// encryption was enforced can be detected and migrated without ambiguity.
/// </summary>
public interface IOidcSecretProtector
{
    /// <summary>
    /// Marker prefix written on every protected value. Any stored value lacking this
    /// prefix is treated as legacy plaintext awaiting migration.
    /// </summary>
    const string ProtectedMarker = "prot1:";

    /// <summary>
    /// Encrypts a plaintext client secret for storage. The returned value begins with
    /// <see cref="ProtectedMarker"/>.
    /// </summary>
    /// <param name="plaintext">The plaintext secret.</param>
    /// <returns>The marker-prefixed encrypted string.</returns>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a stored client secret. The input MUST begin with
    /// <see cref="ProtectedMarker"/>; callers should check
    /// <see cref="IsProtected"/> first and treat unprefixed values as legacy plaintext.
    /// </summary>
    /// <param name="protectedText">The encrypted secret, including the marker prefix.</param>
    /// <returns>The plaintext secret.</returns>
    string Unprotect(string protectedText);

    /// <summary>
    /// Returns whether the supplied value carries the protected-marker prefix.
    /// Use this to distinguish a properly-encrypted value from a legacy plaintext one
    /// without catching <see cref="System.Security.Cryptography.CryptographicException"/>
    /// (which would also fire on corrupt ciphertext and key-ring rotation).
    /// </summary>
    /// <param name="storedValue">The value as read from storage.</param>
    /// <returns><c>true</c> if the value begins with <see cref="ProtectedMarker"/>.</returns>
    bool IsProtected(string? storedValue);
}
