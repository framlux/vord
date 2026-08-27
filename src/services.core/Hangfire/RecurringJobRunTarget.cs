// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Hangfire.Common;

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// The result of resolving a recurring job for an on-demand run.
/// </summary>
/// <param name="Job">The stored job definition, or null when it could not be resolved.</param>
/// <param name="Status">
/// <see cref="RecurringJobHealth.Scheduled"/> when the definition was resolved,
/// <see cref="RecurringJobHealth.Missing"/> when the job is absent from storage, or
/// <see cref="RecurringJobHealth.LoadFailed"/> when its payload will not deserialise. The
/// distinction matters to the operator: one means the worker never registered the job, the other
/// means a rename or refactor left a registration that can never run.
/// </param>
public sealed record RecurringJobRunTarget(Job? Job, RecurringJobHealth Status);
