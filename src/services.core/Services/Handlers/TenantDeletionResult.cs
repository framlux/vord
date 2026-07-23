// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>Outcome of a tenant-deletion Phase-1 operation.</summary>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="Message">Human-readable result or rejection reason.</param>
/// <param name="ScheduledPurgeAt">The scheduled purge instant, when a deletion was created.</param>
public sealed record TenantDeletionResult(bool Success, string Message, DateTimeOffset? ScheduledPurgeAt);
