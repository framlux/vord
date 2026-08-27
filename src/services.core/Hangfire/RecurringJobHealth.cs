// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// Operator-facing health of a single recurring job, derived from Hangfire storage state.
/// </summary>
public enum RecurringJobHealth
{
    /// <summary>State could not be determined.</summary>
    Unknown = 0,

    /// <summary>Registered, with its next occurrence ahead of now or within the grace window.</summary>
    Scheduled = 1,

    /// <summary>Registered, but its next occurrence is further in the past than the grace window.</summary>
    Overdue = 2,

    /// <summary>Hangfire disabled the schedule after repeated scheduling failures.</summary>
    Disabled = 3,

    /// <summary>Known to this build but absent from storage.</summary>
    Missing = 4,

    /// <summary>Registered, but its payload could not be deserialised into a job.</summary>
    LoadFailed = 5,
}
