// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Configuration options for application-level settings.
/// </summary>
public sealed class AppOptions
{
    /// <summary>
    /// The base URL of the application, used for generating invitation links.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The number of days a newly created registration token remains valid before it expires.
    /// Defaults to 7 days.
    /// </summary>
    public int RegistrationTokenLifetimeDays { get; set; } = 7;
}
