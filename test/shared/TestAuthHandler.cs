// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Test authentication handler that reads user identity from custom request headers.
/// Replaces the cookie authentication handler in functional tests so that REST endpoints
/// can be exercised without a real OAuth flow.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>
    /// Header name for the test user ID (maps to <see cref="ClaimTypes.Actor"/>).
    /// </summary>
    public const string UserIdHeader = "X-Test-UserId";

    /// <summary>
    /// Header name for the external ID (maps to <see cref="ClaimTypes.NameIdentifier"/>).
    /// </summary>
    public const string ExternalIdHeader = "X-Test-ExternalId";

    /// <summary>
    /// Header name for the email (maps to <see cref="ClaimTypes.Email"/>).
    /// </summary>
    public const string EmailHeader = "X-Test-Email";

    /// <summary>
    /// Header name for the global admin flag (maps to "iga" claim).
    /// </summary>
    public const string IsGlobalAdminHeader = "X-Test-IsGlobalAdmin";

    /// <summary>
    /// Header name for roles. Comma-separated values in the format "tenantId:roleId".
    /// </summary>
    public const string RolesHeader = "X-Test-Roles";

    /// <summary>
    /// Creates a new instance of the <see cref="TestAuthHandler"/> class.
    /// </summary>
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <inheritdoc/>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // If no test headers are present, fall through so API key auth can handle the request
        if (Request.Headers.ContainsKey(UserIdHeader) == false)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        List<Claim> claims = new();

        string? userId = Request.Headers[UserIdHeader];
        if (string.IsNullOrEmpty(userId) == false)
        {
            claims.Add(new Claim(ClaimTypes.Actor, userId));
        }

        string? externalId = Request.Headers[ExternalIdHeader];
        if (string.IsNullOrEmpty(externalId) == false)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, externalId));
        }

        string? email = Request.Headers[EmailHeader];
        if (string.IsNullOrEmpty(email) == false)
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        string? isGlobalAdmin = Request.Headers[IsGlobalAdminHeader];
        claims.Add(new Claim("iga", string.Equals(isGlobalAdmin, "true", StringComparison.OrdinalIgnoreCase)
            ? bool.TrueString
            : bool.FalseString));

        string? roles = Request.Headers[RolesHeader];
        if (string.IsNullOrEmpty(roles) == false)
        {
            foreach (string role in roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        // Use CookieAuthenticationDefaults.AuthenticationScheme as the authentication type
        // so policies that require AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
        // are satisfied.
        ClaimsIdentity identity = new(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        ClaimsPrincipal principal = new(identity);
        AuthenticationTicket ticket = new(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
