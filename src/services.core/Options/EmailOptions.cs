// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Email configuration. The transport is chosen by deployment mode rather than configured here:
/// the hosted deployment sends through Resend, a self-hosted one through SMTP.
/// </summary>
public sealed class EmailOptions
{
    /// <summary>
    /// The sender address used by whichever transport is active. For Resend it must be on a domain
    /// verified in Resend, or every send is rejected.
    /// </summary>
    public string FromEmail { get; set; } = string.Empty;

    /// <summary>
    /// Resend transport settings, used in a hosted deployment.
    /// </summary>
    public ResendEmailOptions Resend { get; set; } = new();

    /// <summary>
    /// SMTP transport settings, used in a self-hosted deployment.
    /// </summary>
    public SmtpEmailOptions Smtp { get; set; } = new();
}
