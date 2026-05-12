// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// Invoice data returned to the UI.
/// </summary>
public sealed class InvoiceDto
{
    /// <summary>Stripe invoice identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Total amount in cents.</summary>
    public long AmountCents { get; set; }

    /// <summary>Three-letter currency code.</summary>
    public string Currency { get; set; } = "usd";

    /// <summary>Invoice status (paid, open, etc.).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>When the invoice was created.</summary>
    public DateTimeOffset Created { get; set; }

    /// <summary>Billing period start.</summary>
    public DateTimeOffset? PeriodStart { get; set; }

    /// <summary>Billing period end.</summary>
    public DateTimeOffset? PeriodEnd { get; set; }

    /// <summary>URL to the hosted invoice page.</summary>
    public string HostedInvoiceUrl { get; set; } = string.Empty;

    /// <summary>URL to the invoice PDF.</summary>
    public string InvoicePdfUrl { get; set; } = string.Empty;
}

/// <summary>
/// Returns recent paid invoices for the current tenant.
/// </summary>
public sealed class InvoicesEndpoint : EndpointWithoutRequest<ApiResponse<List<InvoiceDto>>>
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IBillingApiClient _billingApiClient;

    /// <summary>
    /// Creates a new instance of the <see cref="InvoicesEndpoint"/> class.
    /// </summary>
    public InvoicesEndpoint(ITenantRepository tenantRepository, IBillingApiClient billingApiClient)
    {
        _tenantRepository = tenantRepository;
        _billingApiClient = billingApiClient;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/billing/invoices");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<List<InvoiceDto>>.Error("Unauthorized"), ct);
            return;
        }

        Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(tenantId.Value, ct);
        if (tenant is null)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<List<InvoiceDto>>.Error("Tenant not found"), ct);

            return;
        }

        List<InvoiceResult> results = await _billingApiClient.ListInvoicesAsync(
            tenant.ExternalId, 12, ct);

        List<InvoiceDto> dtos = results.Select(inv => new InvoiceDto
        {
            Id = inv.Id,
            AmountCents = inv.AmountCents,
            Currency = inv.Currency,
            Status = inv.Status,
            Created = inv.Created,
            PeriodStart = inv.PeriodStart,
            PeriodEnd = inv.PeriodEnd,
            HostedInvoiceUrl = inv.HostedInvoiceUrl,
            InvoicePdfUrl = inv.InvoicePdfUrl,
        }).ToList();

        await Send.OkAsync(ApiResponse<List<InvoiceDto>>.Ok(dtos), cancellation: ct);
    }
}
