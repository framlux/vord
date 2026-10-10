// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Asks the pause service on behalf of the surfaces that only show a machine's paused state.
/// </summary>
/// <remarks>
/// A page that cannot learn which hosts are paused still has to render, so an unavailable decision
/// shows every host as active and is counted against the display surface. That is the same fail-open
/// choice ingest makes, and the counter is what tells an operator the display is running unmetered.
/// Only <see cref="QuotaCheckUnavailableException"/> is absorbed: a cancelled request still cancels.
/// The cause is logged as well as counted, as on the ingest, ping and command paths, because the
/// counter says that checks are failing but not why.
/// </remarks>
public static class QuotaCutoffDisplay
{
    /// <summary>
    /// Gets the tenant's cutoff, or <see cref="QuotaCutoff.None"/> when the decision cannot be made.
    /// </summary>
    /// <param name="quotaPause">The pause service.</param>
    /// <param name="metrics">Where an unavailable decision is counted.</param>
    /// <param name="logger">Where the cause of an unavailable decision is logged.</param>
    /// <param name="tenantId">The tenant to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tenant's cutoff, or nothing paused when it could not be determined.</returns>
    public static async Task<QuotaCutoff> GetOrNoneAsync(
        IQuotaPauseService quotaPause,
        QuotaPauseMetrics metrics,
        ILogger logger,
        int tenantId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(quotaPause);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return await quotaPause.GetCutoffAsync(tenantId, ct);
        }
        catch (QuotaCheckUnavailableException ex)
        {
            metrics.RecordCheckFailure(QuotaCheckSurface.Display);
            logger.LogWarning(ex, "Quota pause check failed for tenant {TenantId}; showing its hosts as active", tenantId);

            return QuotaCutoff.None;
        }
    }

    /// <summary>
    /// Gets how many of the tenant's machines are paused, or zero when the decision cannot be made.
    /// </summary>
    /// <param name="quotaPause">The pause service.</param>
    /// <param name="metrics">Where an unavailable decision is counted.</param>
    /// <param name="logger">Where the cause of an unavailable decision is logged.</param>
    /// <param name="tenantId">The tenant to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The paused machine count, or zero when it could not be determined.</returns>
    public static async Task<int> GetPausedCountOrZeroAsync(
        IQuotaPauseService quotaPause,
        QuotaPauseMetrics metrics,
        ILogger logger,
        int tenantId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(quotaPause);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return await quotaPause.GetPausedCountAsync(tenantId, ct);
        }
        catch (QuotaCheckUnavailableException ex)
        {
            metrics.RecordCheckFailure(QuotaCheckSurface.Display);
            logger.LogWarning(ex, "Quota pause count failed for tenant {TenantId}; showing no paused hosts", tenantId);

            return 0;
        }
    }
}
