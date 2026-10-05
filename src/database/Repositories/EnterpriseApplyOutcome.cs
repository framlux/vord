// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// The result of writing an enterprise agreement onto a tenant's subscription.
/// </summary>
public enum EnterpriseApplyOutcome
{
    /// <summary>The subscription now carries this revision.</summary>
    Applied = 1,

    /// <summary>This revision was already applied; nothing was written.</summary>
    AlreadyApplied = 2,

    /// <summary>A newer revision is already applied; nothing was written.</summary>
    StaleRevision = 3,
}
