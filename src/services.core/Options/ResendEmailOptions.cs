// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Settings for the Resend transport, used by the hosted deployment.
/// </summary>
public sealed class ResendEmailOptions
{
    /// <summary>
    /// The Resend API key. Required in a hosted deployment; ignored in a self-hosted one.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}
