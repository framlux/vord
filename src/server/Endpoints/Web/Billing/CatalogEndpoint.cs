// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Deployment;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// A single public catalog price entry returned to the UI.
/// </summary>
public sealed class CatalogItemDto
{
    /// <summary>The subscription tier this price applies to (e.g. "Pro", "Team").</summary>
    public string Tier { get; set; } = string.Empty;

    /// <summary>The billing interval ("monthly" or "annual"), or null when unknown.</summary>
    public string? Interval { get; set; }

    /// <summary>Per-machine price in cents.</summary>
    public long UnitAmountCents { get; set; }

    /// <summary>Three-letter currency code.</summary>
    public string Currency { get; set; } = "usd";
}

/// <summary>
/// Returns the public pricing catalog. Deliberately available to Free-tier tenants, since the
/// catalog powers the upgrade pricing cards. It is absent in a self-hosted deployment, which sells
/// nothing and therefore has no prices to list.
/// </summary>
public sealed class CatalogEndpoint : EndpointWithoutRequest<ApiResponse<List<CatalogItemDto>>>
{
    private readonly IBillingApiClient _billingApiClient;
    private readonly ITenantContext _tenantContext;
    private readonly DeploymentMode _deploymentMode;

    /// <summary>
    /// Creates a new instance of the <see cref="CatalogEndpoint"/> class.
    /// </summary>
    public CatalogEndpoint(IBillingApiClient billingApiClient, ITenantContext tenantContext, DeploymentMode deploymentMode)
    {
        ArgumentNullException.ThrowIfNull(billingApiClient);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(deploymentMode);

        _billingApiClient = billingApiClient;
        _tenantContext = tenantContext;
        _deploymentMode = deploymentMode;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/billing/catalog");
        Policies(AuthorizationPolicies.ViewOnly);
        Tags(EndpointTags.RequiresTenant);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        if (_deploymentMode.IsSelfHosted)
        {
            await HttpContext.SendApiErrorAsync(404, "Billing is not enabled", ct);

            return;
        }

        _tenantContext.RequireTenantId();

        List<CatalogItemResult> items = await _billingApiClient.GetPublicCatalogAsync(ct);

        List<CatalogItemDto> dtos = items.Select(i => new CatalogItemDto
        {
            Tier = i.Tier.ToString(),
            Interval = BillingIntervalFormat.ToWireString(i.Interval),
            UnitAmountCents = i.UnitAmountCents,
            Currency = i.Currency,
        }).ToList();

        await Send.OkAsync(ApiResponse<List<CatalogItemDto>>.Ok(dtos), cancellation: ct);
    }
}
