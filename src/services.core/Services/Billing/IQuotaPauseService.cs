// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Decides which of a tenant's hosts are paused for exceeding its machine limit. The decision is
/// derived on every ask and never stored: the oldest N registered machines by id stay active and the
/// rest are paused, so raising the limit, deleting a machine or a tier change takes effect without
/// anything having to be un-paused.
/// </summary>
public interface IQuotaPauseService
{
    /// <summary>
    /// Gets the tenant's cutoff, shared through the cache so every replica gives the same answer.
    /// </summary>
    /// <param name="tenantId">The tenant to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The cutoff describing every paused machine of the tenant.</returns>
    /// <exception cref="QuotaCheckUnavailableException">The decision could not be made.</exception>
    Task<QuotaCutoff> GetCutoffAsync(int tenantId, CancellationToken ct);

    /// <summary>
    /// Whether a machine is paused under its tenant's current cutoff.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the machine.</param>
    /// <param name="machineId">The machine to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the machine falls outside the tenant's oldest N machines.</returns>
    /// <exception cref="QuotaCheckUnavailableException">The decision could not be made.</exception>
    Task<bool> IsPausedAsync(int tenantId, long machineId, CancellationToken ct);

    /// <summary>
    /// How many of the tenant's registered machines are paused. Computed from the live registered
    /// count and the current limit rather than from the cached cutoff, which holds an id and not a count.
    /// </summary>
    /// <param name="tenantId">The tenant to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of paused machines; zero when the tenant is within its limit.</returns>
    /// <exception cref="QuotaCheckUnavailableException">The decision could not be made.</exception>
    Task<int> GetPausedCountAsync(int tenantId, CancellationToken ct);
}
