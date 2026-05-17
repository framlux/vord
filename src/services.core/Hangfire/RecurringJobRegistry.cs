// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Hangfire;

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// Registers all Vord recurring jobs with Hangfire at services.worker startup.
/// Each phase of the Hangfire migration adds its job to <see cref="RegisterAll"/>.
/// </summary>
public static class RecurringJobRegistry
{
    /// <summary>
    /// Registers every recurring job. Safe to call repeatedly — Hangfire upserts by job id.
    /// </summary>
    /// <param name="recurringJobs">The Hangfire recurring job manager.</param>
    public static void RegisterAll(IRecurringJobManager recurringJobs)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        // Recurring job registrations are added by subsequent migration phases.
    }
}
