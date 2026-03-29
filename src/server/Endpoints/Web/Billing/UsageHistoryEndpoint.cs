// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;
using LinqToDB;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// A single month's usage data point.
/// </summary>
public sealed class UsagePointDto
{
    /// <summary>Month in yyyy-MM format.</summary>
    public string Month { get; set; } = string.Empty;

    /// <summary>Number of active machines in this month.</summary>
    public int MachineCount { get; set; }

    /// <summary>Invoice amount in cents for this month.</summary>
    public int InvoiceAmountCents { get; set; }
}

/// <summary>
/// Returns historical usage and cost data for the billing trend chart.
/// Machine counts are reconstructed from RegisteredOn/DeletedOn timestamps.
/// Costs come from actual Stripe invoice amounts.
/// </summary>
public sealed class UsageHistoryEndpoint : EndpointWithoutRequest<ApiResponse<List<UsagePointDto>>>
{
    private readonly IDatabaseCache _databaseCache;
    private readonly IBillingApiClient _billingApiClient;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="UsageHistoryEndpoint"/> class.
    /// </summary>
    public UsageHistoryEndpoint(
        IDatabaseCache databaseCache,
        IBillingApiClient billingApiClient,
        ISubscriptionService subscriptionService)
    {
        _databaseCache = databaseCache;
        _billingApiClient = billingApiClient;
        _subscriptionService = subscriptionService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/billing/usage-history");
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
                ApiResponse<List<UsagePointDto>>.Error("Unauthorized"), ct);
            return;
        }

        Tenant? tenant = await _databaseCache.GetTenantByIdAsync(tenantId.Value, ct);
        if (tenant is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        int months = 6;
        if (Query<int?>("months", isRequired: false) is int m && (m is > 0 and <= 12))
        {
            months = m;
        }

        // Get invoice history from Stripe for actual costs
        List<InvoiceResult> invoices = await _billingApiClient.ListInvoicesAsync(
            tenant.ExternalId, months, ct);

        // Build a map of month -> invoice amount
        Dictionary<string, int> invoiceByMonth = [];
        foreach (InvoiceResult inv in invoices)
        {
            if (inv.PeriodStart.HasValue)
            {
                string key = inv.PeriodStart.Value.ToString("yyyy-MM");
                invoiceByMonth.TryAdd(key, inv.AmountCents);
            }
        }

        // Reconstruct fleet size at the end of each month
        List<UsagePointDto> points = [];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = months - 1; i >= 0; i--)
        {
            DateTimeOffset monthEnd = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)
                .AddMonths(-i);
            // Last day of the month
            monthEnd = monthEnd.AddMonths(1).AddSeconds(-1);

            int machineCount = await _subscriptionService.GetMachineCountAtDateAsync(
                tenantId.Value, monthEnd, ct);

            string monthKey = monthEnd.ToString("yyyy-MM");
            invoiceByMonth.TryGetValue(monthKey, out int invoiceAmount);

            points.Add(new UsagePointDto
            {
                Month = monthKey,
                MachineCount = machineCount,
                InvoiceAmountCents = invoiceAmount,
            });
        }

        await Send.OkAsync(ApiResponse<List<UsagePointDto>>.Ok(points), cancellation: ct);
    }
}
