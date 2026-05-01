// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for webhook endpoint operations.
/// </summary>
public interface IWebhookRepository
{
    /// <summary>
    /// Returns all webhook endpoints for a tenant, ordered by name.
    /// </summary>
    Task<List<WebhookEndpoint>> GetWebhooksForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new webhook endpoint and sets its generated ID.
    /// </summary>
    Task<WebhookEndpoint> CreateWebhookAsync(WebhookEndpoint webhook, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a webhook endpoint by ID scoped to a tenant, or null if not found.
    /// </summary>
    Task<WebhookEndpoint?> GetWebhookByIdAsync(int webhookId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a webhook endpoint by ID. Returns the number of rows deleted.
    /// </summary>
    Task<int> DeleteWebhookAsync(int webhookId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the enabled flag on a webhook endpoint.
    /// </summary>
    Task UpdateWebhookEnabledAsync(int webhookId, bool isEnabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the encrypted secret on a webhook endpoint.
    /// </summary>
    Task UpdateWebhookSecretAsync(int webhookId, string encryptedSecret, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of webhook endpoints for a tenant.
    /// </summary>
    Task<int> CountWebhooksForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables all webhook endpoints for a tenant. Returns the number of rows updated.
    /// </summary>
    Task<int> DisableWebhooksForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all enabled webhook endpoints for a tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<WebhookEndpoint>> GetEnabledWebhooksForTenantAsync(int tenantId, CancellationToken cancellationToken = default);
}
