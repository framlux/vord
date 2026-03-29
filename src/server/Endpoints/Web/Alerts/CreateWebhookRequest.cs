// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>Request to create a webhook endpoint.</summary>
public sealed class CreateWebhookRequest
{
    /// <summary>The webhook name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The webhook URL.</summary>
    public string Url { get; set; } = string.Empty;
}
