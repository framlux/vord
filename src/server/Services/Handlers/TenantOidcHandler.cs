// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Web.Tenants;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Security;
using LinqToDB.Async;
using LinqToDB;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles tenant OIDC configuration operations.
/// </summary>
public sealed class TenantOidcHandler : ITenantOidcHandler
{
    private readonly DatabaseContext _db;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IOidcSecretProtector _secretProtector;

    /// <summary>
    /// Creates a new instance of the <see cref="TenantOidcHandler"/> class.
    /// </summary>
    public TenantOidcHandler(DatabaseContext db, ISubscriptionService subscriptionService, IOidcSecretProtector secretProtector)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(secretProtector);

        _db = db;
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

        TenantOidcConfiguration? config = await _db.TenantOidcConfigurations
            .Where(c => c.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);

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

        DateTimeOffset now = DateTimeOffset.UtcNow;
        TenantOidcConfiguration? existing = await _db.TenantOidcConfigurations
            .Where(c => c.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);

        if (existing is null)
        {
            TenantOidcConfiguration config = new()
            {
                TenantId = tenantId,
                Authority = request.Authority,
                ClientId = request.ClientId,
                ClientSecret = _secretProtector.Protect(request.ClientSecret),
                MetadataAddress = request.MetadataAddress,
                EmailDomain = normalizedDomain,
                IsEnabled = request.IsEnabled,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await _db.InsertAsync(config, token: ct);
        }
        else
        {
            string encryptedSecret = request.ClientSecret != "********"
                ? _secretProtector.Protect(request.ClientSecret)
                : existing.ClientSecret;

            await _db.TenantOidcConfigurations
                .Where(c => c.TenantId == tenantId)
                .Set(c => c.Authority, request.Authority)
                .Set(c => c.ClientId, request.ClientId)
                .Set(c => c.ClientSecret, encryptedSecret)
                .Set(c => c.MetadataAddress, request.MetadataAddress)
                .Set(c => c.EmailDomain, normalizedDomain)
                .Set(c => c.IsEnabled, request.IsEnabled)
                .Set(c => c.UpdatedAt, now)
                .UpdateAsync(ct);
        }

        return ServiceResult<TenantOidcConfigDto>.Ok(request);
    }
}
