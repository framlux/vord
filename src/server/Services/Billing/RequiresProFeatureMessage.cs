// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// Endpoint metadata carrying the feature-specific 403 message returned by
/// <see cref="ProSubscriptionPreProcessor"/> when a tenant lacks an active Pro or Team
/// subscription. Endpoints tagged <see cref="EndpointTags.RequiresProSubscription"/> attach this
/// via <c>Options(b => b.WithMetadata(new RequiresProFeatureMessage(...)))</c> in Configure() so
/// the same shared pre-processor can report the feature-appropriate wording instead of a single
/// message shared across unrelated features.
/// </summary>
/// <param name="Message">The feature-specific message to return in the 403 response.</param>
public sealed record RequiresProFeatureMessage(string Message);
