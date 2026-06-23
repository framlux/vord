// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.AspNetCore.Authentication;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// A test stub for <see cref="ISecureDataFormat{T}"/> that unprotects a single known protected value
/// back to a known plaintext, mirroring how the OpenID Connect handler binds a nonce to a cookie at
/// challenge time. Any other input unprotects to null.
/// </summary>
public sealed class StubStringDataFormat : ISecureDataFormat<string>
{
    private readonly string _protectedValue;
    private readonly string _plaintext;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubStringDataFormat"/> class.
    /// </summary>
    /// <param name="protectedValue">The protected cookie value this stub recognizes.</param>
    /// <param name="plaintext">The plaintext that <paramref name="protectedValue"/> unprotects to.</param>
    public StubStringDataFormat(string protectedValue, string plaintext)
    {
        _protectedValue = protectedValue;
        _plaintext = plaintext;
    }

    /// <inheritdoc/>
    public string Protect(string data)
    {
        return _protectedValue;
    }

    /// <inheritdoc/>
    public string Protect(string data, string? purpose)
    {
        return _protectedValue;
    }

    /// <inheritdoc/>
    public string? Unprotect(string? protectedText)
    {
        return string.Equals(protectedText, _protectedValue, StringComparison.Ordinal)
            ? _plaintext
            : null;
    }

    /// <inheritdoc/>
    public string? Unprotect(string? protectedText, string? purpose)
    {
        return Unprotect(protectedText);
    }
}
