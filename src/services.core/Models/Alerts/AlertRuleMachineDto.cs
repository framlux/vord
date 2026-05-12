// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Alerts;

/// <summary>Lightweight machine reference for alert rule display.</summary>
public sealed class AlertRuleMachineDto
{
    /// <summary>The machine ID.</summary>
    public long Id { get; set; }

    /// <summary>The machine display name.</summary>
    public string Name { get; set; } = string.Empty;
}
