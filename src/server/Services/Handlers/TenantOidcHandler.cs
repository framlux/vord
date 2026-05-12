// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Tenants;
using Framlux.FleetManagement.Services.Core.Security;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles tenant OIDC configuration operations.
/// </summary>
public sealed class TenantOidcHandler : ITenantOidcHandler
{
    private readonly ITenantRepository _tenantRepo;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IOidcSecretProtector _secretProtector;

    /// <summary>
    /// Creates a new instance of the <see cref="TenantOidcHandler"/> class.
    /// </summary>
    public TenantOidcHandler(ITenantRepository tenantRepo, ISubscriptionService subscriptionService, IOidcSecretProtector secretProtector)
    {
        ArgumentNullException.ThrowIfNull(tenantRepo);
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(secretProtector);

        _tenantRepo = tenantRepo;
        _subscriptionService = subscriptionService;
        _secretProtector = secretProtector;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<TenantOidcConfigDto>> GetConfigAsync(int tenantId, int? claimTenantId, CancellationToken ct)
    {
        if ((claimTenantId is null) || (claimTenantId.Value != tenantId))
        {
            return ServiceResult<TenantOidcConfigDto>.NotFound();
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
        if ((subscription is null) || (subscription.Tier != SubscriptionTier.Team))
        {
            return ServiceResult<TenantOidcConfigDto>.Forbidden("Custom OIDC configuration requires a Team tier subscription");
        }

        TenantOidcConfiguration? config = await _tenantRepo.GetTenantOidcConfigByTenantIdAsync(tenantId, ct);

        if (config is null)
        {
            return ServiceResult<TenantOidcConfigDto>.Ok(new TenantOidcConfigDto());
        }

        TenantOidcConfigDto dto = new()
        {
            Authority = config.Authority,
            ClientId = config.ClientId,
            ClientSecret = "********",
            MetadataAddress = config.MetadataAddress,
            EmailDomain = config.EmailDomain,
            IsEnabled = config.IsEnabled,
        };

        return ServiceResult<TenantOidcConfigDto>.Ok(dto);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<TenantOidcConfigDto>> UpdateConfigAsync(int tenantId, int? claimTenantId, TenantOidcConfigDto request, CancellationToken ct)
    {
        if ((claimTenantId is null) || (claimTenantId.Value != tenantId))
        {
            return ServiceResult<TenantOidcConfigDto>.NotFound();
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
        if ((subscription is null) || (subscription.Tier != SubscriptionTier.Team))
        {
            return ServiceResult<TenantOidcConfigDto>.Forbidden("Custom OIDC configuration requires a Team tier subscription");
        }

        // Validate Authority URL to prevent SSRF
        if (string.IsNullOrWhiteSpace(request.Authority) || await SsoOidcEvents.IsUrlSafeAsync(request.Authority) == false)
        {
            return ServiceResult<TenantOidcConfigDto>.BadRequest("Authority URL is missing or not a valid, safe URL");
        }

        if (string.IsNullOrEmpty(request.MetadataAddress) == false && await SsoOidcEvents.IsUrlSafeAsync(request.MetadataAddress) == false)
        {
            return ServiceResult<TenantOidcConfigDto>.BadRequest("Metadata address is not a valid, safe URL");
        }

        string normalizedDomain = (request.EmailDomain ?? string.Empty).Trim().ToLowerInvariant().TrimStart('@');

        TenantOidcConfiguration? existing = await _tenantRepo.GetTenantOidcConfigByTenantIdAsync(tenantId, ct);

        if (existing is null)
        {
            string encryptedSecret = _secretProtector.Protect(request.ClientSecret);
            TenantOidcConfiguration config = new()
            {
                TenantId = tenantId,
                Authority = request.Authority,
                ClientId = request.ClientId,
                ClientSecret = encryptedSecret,
                MetadataAddress = request.MetadataAddress,
                EmailDomain = normalizedDomain,
                IsEnabled = request.IsEnabled,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _tenantRepo.InsertTenantOidcConfigAsync(config, ct);
        }
        else
        {
            string encryptedSecret = request.ClientSecret != "********"
                ? _secretProtector.Protect(request.ClientSecret)
                : existing.ClientSecret;

            await _tenantRepo.UpdateTenantOidcConfigAsync(
                tenantId,
                request.Authority,
                request.ClientId,
                encryptedSecret,
                request.MetadataAddress,
                normalizedDomain,
                request.IsEnabled,
                ct);
        }

        TenantOidcConfigDto response = new()
        {
            Authority = request.Authority,
            ClientId = request.ClientId,
            ClientSecret = "********",
            MetadataAddress = request.MetadataAddress,
            EmailDomain = normalizedDomain,
            IsEnabled = request.IsEnabled,
        };

        return ServiceResult<TenantOidcConfigDto>.Ok(response);
    }
}
