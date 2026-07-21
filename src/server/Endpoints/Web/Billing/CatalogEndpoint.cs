// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;

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

    /// <summary>Whether the price is metered (billed on reported machine usage).</summary>
    public bool IsMetered { get; set; }
}

/// <summary>
/// Returns the public pricing catalog. Deliberately available to Free-tier tenants —
/// the catalog powers the upgrade pricing cards — and deliberately not gated on the
/// billing-enabled flag: disabled installs simply receive an empty catalog.
/// </summary>
public sealed class CatalogEndpoint : EndpointWithoutRequest<ApiResponse<List<CatalogItemDto>>>
{
    private readonly IBillingApiClient _billingApiClient;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="CatalogEndpoint"/> class.
    /// </summary>
    public CatalogEndpoint(IBillingApiClient billingApiClient, ITenantContext tenantContext)
    {
        _billingApiClient = billingApiClient;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/billing/catalog");
        Policies("ViewOnly");
        Tags(EndpointTags.RequiresTenant);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        _tenantContext.RequireTenantId();

        List<CatalogItemResult> items = await _billingApiClient.GetPublicCatalogAsync(ct);

        List<CatalogItemDto> dtos = items.Select(i => new CatalogItemDto
        {
            Tier = i.Tier.ToString(),
            Interval = BillingIntervalFormat.ToWireString(i.Interval),
            UnitAmountCents = i.UnitAmountCents,
            Currency = i.Currency,
            IsMetered = i.IsMetered,
        }).ToList();

        await Send.OkAsync(ApiResponse<List<CatalogItemDto>>.Ok(dtos), cancellation: ct);
    }
}
