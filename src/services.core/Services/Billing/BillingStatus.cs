// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Singleton service that caches the Billing:Enabled configuration value.
/// </summary>
public sealed class BillingStatus : IBillingStatus
{
    /// <inheritdoc/>
    public bool IsEnabled { get; }

    /// <summary>
    /// Creates a new instance of the <see cref="BillingStatus"/> class.
    /// </summary>
    public BillingStatus(IOptions<BillingOptions> billingOptions)
    {
        IsEnabled = billingOptions.Value.Enabled;
    }
}
