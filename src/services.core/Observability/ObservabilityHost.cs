// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Which long-lived process is registering observability.
/// </summary>
/// <remarks>
/// Instruments are placed by host rather than registered everywhere. A gauge belongs in the process
/// that can actually observe what it reports: one whose backing service is absent would throw from
/// inside an observable callback and take the collection cycle with it, and the fleet gauge in
/// particular has to outlive an API server outage for the ingest-stalled rule to fire during one.
/// The host also owns the reported service name, so the two processes cannot drift into two
/// spellings of it.
/// </remarks>
public enum ObservabilityHost
{
    /// <summary>The REST and gRPC front end.</summary>
    ApiServer,

    /// <summary>The background job processor.</summary>
    ServicesWorker,
}
