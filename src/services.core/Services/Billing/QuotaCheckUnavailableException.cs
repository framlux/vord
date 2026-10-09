// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// The quota pause decision could not be made. Callers choose how to fail: anything that would stop
/// a paying fleet reporting treats the host as active, and alert evaluation skips the tenant.
/// </summary>
public sealed class QuotaCheckUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public QuotaCheckUnavailableException(int tenantId, Exception innerException)
        : base($"The quota pause decision for tenant {tenantId} is unavailable", innerException)
    {
        TenantId = tenantId;
    }

    /// <summary>The tenant whose decision failed.</summary>
    public int TenantId { get; }
}
