// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IWebhookRepository
{
    /// <inheritdoc/>
    public async Task<List<WebhookEndpoint>> GetWebhooksForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        List<WebhookEndpoint> webhooks = await _db.WebhookEndpoints
            .Where(w => w.TenantId == tenantId)
            .OrderBy(w => w.Name)
            .ToListAsync(cancellationToken);

        return webhooks;
    }

    /// <inheritdoc/>
    public async Task<WebhookEndpoint> CreateWebhookAsync(WebhookEndpoint webhook, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhook);

        webhook.Id = await _db.InsertWithInt32IdentityAsync(webhook, token: cancellationToken);

        _logger.LogDebug("Created webhook endpoint {WebhookId} for tenant {TenantId}", webhook.Id, webhook.TenantId);

        return webhook;
    }

    /// <inheritdoc/>
    public async Task<WebhookEndpoint?> GetWebhookByIdAsync(int webhookId, int tenantId, CancellationToken cancellationToken)
    {
        WebhookEndpoint? webhook = await _db.WebhookEndpoints
            .FirstOrDefaultAsync(w => (w.Id == webhookId) && (w.TenantId == tenantId), cancellationToken);

        return webhook;
    }

    /// <inheritdoc/>
    public async Task<int> DeleteWebhookAsync(int webhookId, CancellationToken cancellationToken)
    {
        int deleted = await _db.WebhookEndpoints
            .Where(w => w.Id == webhookId)
            .DeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted webhook endpoint {WebhookId}", webhookId);
        }

        return deleted;
    }

    /// <inheritdoc/>
    public async Task UpdateWebhookEnabledAsync(int webhookId, bool isEnabled, CancellationToken cancellationToken)
    {
        await _db.WebhookEndpoints
            .Where(w => w.Id == webhookId)
            .Set(w => w.IsEnabled, isEnabled)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateWebhookSecretAsync(int webhookId, string encryptedSecret, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedSecret);

        await _db.WebhookEndpoints
            .Where(w => w.Id == webhookId)
            .Set(w => w.Secret, encryptedSecret)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> CountWebhooksForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        int count = await _db.WebhookEndpoints
            .Where(w => w.TenantId == tenantId)
            .CountAsync(cancellationToken);

        return count;
    }

    /// <inheritdoc/>
    public async Task<int> DisableWebhooksForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        int updated = await _db.WebhookEndpoints
            .Where(w => (w.TenantId == tenantId) && (w.IsEnabled == true))
            .Set(w => w.IsEnabled, false)
            .UpdateAsync(cancellationToken);

        if (updated > 0)
        {
            _logger.LogInformation("Disabled {Count} webhook endpoints for tenant {TenantId}", updated, tenantId);
        }

        return updated;
    }

    /// <inheritdoc/>
    public async Task<List<WebhookEndpoint>> GetEnabledWebhooksForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        List<WebhookEndpoint> webhooks = await _db.WebhookEndpoints
            .Where(w => (w.TenantId == tenantId) && (w.IsEnabled == true))
            .ToListAsync(cancellationToken);

        return webhooks;
    }
}
