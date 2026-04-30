// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Alerts;
using Framlux.FleetManagement.Server.Services.Billing;
using LinqToDB;
using LinqToDB.Async;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Deletes a custom alert rule.
/// Only custom rules can be deleted. Default rules must be disabled instead.
/// </summary>
public sealed class AlertRuleDeleteEndpoint : EndpointWithoutRequest<ApiResponse<bool>>
{
    private readonly DatabaseContext _db;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IConnectionMultiplexer _redis;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertRuleDeleteEndpoint"/> class.
    /// </summary>
    public AlertRuleDeleteEndpoint(DatabaseContext db, ISubscriptionService subscriptionService, IConnectionMultiplexer redis)
    {
        _db = db;
        _subscriptionService = subscriptionService;
        _redis = redis;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Delete("/alert-rules/{id}");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<bool>.Error("Unauthorized"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free) || (subscription.Status != SubscriptionStatus.Active))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<bool>.Error("Alerting requires a Pro or Team subscription"), ct);

            return;
        }

        int ruleId = Route<int>("id");

        AlertRule? rule = await _db.AlertRules
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.TenantId == tenantId.Value, ct);

        if (rule is null)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        if (rule.IsCustom == false)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<bool>.Error("Default rules cannot be deleted. Disable them instead."), ct);

            return;
        }

        // Resolve all active events for this rule before deleting it.
        await _db.AlertEvents
            .Where(e => (e.AlertRuleId == ruleId) && (e.Status != AlertEventStatus.Resolved))
            .Set(e => e.Status, AlertEventStatus.Resolved)
            .Set(e => e.ResolvedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(ct);

        // Clean up Redis condition-tracking keys for this rule.
        List<long> machineIds = await _db.Machines
            .Where(m => (m.TenantId == tenantId.Value) && (m.IsDeleted == false))
            .Select(m => m.Id)
            .ToListAsync(ct);

        IDatabase redisDb = _redis.GetDatabase();
        foreach (long machineId in machineIds)
        {
            await redisDb.KeyDeleteAsync($"{AlertConstants.ConditionKeyPrefix}:{ruleId}:{machineId}");
        }

        await _db.AlertRules
            .Where(r => (r.Id == ruleId) && (r.TenantId == tenantId.Value))
            .DeleteAsync(ct);

        await Send.OkAsync(ApiResponse<bool>.Ok(true, "Alert rule deleted"), cancellation: ct);
    }
}
