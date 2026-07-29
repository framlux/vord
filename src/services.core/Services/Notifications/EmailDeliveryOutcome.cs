// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Notifications;

/// <summary>
/// The result of attempting to deliver an email. A single boolean could not distinguish "no
/// provider is configured, which is a supported deployment" from "the provider rejected this
/// message", so callers treated both as retryable and a keyless install accumulated permanently
/// failed jobs.
/// </summary>
public enum EmailDeliveryOutcome
{
    /// <summary>
    /// No email provider is configured, so nothing was sent. Not a failure — retrying cannot help
    /// and the caller must treat this as success.
    /// </summary>
    Skipped,

    /// <summary>
    /// The message was accepted by the provider.
    /// </summary>
    Sent,

    /// <summary>
    /// The provider was configured but rejected the message or was unreachable. Retrying may help.
    /// </summary>
    Failed,
}
