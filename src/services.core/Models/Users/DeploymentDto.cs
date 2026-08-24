// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Users;

/// <summary>
/// Deployment facts the web application needs in order to render the right product. Carried on the
/// session payload the client already fetches, so the interface can never disagree with the server
/// about which mode is running.
/// </summary>
public sealed class DeploymentDto
{
    /// <summary>
    /// Whether the server is running as a self-hosted deployment. When true the client hides
    /// billing, tiers and upgrade prompts.
    /// </summary>
    public bool SelfHosted { get; set; }
}
