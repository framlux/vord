// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Hangfire;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// Delivers alert notifications via email and integration endpoints. Integration delivery is
/// idempotent across Hangfire retries: each successful (event, integration) tuple is recorded so
/// re-runs skip already-delivered integrations. Transient failures (5xx, transport errors) throw
/// so Hangfire's retry kicks in; permanent failures (4xx) log without throwing.
/// </summary>
public sealed class AlertDeliveryService : IAlertDeliveryService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly Dictionary<IntegrationProvider, IIntegrationPayloadFormatter> _formatters;
    private readonly ILogger<AlertDeliveryService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertDeliveryService"/> class.
    /// </summary>
    /// <param name="scopeFactory">Service scope factory for resolving scoped repositories.</param>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="backgroundJobClient">Hangfire background job client for enqueue.</param>
    /// <param name="formatters">Payload formatters for each integration provider.</param>
    /// <param name="logger">The logger.</param>
    public AlertDeliveryService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IBackgroundJobClient backgroundJobClient,
        IEnumerable<IIntegrationPayloadFormatter> formatters,
        ILogger<AlertDeliveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(backgroundJobClient);
        ArgumentNullException.ThrowIfNull(formatters);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _backgroundJobClient = backgroundJobClient;
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
        IIntegrationDeliveryAttemptRepository attemptRepo = scope.ServiceProvider.GetRequiredService<IIntegrationDeliveryAttemptRepository>();

        List<IntegrationEndpoint> integrations = await integrationRepo.GetEnabledIntegrationsForTenantAsync(rule.TenantId, ct);
        HashSet<int> alreadyClaimed = await attemptRepo.GetClaimedIntegrationIdsAsync(alertEvent.Id, ct);

        List<string> transientFailures = [];

        foreach (IntegrationEndpoint integration in integrations)
        {
            if (alreadyClaimed.Contains(integration.Id))
            {
                _logger.LogDebug("Integration {IntegrationId} already attempted for event {EventId}; skipping",
                    integration.Id, alertEvent.Id);

                continue;
            }

            if (_formatters.TryGetValue(integration.Provider, out IIntegrationPayloadFormatter? formatter) == false)
            {
                _logger.LogWarning("No formatter registered for provider {Provider} on integration {IntegrationId}",
                    integration.Provider, integration.Id);

                continue;
            }

            // Claim BEFORE sending. A crash or Hangfire retry between the HTTP POST and the
            // success record would otherwise re-POST to the receiver. The claim is recorded
            // first; transient failures explicitly release it, permanent failures do not.
            bool claimed = await attemptRepo.TryClaimAttemptAsync(
                alertEvent.Id, integration.Id, DateTimeOffset.UtcNow, ct);
            if (claimed == false)
            {
                _logger.LogDebug("Integration {IntegrationId} attempt already claimed for event {EventId}; skipping",
                    integration.Id, alertEvent.Id);

                continue;
            }

            bool releaseForRetry = false;
            try
            {
                HttpRequestMessage request = formatter.FormatRequest(alertEvent, rule, integration);
                HttpClient client = _httpClientFactory.CreateClient("IntegrationDelivery");
                HttpResponseMessage response = await client.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    await attemptRepo.MarkAttemptSucceededAsync(alertEvent.Id, integration.Id, DateTimeOffset.UtcNow, ct);
                }
                else if ((int)response.StatusCode >= 500)
                {
                    // Transient — release the claim so a Hangfire retry can re-attempt.
                    releaseForRetry = true;
                    _logger.LogWarning("Integration {IntegrationId} ({Provider}) delivery failed with 5xx {StatusCode}; will retry",
                        integration.Id, integration.Provider, response.StatusCode);
                    transientFailures.Add($"integration {integration.Id} returned {(int)response.StatusCode}");
                }
                else
                {
                    // 4xx — permanent, do not retry. Leave the Pending claim in place so future
                    // retries skip this integration; the receiver rejected our request format/auth.
                    _logger.LogError("Integration {IntegrationId} ({Provider}) delivery permanently failed with {StatusCode}; suppressing retries",
                        integration.Id, integration.Provider, response.StatusCode);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                // Transport-level failure — same retry semantics as 5xx; release the claim.
                releaseForRetry = true;
                _logger.LogWarning(ex, "Integration {IntegrationId} ({Provider}) transport failure for event {EventId}; will retry",
                    integration.Id, integration.Provider, alertEvent.Id);
                transientFailures.Add($"integration {integration.Id} transport error: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Unexpected — likely a programming error in a formatter. Log + leave the claim
                // in place (retries will not help). Other integrations are still attempted on
                // this pass.
                _logger.LogError(ex, "Failed to deliver integration {IntegrationId} ({Provider}) for alert event {EventId}; suppressing retries",
                    integration.Id, integration.Provider, alertEvent.Id);
            }
            finally
            {
                if (releaseForRetry)
                {
                    // Use CancellationToken.None — even if the worker is shutting down, we still
                    // want the claim released so the Hangfire retry can proceed.
                    await attemptRepo.ReleaseClaimForRetryAsync(alertEvent.Id, integration.Id, CancellationToken.None);
                }
            }
        }

        if (transientFailures.Count > 0)
        {
            throw new IntegrationDeliveryException(
                $"Transient failures during alert delivery for event {alertEvent.Id}: {string.Join("; ", transientFailures)}");
        }
    }

    /// <inheritdoc/>
    public Task EnqueueAsync(long eventId, int ruleId, int tenantId, CancellationToken ct)
    {
        // Hangfire stores the lambda expression for later execution by a worker; the caller's
        // CancellationToken is not meaningful at deserialization time. Hangfire passes its own
        // shutdown token when the worker invokes the job.
        _backgroundJobClient.Enqueue<IntegrationDeliveryJob>(j => j.DeliverAsync(eventId, ruleId, tenantId, CancellationToken.None));
        _logger.LogDebug("Enqueued delivery job for event {EventId}, rule {RuleId}", eventId, ruleId);

        return Task.CompletedTask;
    }
}
