// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Puts a tenant on the Enterprise tier for an agreement revision. The only path into Enterprise.
/// </summary>
public interface IEnterpriseAgreementHandler
{
    /// <summary>
    /// Applies an agreement revision: tier, status, term end and every limit in one transaction, then
    /// cache invalidation, retention reclassification and restoration of tier-gated resources.
    /// </summary>
    /// <param name="terms">The agreement revision.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether the revision was applied, already applied, or older than the one applied.</returns>
    /// <exception cref="ArgumentException">A limit or the revision is out of range.</exception>
    Task<EnterpriseApplyOutcome> ApplyAsync(EnterpriseAgreementTerms terms, CancellationToken ct);
}
