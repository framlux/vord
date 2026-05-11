// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Services.Alerts;

/// <summary>
/// Delivers alert notifications via email and integration endpoints.
/// </summary>
public sealed class AlertDeliveryService : IAlertDeliveryService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionMultiplexer _redis;
    private readonly Dictionary<IntegrationProvider, IIntegrationPayloadFormatter> _formatters;
    private readonly ILogger<AlertDeliveryService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertDeliveryService"/> class.
    /// </summary>
    public AlertDeliveryService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IConnectionMultiplexer redis,
        IEnumerable<IIntegrationPayloadFormatter> formatters,
        ILogger<AlertDeliveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(formatters);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _redis = redis;
        _formatters = formatters.ToDictionary(f => f.Provider);
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task DeliverAsync(AlertEvent alertEvent, AlertRule rule, CancellationToken ct)
    {
        if (rule.NotifyWebhook)
        {
            await DeliverIntegrationsAsync(alertEvent, rule, ct);
        }

        if (rule.NotifyEmail)
        {
            _logger.LogInformation("Email alert delivery for event {EventId} (email delivery not yet implemented)", alertEvent.Id);
        }
    }

    private async Task DeliverIntegrationsAsync(AlertEvent alertEvent, AlertRule rule, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IIntegrationRepository integrationRepo = scope.ServiceProvider.GetRequiredService<IIntegrationRepository>();

        List<IntegrationEndpoint> integrations = await integrationRepo.GetEnabledIntegrationsForTenantAsync(rule.TenantId, ct);

        foreach (IntegrationEndpoint integration in integrations)
        {
            try
            {
                if (_formatters.TryGetValue(integration.Provider, out IIntegrationPayloadFormatter? formatter) == false)
                {
                    _logger.LogWarning("No formatter registered for provider {Provider} on integration {IntegrationId}",
                        integration.Provider, integration.Id);

                    continue;
                }

                HttpRequestMessage request = formatter.FormatRequest(alertEvent, rule, integration);
                HttpClient client = _httpClientFactory.CreateClient("IntegrationDelivery");
                HttpResponseMessage response = await client.SendAsync(request, ct);

                if (response.IsSuccessStatusCode == false)
                {
                    _logger.LogWarning("Integration {IntegrationId} ({Provider}) delivery failed with status {StatusCode}",
                        integration.Id, integration.Provider, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deliver integration {IntegrationId} ({Provider}) for alert event {EventId}",
                    integration.Id, integration.Provider, alertEvent.Id);
            }
        }
    }

    /// <inheritdoc/>
    public async Task EnqueueAsync(long eventId, int ruleId, int tenantId, CancellationToken ct)
    {
        string payload = JsonSerializer.Serialize(new { eventId, ruleId, tenantId }, JsonDefaults.CamelCase);
        IDatabase redisDb = _redis.GetDatabase();
        await redisDb.ListLeftPushAsync(AlertConstants.DeliveryQueueKey, payload);
        _logger.LogDebug("Enqueued delivery job for event {EventId}, rule {RuleId}", eventId, ruleId);
    }
}
