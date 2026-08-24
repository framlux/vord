// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Selects which of the two supported deployment shapes this process is running as. This is the
/// single switch: billing, the internal control plane, entitlement limits and the email provider
/// all derive from it, and nothing else may reintroduce a second, independently settable signal.
/// </summary>
public sealed class DeploymentOptions
{
    /// <summary>
    /// Whether this is a self-hosted deployment. Defaults to true so a clone of this repository
    /// runs correctly with no configuration at all; the hosted SaaS deployment is the one that
    /// opts out explicitly.
    /// </summary>
    public bool SelfHosted { get; set; } = true;
}
