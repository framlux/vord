// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;
using Microsoft.AspNetCore.Authentication;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Auth;

/// <summary>
/// Initiates an OAuth/OIDC challenge for the specified provider.
/// </summary>
public sealed class AuthProviderChallengeEndpoint : EndpointWithoutRequest<ApiResponse<object>>
{
    private static readonly HashSet<string> ValidProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "github",
        "google",
        "microsoft",
        "tenant-oidc"
    };

    private readonly ITenantRepository _tenantRepository;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="AuthProviderChallengeEndpoint"/> class.
    /// </summary>
    public AuthProviderChallengeEndpoint(
        ITenantRepository tenantRepository,
        ISubscriptionService subscriptionService)
    {
        _tenantRepository = tenantRepository;
        _subscriptionService = subscriptionService;
    }

    /// <inheritdoc />
    public override void Configure()
    {
        Get("/auth/challenge/{provider}");
        AllowAnonymous();
        Version(1);
        Options(x => x.RequireRateLimiting("login"));
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        string provider = Route<string>("provider") ?? string.Empty;
        string returnUrl = Query<string?>("returnUrl", isRequired: false) ?? "/dashboard";

        if (ValidProviders.Contains(provider) == false)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<object>.Error("Invalid authentication provider"), ct);

            return;
        }

        // Prevent open redirect: only allow relative paths.
        if (Uri.IsWellFormedUriString(returnUrl, UriKind.Relative) == false ||
            returnUrl.StartsWith("//", StringComparison.Ordinal) ||
            returnUrl.StartsWith("\\", StringComparison.Ordinal) ||
            returnUrl.StartsWith("/", StringComparison.Ordinal) == false)
        {
            returnUrl = "/dashboard";
        }

        HttpContext.MarkResponseStart();

        AuthenticationProperties properties = new() { RedirectUri = returnUrl, AllowRefresh = true };

        // For tenant-oidc, pass the tenantId so SsoOidcEvents can load dynamic config
        if (string.Equals(provider, "tenant-oidc", StringComparison.OrdinalIgnoreCase))
        {
            string? tenantIdStr = Query<string?>("tenantId", isRequired: false);
            if (string.IsNullOrEmpty(tenantIdStr))
            {
                HttpContext.Response.StatusCode = 400;
                await HttpContext.Response.WriteAsJsonAsync(ApiResponse<object>.Error("tenantId is required for tenant-oidc provider"), ct);

                return;
            }

            if (int.TryParse(tenantIdStr, out int tenantId) == false)
            {
                HttpContext.Response.StatusCode = 400;
                await HttpContext.Response.WriteAsJsonAsync(ApiResponse<object>.Error("tenantId is invalid"), ct);

                return;
            }

            TenantOidcConfiguration? oidcConfig = await _tenantRepository.GetTenantOidcConfigurationAsync(tenantId, ct);
            TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
            bool teamTier = (subscription is not null) && (subscription.Tier == SubscriptionTier.Team);
            if ((SsoOidcEvents.IsConfigUsable(oidcConfig) == false) || (teamTier == false))
            {
                HttpContext.Response.StatusCode = 400;
                await HttpContext.Response.WriteAsJsonAsync(ApiResponse<object>.Error("Custom SSO is not available for this organization"), ct);

                return;
            }

            properties.Items["tenantId"] = tenantIdStr;
        }

        await HttpContext.ChallengeAsync(provider, properties);
    }
}
