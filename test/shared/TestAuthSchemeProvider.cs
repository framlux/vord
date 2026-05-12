// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Custom <see cref="IAuthenticationSchemeProvider"/> that redirects the cookie authentication
/// scheme to use <see cref="TestAuthHandler"/> instead of the real cookie handler.
/// All other schemes (API key, MultiAuth, OAuth providers) are unchanged.
/// </summary>
public sealed class TestAuthSchemeProvider : AuthenticationSchemeProvider
{
    private readonly AuthenticationScheme _testCookieScheme;

    /// <summary>
    /// Creates a new instance of the <see cref="TestAuthSchemeProvider"/> class.
    /// </summary>
    public TestAuthSchemeProvider(IOptions<AuthenticationOptions> options)
        : base(options)
    {
        _testCookieScheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme,
            CookieAuthenticationDefaults.AuthenticationScheme,
            typeof(TestAuthHandler));
    }

    /// <inheritdoc/>
    public override Task<AuthenticationScheme?> GetSchemeAsync(string name)
    {
        if (string.Equals(name, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.Ordinal))
        {
            return Task.FromResult<AuthenticationScheme?>(_testCookieScheme);
        }

        return base.GetSchemeAsync(name);
    }

    /// <inheritdoc/>
    public override Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync()
    {
        return Task.FromResult(ReplaceSchemes(base.GetAllSchemesAsync().GetAwaiter().GetResult()));
    }

    private IEnumerable<AuthenticationScheme> ReplaceSchemes(IEnumerable<AuthenticationScheme> schemes)
    {
        foreach (AuthenticationScheme scheme in schemes)
        {
            if (string.Equals(scheme.Name, CookieAuthenticationDefaults.AuthenticationScheme, StringComparison.Ordinal))
            {
                yield return _testCookieScheme;
            }
            else
            {
                yield return scheme;
            }
        }
    }
}
