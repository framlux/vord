// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.AspNetCore.DataProtection;

namespace Framlux.FleetManagement.Server.Services.Security;

/// <summary>
/// Encrypts and decrypts OIDC client secrets using ASP.NET Data Protection API.
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
        _protector = provider.CreateProtector("OidcClientSecret");
    }

    /// <inheritdoc/>
    public string Protect(string plaintext)
    {
        return _protector.Protect(plaintext);
    }

    /// <inheritdoc/>
    public string Unprotect(string protectedText)
    {
        return _protector.Unprotect(protectedText);
    }
}
