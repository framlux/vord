// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Security;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Services.Alerts;

/// <summary>
/// Delivers alert notifications via email and webhook.
/// </summary>
public sealed class AlertDeliveryService : IAlertDeliveryService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnectionMultiplexer _redis;
    private readonly IWebhookSecretProtector _secretProtector;
    private readonly ILogger<AlertDeliveryService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertDeliveryService"/> class.
    /// </summary>
    public AlertDeliveryService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IConnectionMultiplexer redis,
        IWebhookSecretProtector secretProtector,
        ILogger<AlertDeliveryService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _redis = redis;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task DeliverAsync(AlertEvent alertEvent, AlertRule rule, CancellationToken ct)
    {
        if (rule.NotifyWebhook)
        {
            await DeliverWebhooksAsync(alertEvent, rule, ct);
        }

        if (rule.NotifyEmail)
        {
            _logger.LogInformation("Email alert delivery for event {EventId} (email delivery not yet implemented)", alertEvent.Id);
        }
    }

    private async Task DeliverWebhooksAsync(AlertEvent alertEvent, AlertRule rule, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IWebhookRepository webhookRepo = scope.ServiceProvider.GetRequiredService<IWebhookRepository>();

        List<WebhookEndpoint> webhooks = await webhookRepo.GetEnabledWebhooksForTenantAsync(rule.TenantId, ct);

        foreach (WebhookEndpoint webhook in webhooks)
        {
            try
            {
                string payload = JsonSerializer.Serialize(new
                {
                    eventId = alertEvent.Id,
                    ruleName = rule.Name,
                    severity = alertEvent.Severity.ToString(),
                    message = alertEvent.Message,
                    machineId = alertEvent.MachineId,
                    triggeredAt = alertEvent.TriggeredAt,
                    details = alertEvent.Details,
                }, JsonDefaults.CamelCase);

                string secret = _secretProtector.Unprotect(webhook.Secret);
                byte[] signatureBytes = HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(secret),
                    Encoding.UTF8.GetBytes(payload));
                string signature = $"sha256={Convert.ToHexStringLower(signatureBytes)}";

                HttpClient client = _httpClientFactory.CreateClient("WebhookDelivery");
                HttpRequestMessage request = new(HttpMethod.Post, webhook.Url);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                request.Headers.Add("X-Vord-Signature", signature);

                HttpResponseMessage response = await client.SendAsync(request, ct);

                if (response.IsSuccessStatusCode == false)
                {
                    _logger.LogWarning("Webhook {WebhookId} delivery failed with status {StatusCode}", webhook.Id, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deliver webhook {WebhookId} for alert event {EventId}", webhook.Id, alertEvent.Id);
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
