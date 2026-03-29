// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// DTO for creating a registration token.
/// </summary>
public sealed class CreateRegistrationTokenRequest
{
    /// <summary>A friendly name for the token.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Number of days until the token expires.</summary>
    public int ExpiresInDays { get; set; } = 30;

    /// <summary>Maximum number of times this token can be used.</summary>
    public int MaxUses { get; set; } = 100;
}
