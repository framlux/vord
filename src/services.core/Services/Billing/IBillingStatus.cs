// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Provides cached billing configuration status.
/// </summary>
public interface IBillingStatus
{
    /// <summary>
    /// Whether billing integration is enabled for this deployment.
    /// </summary>
    bool IsEnabled { get; }
}
