// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Configuration options for the Resend email service.
/// </summary>
public sealed class ResendOptions
{
    /// <summary>
    /// The Resend API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The sender email address.
    /// </summary>
    public string FromEmail { get; set; } = string.Empty;
}
