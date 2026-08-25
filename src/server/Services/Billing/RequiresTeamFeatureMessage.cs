// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// Endpoint metadata carrying the feature-specific 403 message returned by
/// <see cref="TeamSubscriptionPreProcessor"/> when a tenant is not on the Team tier. Endpoints
/// tagged <see cref="EndpointTags.RequiresTeamSubscription"/> attach this via
/// <c>Options(b => b.WithMetadata(new RequiresTeamFeatureMessage(...)))</c> in Configure() so the
/// shared pre-processor can report the feature-appropriate wording rather than one message shared
/// across unrelated features.
/// </summary>
/// <param name="Message">The feature-specific message to return in the 403 response.</param>
public sealed record RequiresTeamFeatureMessage(string Message);
