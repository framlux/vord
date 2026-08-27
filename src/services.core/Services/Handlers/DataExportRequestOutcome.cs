// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// The outcome of asking for a data export. Carries the eligibility instant alongside the job id
/// so a refusal can say when the caller may retry without asking the database a second time — a
/// second read can disagree with the first, and answering "not now" with no time attached is the
/// worst version of the response.
/// </summary>
public sealed class DataExportRequestOutcome
{
    /// <summary>
    /// The identifier of the created export job, or zero when no job was created.
    /// </summary>
    public int JobId { get; init; }

    /// <summary>
    /// When the tenant may next generate an export, set only when the request was refused because
    /// the tier cooldown had not elapsed.
    /// </summary>
    public DateTimeOffset? NextEligibleAt { get; init; }
}
