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
/// Upcoming invoice data returned to the UI.
/// </summary>
public sealed class UpcomingInvoiceDto
{
    /// <summary>Whether an upcoming invoice exists.</summary>
    public bool HasInvoice { get; set; }

    /// <summary>Total amount due in cents.</summary>
    public long AmountDueCents { get; set; }

    /// <summary>Three-letter currency code.</summary>
    public string Currency { get; set; } = "usd";

    /// <summary>Billing period start.</summary>
    public DateTimeOffset? PeriodStart { get; set; }

    /// <summary>Billing period end.</summary>
    public DateTimeOffset? PeriodEnd { get; set; }

    /// <summary>When the next payment attempt will occur.</summary>
    public DateTimeOffset? NextPaymentAttempt { get; set; }

    /// <summary>Per-unit amount in cents.</summary>
    public long UnitAmountCents { get; set; }

    /// <summary>Total discount amount in cents (from coupons or credits).</summary>
    public long DiscountAmountCents { get; set; }

    /// <summary>Individual line items on the invoice.</summary>
    public List<LineItemDto> Lines { get; set; } = [];
}

/// <summary>
/// A single line item on an upcoming invoice.
/// </summary>
public sealed class LineItemDto
{
    /// <summary>Line item description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Amount in cents.</summary>
    public long AmountCents { get; set; }

    /// <summary>Number of units.</summary>
    public long Quantity { get; set; }

    /// <summary>Line item period start.</summary>
    public DateTimeOffset? PeriodStart { get; set; }

    /// <summary>Line item period end.</summary>
    public DateTimeOffset? PeriodEnd { get; set; }

    /// <summary>Whether this line item is a proration.</summary>
    public bool Proration { get; set; }
}

/// <summary>
/// Returns the upcoming invoice for the current tenant's subscription,
/// including prorated charges for mid-cycle machine additions/removals.
/// </summary>
public sealed class UpcomingInvoiceEndpoint : EndpointWithoutRequest<ApiResponse<UpcomingInvoiceDto>>
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IBillingApiClient _billingApiClient;

    /// <summary>
    /// Creates a new instance of the <see cref="UpcomingInvoiceEndpoint"/> class.
    /// </summary>
    public UpcomingInvoiceEndpoint(ITenantRepository tenantRepository, IBillingApiClient billingApiClient)
    {
        _tenantRepository = tenantRepository;
        _billingApiClient = billingApiClient;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/billing/upcoming-invoice");
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
                ApiResponse<UpcomingInvoiceDto>.Error("Unauthorized"), ct);
            return;
        }

        Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(tenantId.Value, ct);
        if (tenant is null)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<UpcomingInvoiceDto>.Error("Tenant not found"), ct);

            return;
        }

        UpcomingInvoiceResult? result = await _billingApiClient.GetUpcomingInvoiceAsync(
            tenant.ExternalId, ct);

        if (result is null)
        {
            await Send.OkAsync(ApiResponse<UpcomingInvoiceDto>.Ok(new UpcomingInvoiceDto { HasInvoice = false }),
                cancellation: ct);
            return;
        }

        UpcomingInvoiceDto dto = new()
        {
            HasInvoice = true,
            AmountDueCents = result.AmountDueCents,
            Currency = result.Currency,
            PeriodStart = result.PeriodStart,
            PeriodEnd = result.PeriodEnd,
            NextPaymentAttempt = result.NextPaymentAttempt,
            UnitAmountCents = result.UnitAmountCents,
            DiscountAmountCents = result.DiscountAmountCents,
            Lines = result.Lines.Select(l => new LineItemDto
            {
                Description = l.Description,
                AmountCents = l.AmountCents,
                Quantity = l.Quantity,
                PeriodStart = l.PeriodStart,
                PeriodEnd = l.PeriodEnd,
                Proration = l.Proration,
            }).ToList(),
        };

        await Send.OkAsync(ApiResponse<UpcomingInvoiceDto>.Ok(dto), cancellation: ct);
    }
}
