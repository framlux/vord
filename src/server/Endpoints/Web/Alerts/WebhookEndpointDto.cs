// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>DTO for webhook endpoints.</summary>
public sealed class WebhookEndpointDto
{
    /// <summary>The webhook ID.</summary>
    public int Id { get; set; }

    /// <summary>The webhook name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The webhook URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Whether the webhook is enabled.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>When the webhook was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
