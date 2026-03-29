// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Security;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// OIDC events for tenant-specific custom OIDC providers (Team tier).
/// Dynamically configures OIDC settings per-tenant at challenge time.
/// Delegates to shared SocialAuthEvents logic for user provisioning.
/// All per-request state is kept on the protocol message or context — never on the shared Options singleton.
/// </summary>
public sealed class SsoOidcEvents : OpenIdConnectEvents
{
    /// <summary>
    /// Overrides the redirect to the identity provider to load tenant-specific OIDC configuration.
    /// Sets issuer address and client ID on the per-request protocol message without mutating shared Options.
    /// </summary>
    /// <param name="context">The redirect context.</param>
    /// <returns>Returns an awaitable Task.</returns>
    public override async Task RedirectToIdentityProvider(RedirectContext context)
    {
        if ((context.Properties.Items.TryGetValue("tenantId", out string? tenantIdStr) == false) ||
            (int.TryParse(tenantIdStr, out int tenantId) == false))
        {
            ILogger<SsoOidcEvents> logger = ResolveLogger(context.HttpContext);
            logger.LogWarning("OIDC redirect missing or invalid tenantId in authentication properties");
            context.Response.StatusCode = 400;
            context.HandleResponse();

            return;
        }

        TenantOidcConfiguration? config = await ResolveTenantOidcConfigAsync(context.HttpContext, tenantId);
        if (config is null)
        {
            ILogger<SsoOidcEvents> logger = ResolveLogger(context.HttpContext);
            logger.LogWarning("OIDC configuration not found for tenant {TenantId}", tenantId);
            context.Response.StatusCode = 400;
            context.HandleResponse();

            return;
        }

        // Discover the authorization endpoint from the tenant's IdP — per-request, no shared state mutation
        IHttpClientFactory httpClientFactory = context.HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>();
        OpenIdConnectConfiguration oidcDiscovery = await FetchDiscoveryDocumentAsync(config, httpClientFactory, context.HttpContext.RequestAborted);
        context.ProtocolMessage.IssuerAddress = oidcDiscovery.AuthorizationEndpoint;
        context.ProtocolMessage.ClientId = config.ClientId;

        // tenantId is already in Properties from the challenge setup.
        // Do NOT stash secrets in Properties — they get serialized into a browser cookie.

        await base.RedirectToIdentityProvider(context);
    }

    /// <summary>
    /// Handles the full authorization code exchange, id_token validation, and user provisioning.
    /// Validates the id_token using signing keys from the tenant's discovery document — not from the
    /// shared Options singleton — then calls <see cref="SocialAuthEvents.PopulateUserClaimsAsync"/>
    /// and short-circuits via context.Success() to avoid
    /// the middleware's built-in token validation (which would fail against the placeholder authority).
    /// </summary>
    /// <param name="context">The authorization code received context.</param>
    /// <returns>Returns an awaitable Task.</returns>
    public override async Task AuthorizationCodeReceived(AuthorizationCodeReceivedContext context)
    {
        if (context.Properties?.Items is null ||
            context.Properties.Items.TryGetValue("tenantId", out string? tenantIdStr) == false ||
            int.TryParse(tenantIdStr, out int tenantId) == false)
        {
            context.Fail("Missing tenant ID in authentication properties");

            return;
        }

        ILogger<SsoOidcEvents> logger = ResolveLogger(context.HttpContext);

        // Re-read OIDC config from DB instead of reading secrets from the cookie
        TenantOidcConfiguration? config = await ResolveTenantOidcConfigAsync(context.HttpContext, tenantId);
        if (config is null)
        {
            logger.LogWarning("Tenant OIDC configuration not found during code exchange for tenant {TenantId}", tenantId);
            context.Fail("Tenant OIDC configuration not found");

            return;
        }

        // Fetch the discovery document (uses SSRF-safe HttpClient)
        IHttpClientFactory httpClientFactory = context.HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>();
        OpenIdConnectConfiguration discovery = await FetchDiscoveryDocumentAsync(config, httpClientFactory, context.HttpContext.RequestAborted);

        // Validate the token endpoint URL before sending credentials to it
        string tokenEndpoint = discovery.TokenEndpoint;
        if (IsUrlSafe(tokenEndpoint) == false)
        {
            logger.LogWarning(
                "Token endpoint from discovery document is unsafe for tenant {TenantId}: {TokenEndpoint}",
                tenantId, tokenEndpoint);
            context.Fail("Token endpoint points to a disallowed destination");

            return;
        }

        // Decrypt the stored OIDC client secret
        IOidcSecretProtector secretProtector = context.HttpContext.RequestServices.GetRequiredService<IOidcSecretProtector>();
        string clientSecret = secretProtector.Unprotect(config.ClientSecret);

        // Manually exchange the authorization code for tokens (uses SSRF-safe HttpClient)
        using HttpClient httpClient = httpClientFactory.CreateClient("OidcTokenExchange");
        Dictionary<string, string> tokenRequestParams = new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = context.TokenEndpointRequest?.Code ?? context.ProtocolMessage.Code,
            ["redirect_uri"] = context.TokenEndpointRequest?.RedirectUri ?? context.Properties.Items[OpenIdConnectDefaults.RedirectUriForCodePropertiesKey] ?? string.Empty,
            ["client_id"] = config.ClientId,
            ["client_secret"] = clientSecret,
        };

        using FormUrlEncodedContent content = new(tokenRequestParams);
        using HttpResponseMessage tokenResponse = await httpClient.PostAsync(tokenEndpoint, content, context.HttpContext.RequestAborted);
        string responseBody = await tokenResponse.Content.ReadAsStringAsync(context.HttpContext.RequestAborted);

        if (tokenResponse.IsSuccessStatusCode == false)
        {
            logger.LogWarning(
                "Token exchange failed for tenant {TenantId}: HTTP {StatusCode}",
                tenantId, (int)tokenResponse.StatusCode);
            context.Fail($"Token exchange failed: {tokenResponse.StatusCode}");

            return;
        }

        using JsonDocument tokenJson = JsonDocument.Parse(responseBody);
        string? idToken = tokenJson.RootElement.TryGetProperty("id_token", out JsonElement idTokenElement) ? idTokenElement.GetString() : null;

        if (string.IsNullOrEmpty(idToken))
        {
            logger.LogWarning("No id_token received from token endpoint for tenant {TenantId}", tenantId);
            context.Fail("No id_token received from token endpoint");

            return;
        }

        // Validate the id_token using the tenant's signing keys from the discovery document.
        // This is done per-request with no shared state — avoids the middleware's built-in validation
        // which would fail against the placeholder authority configured in Program.cs.
        JsonWebTokenHandler tokenHandler = new();
        TokenValidationParameters validationParameters = new()
        {
            ValidIssuer = discovery.Issuer,
            IssuerSigningKeys = discovery.SigningKeys,
            ValidAudience = config.ClientId,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
        };

        TokenValidationResult validationResult = await tokenHandler.ValidateTokenAsync(idToken, validationParameters);
        if (validationResult.IsValid == false)
        {
            logger.LogWarning(
                "id_token signature validation failed for tenant {TenantId}: {Reason}",
                tenantId, validationResult.Exception?.Message ?? "unknown");
            context.Fail("id_token validation failed");

            return;
        }

        // Build the principal from the validated token and populate user claims
        ClaimsIdentity identity = new(validationResult.ClaimsIdentity.Claims, "tenant-oidc");
        bool populated = await SocialAuthEvents.PopulateUserClaimsAsync(identity, context.HttpContext, context.HttpContext.RequestAborted, AuthProviderType.CustomOidc);
        if (populated == false)
        {
            logger.LogWarning("User account is inactive or not authorized for tenant {TenantId}", tenantId);
            context.Fail("User account is inactive or not authorized");

            return;
        }

        // Short-circuit: set the validated principal and complete authentication.
        // This bypasses the middleware's built-in token validation entirely, avoiding the
        // placeholder authority issue and eliminating any need to mutate the shared Options singleton.
        context.Principal = new ClaimsPrincipal(identity);
        context.Success();
    }

    /// <summary>
    /// Fetches the OIDC discovery document using an SSRF-safe HTTP client.
    /// The <see cref="SsrfSafeSocketsHttpHandler"/> blocks connections to private/reserved IPs at the
    /// socket level, eliminating DNS rebinding TOCTOU vulnerabilities.
    /// </summary>
    /// <param name="config">The tenant OIDC configuration.</param>
    /// <param name="httpClientFactory">The HTTP client factory (must have "OidcDiscovery" client registered with SSRF-safe handler).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The OIDC discovery configuration.</returns>
    internal static async Task<OpenIdConnectConfiguration> FetchDiscoveryDocumentAsync(
        TenantOidcConfiguration config,
        IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        string metadataAddress = config.MetadataAddress
            ?? $"{config.Authority.TrimEnd('/')}/.well-known/openid-configuration";

        // Fast string-level pre-filter (blocks http://, literal private IPs, localhost)
        if (IsUrlSafe(metadataAddress) == false)
        {
            throw new InvalidOperationException("OIDC metadata address points to a disallowed destination");
        }

        // The SSRF-safe HttpClient handles DNS rebinding protection at the socket level
        HttpClient httpClient = httpClientFactory.CreateClient("OidcDiscovery");
        HttpDocumentRetriever documentRetriever = new(httpClient) { RequireHttps = true };

        ConfigurationManager<OpenIdConnectConfiguration> configManager = new(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            documentRetriever);

        return await configManager.GetConfigurationAsync(ct);
    }

    /// <summary>
    /// Validates that a URL is safe for OIDC discovery (HTTPS, no private IPs).
    /// Performs synchronous string-level checks only. Socket-level DNS rebinding protection
    /// is handled by <see cref="SsrfSafeSocketsHttpHandler"/> at connect time.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <returns>True if the URL passes string-level safety checks; otherwise, false.</returns>
    internal static bool IsUrlSafe(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) == false)
        {
            return false;
        }

        if (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) == false)
        {
            return false;
        }

        string host = uri.Host;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
            string.Equals(host, "0.0.0.0", StringComparison.Ordinal) ||
            string.Equals(host, "::1", StringComparison.Ordinal))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            if (SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(ip))
            {
                return false;
            }

            // IPv4-mapped IPv6 addresses (::ffff:x.x.x.x) — re-check the embedded IPv4 portion.
            if (ip.IsIPv4MappedToIPv6)
            {
                IPAddress mapped = ip.MapToIPv4();

                return IsUrlSafe($"https://{mapped}{uri.PathAndQuery}");
            }
        }

        return true;
    }

    /// <summary>
    /// Validates that a URL is safe for OIDC discovery, including DNS resolution to prevent DNS rebinding.
    /// Retained for use by <see cref="Services.Handlers.TenantOidcHandler"/> at config-save time.
    /// At runtime, the <see cref="SsrfSafeSocketsHttpHandler"/> provides the authoritative socket-level check.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <returns>True if the URL is safe to fetch; otherwise, false.</returns>
    internal static async Task<bool> IsUrlSafeAsync(string url)
    {
        if (IsUrlSafe(url) == false)
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) == false)
        {
            return false;
        }

        string host = uri.Host;

        // If the host is already an IP address, no DNS resolution needed (already checked above)
        if (IPAddress.TryParse(host, out _))
        {
            return true;
        }

        // Resolve DNS and validate all returned IP addresses
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host);
        }
        catch (Exception)
        {
            return false;
        }

        if (addresses.Length == 0)
        {
            return false;
        }

        foreach (IPAddress address in addresses)
        {
            if (SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(address))
            {
                return false;
            }

            // Check IPv4-mapped IPv6 addresses
            if (address.IsIPv4MappedToIPv6 && SsrfSafeSocketsHttpHandler.IsPrivateOrReservedIp(address.MapToIPv4()))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<TenantOidcConfiguration?> ResolveTenantOidcConfigAsync(HttpContext httpContext, int tenantId)
    {
        IDatabaseCache dbCache = httpContext.RequestServices.GetRequiredService<IDatabaseCache>();

        return await dbCache.GetTenantOidcConfigurationAsync(tenantId, httpContext.RequestAborted);
    }

    private static ILogger<SsoOidcEvents> ResolveLogger(HttpContext httpContext)
    {
        return httpContext.RequestServices.GetRequiredService<ILogger<SsoOidcEvents>>();
    }
}
