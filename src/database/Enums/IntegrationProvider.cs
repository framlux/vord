// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the type of integration provider for alert delivery.
/// </summary>
public enum IntegrationProvider : short
{
    /// <summary>Invalid/unset provider. Used for detection of invalid values.</summary>
    None = 0,

    /// <summary>Slack incoming webhook integration.</summary>
    Slack = 1,

    /// <summary>Microsoft Teams incoming webhook connector.</summary>
    MicrosoftTeams = 2,

    /// <summary>Discord webhook integration.</summary>
    Discord = 3,

    /// <summary>PagerDuty Events API v2 integration.</summary>
    PagerDuty = 4,

    /// <summary>Custom HMAC-signed webhook endpoint.</summary>
    Custom = 5,
}
