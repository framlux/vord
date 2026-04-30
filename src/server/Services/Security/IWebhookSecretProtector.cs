// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Security;

/// <summary>
/// Encrypts and decrypts webhook endpoint secrets for at-rest protection.
/// </summary>
public interface IWebhookSecretProtector
{
    /// <summary>
    /// Encrypts a plaintext webhook secret for storage.
    /// </summary>
    /// <param name="plaintext">The plaintext secret.</param>
    /// <returns>The encrypted string.</returns>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts a stored webhook secret.
    /// </summary>
    /// <param name="protectedText">The encrypted secret.</param>
    /// <returns>The plaintext secret.</returns>
    string Unprotect(string protectedText);
}
