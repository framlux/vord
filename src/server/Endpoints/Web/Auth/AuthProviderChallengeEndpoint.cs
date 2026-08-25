// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Globalization;
using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;

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
    private readonly IDataProtectionProvider _dataProtectionProvider;

    /// <summary>
    /// Creates a new instance of the <see cref="AuthProviderChallengeEndpoint"/> class.
    /// </summary>
    public AuthProviderChallengeEndpoint(
        ITenantRepository tenantRepository,
        ISubscriptionService subscriptionService,
        IDataProtectionProvider dataProtectionProvider)
    {
        _tenantRepository = tenantRepository;
        _subscriptionService = subscriptionService;
        _dataProtectionProvider = dataProtectionProvider;
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
            await HttpContext.SendApiErrorAsync(400, "Invalid authentication provider", ct);

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

        // For tenant-oidc the browser passes only an opaque slug. Resolve it server-side to the
        // tenant id so SsoOidcEvents can load dynamic config; the raw id is never exposed to the
        // client. An unresolvable slug is treated the same as an unavailable tenant.
        if (string.Equals(provider, "tenant-oidc", StringComparison.OrdinalIgnoreCase))
        {
            string? slug = Query<string?>("slug", isRequired: false);
            IDataProtector protector = _dataProtectionProvider.CreateProtector(TenantSsoSlug.Purpose);
            if (TenantSsoSlug.TryResolve(protector, slug, out int tenantId) == false)
            {
                await HttpContext.SendApiErrorAsync(400, "Custom SSO is not available for this organization", ct);

                return;
            }

            TenantOidcConfiguration? oidcConfig = await _tenantRepository.GetTenantOidcConfigurationAsync(tenantId, ct);
            TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
            // Asks the positive question, so the block-polarity predicate is inverted here rather than
            // written out again. Using it bare would silently reverse the gate.
            bool teamTier = SubscriptionPolicy.RequiresTeam(subscription) == false;
            if ((SsoOidcEvents.IsConfigUsable(oidcConfig) == false) || (teamTier == false))
            {
                await HttpContext.SendApiErrorAsync(400, "Custom SSO is not available for this organization", ct);

                return;
            }

            properties.Items["tenantId"] = tenantId.ToString(CultureInfo.InvariantCulture);
        }

        await HttpContext.ChallengeAsync(provider, properties);
    }
}
