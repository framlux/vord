// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Options;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// Service for managing tenant subscriptions and billing.
/// </summary>
public sealed class SubscriptionService : ISubscriptionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SubscriptionOptions _subscriptionOptions;
    private readonly ILogger<SubscriptionService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="SubscriptionService"/> class.
    /// </summary>
    public SubscriptionService(IServiceScopeFactory scopeFactory, IOptions<SubscriptionOptions> subscriptionOptions, ILogger<SubscriptionService> logger)
    {
        _scopeFactory = scopeFactory;
        _subscriptionOptions = subscriptionOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<TenantSubscription?> GetSubscriptionForTenantAsync(int tenantId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        TenantSubscription? subscription = await db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);

        return subscription;
    }

    /// <inheritdoc/>
    public async Task<bool> CanApproveMachineAsync(int tenantId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        TenantSubscription? subscription = await db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);

        if (subscription is null)
        {
            return false;
        }

        if (subscription.MachineLimit is null)
        {
            return true; // Unlimited
        }

        int activeMachineCount = await db.Machines
            .Where(m => m.TenantId == tenantId && m.IsDeleted == false)
            .CountAsync(ct);

        return activeMachineCount < subscription.MachineLimit.Value;
    }

    /// <inheritdoc/>
    public async Task<TenantSubscription> ProvisionFreeSubscriptionAsync(int tenantId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        int machineLimit = _subscriptionOptions.FreeTierMachineLimit;
        int retentionDays = _subscriptionOptions.FreeTierRetentionDays;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            MachineLimit = machineLimit,
            RetentionDays = retentionDays,
            CreatedAt = now,
            UpdatedAt = now,
        };

        subscription.Id = await db.InsertWithInt32IdentityAsync(subscription, token: ct);
        _logger.LogInformation("Provisioned Free subscription for tenant {TenantId} (machines: {MachineLimit}, retention: {RetentionDays}d)", tenantId, machineLimit, retentionDays);

        return subscription;
    }

    /// <inheritdoc/>
    public async Task<int> GetRetentionDaysForTenantAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await GetSubscriptionForTenantAsync(tenantId, ct);

        return subscription?.RetentionDays ?? 1;
    }

    /// <inheritdoc/>
    public async Task<int> GetMachineCountForTenantAsync(int tenantId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        int count = await db.Machines
            .Where(m => m.TenantId == tenantId && m.IsDeleted == false)
            .CountAsync(ct);

        return count;
    }

    /// <inheritdoc/>
    public async Task EnsureSubscriptionExistsAsync(int tenantId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        TenantSubscription? subscription = await db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);

        if (subscription is not null && subscription.Status == SubscriptionStatus.Active)
        {
            return;
        }

        if (subscription is not null && subscription.Tier == SubscriptionTier.Free && subscription.Status != SubscriptionStatus.Active)
        {
            int machineLimit = _subscriptionOptions.FreeTierMachineLimit;
            int retentionDays = _subscriptionOptions.FreeTierRetentionDays;

            await db.TenantSubscriptions
                .Where(s => s.Id == subscription.Id)
                .Set(s => s.Status, SubscriptionStatus.Active)
                .Set(s => s.MachineLimit, machineLimit)
                .Set(s => s.RetentionDays, retentionDays)
                .Set(s => s.UpdatedAt, DateTimeOffset.UtcNow)
                .UpdateAsync(ct);

            _logger.LogInformation("Reactivated Free subscription for tenant {TenantId}", tenantId);

            return;
        }

        if (subscription is null)
        {
            await ProvisionFreeSubscriptionAsync(tenantId, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<int> GetMachineCountAtDateAsync(int tenantId, DateTimeOffset targetDate, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        int count = await db.Machines
            .Where(m => m.TenantId == tenantId
                && m.RegisteredOn <= targetDate
                && (m.IsDeleted == false || m.DeletedOn > targetDate))
            .CountAsync(ct);

        return count;
    }
}
