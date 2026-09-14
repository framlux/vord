// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// How one alert delivery to one integration endpoint ended.
/// </summary>
public enum IntegrationDeliveryOutcome
{
    /// <summary>The receiver accepted it.</summary>
    Delivered,

    /// <summary>The receiver was unreachable or answered 5xx. Retried, and resolves on its own.</summary>
    Transient,

    /// <summary>The receiver answered 4xx. Permanent, retries are deliberately suppressed, and
    /// somebody has to change a configuration before it will ever succeed.</summary>
    Rejected,

    /// <summary>An unexpected failure, most likely a defect in a payload formatter. Retries are
    /// suppressed because they cannot help.</summary>
    Error,

    /// <summary>No formatter is registered for the endpoint's provider, so nothing was ever sent.
    /// The delivery loop skips these silently, which makes this the least visible way for a
    /// customer to stop receiving alerts.</summary>
    Unformattable,
}
