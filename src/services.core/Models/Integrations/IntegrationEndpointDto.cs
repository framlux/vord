// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Integrations;

/// <summary>
/// Data transfer object for integration endpoint responses.
/// </summary>
public sealed class IntegrationEndpointDto
{
    /// <summary>The integration endpoint ID.</summary>
    public int Id { get; set; }

    /// <summary>The integration provider type.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The user-facing name for this integration.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether this integration is enabled for delivery.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>When the integration was created (ISO8601).</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>
    /// The plaintext secret for Custom provider integrations.
    /// Only populated on Create and RotateSecret responses.
    /// </summary>
    public string? Secret { get; set; }
}
