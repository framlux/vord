// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// The background jobs this build knows how to name. A closed vocabulary rather than a job type name
/// or a recurring id, because both of those are runtime strings and either would let a renamed class
/// or a mistyped id mint a permanent series.
/// </summary>
public enum InstrumentedJob
{
    /// <summary>Expires remote commands that were never collected.</summary>
    RemoteCommandExpiry,

    /// <summary>Creates and drops telemetry table partitions.</summary>
    PartitionManagement,

    /// <summary>Enumerates active tenants and enqueues a health sweep for each.</summary>
    HealthSweepCoordinator,

    /// <summary>Evaluates threshold alert rules across paid tenants.</summary>
    AlertEvaluation,

    /// <summary>Reaps orphaned alert condition state rows.</summary>
    AlertConditionStateCleanup,

    /// <summary>Purges tenants past their deletion grace period.</summary>
    TenantPurge,

    /// <summary>Reconciles subscription state with billing. Hosted deployment only.</summary>
    StripeSync,

    /// <summary>Produces requested data exports. Object storage only.</summary>
    DataExportProcessing,

    /// <summary>Removes expired data exports. Object storage only.</summary>
    DataExportCleanup,

    /// <summary>Sweeps one tenant's machine health. Enqueued per tenant by the coordinator.</summary>
    HealthSweepTenant,

    /// <summary>Delivers one alert to one third-party integration.</summary>
    IntegrationDelivery,

    /// <summary>Reclassifies telemetry rows into their retention class.</summary>
    RetentionReclassify,

    /// <summary>Sends one tenant invitation email.</summary>
    SendInvitationEmail,

    /// <summary>Evaluates SSH connection event alerts for one batch.</summary>
    SshAlertEvaluation,

    /// <summary>A job this build does not know about. A rising count here means this enum needs a
    /// member, not that the tag rule failed.</summary>
    Other,
}
