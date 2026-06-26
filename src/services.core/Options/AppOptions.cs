// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Configuration options for application-level settings.
/// </summary>
public sealed class AppOptions
{
    /// <summary>
    /// The absolute base URL of the application, used for generating alert and invitation
    /// email links. Must be a valid absolute URL; validated at startup so a missing or
    /// malformed value fails fast rather than producing broken relative links in emails.
    /// </summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The number of days a newly created registration token remains valid before it expires.
    /// Defaults to 7 days.
    /// </summary>
    public int RegistrationTokenLifetimeDays { get; set; } = 7;
}
