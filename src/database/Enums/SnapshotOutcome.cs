// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>What applying a cluster snapshot did to the inventory.</summary>
public enum SnapshotOutcome
{
    /// <summary>The snapshot was newer than everything the cluster had accepted and its nodes were applied.</summary>
    Accepted,

    /// <summary>
    /// The snapshot came from an older Lease epoch or an earlier holder than the cluster's latest
    /// accepted snapshot, so none of its content was applied.
    /// </summary>
    Fenced,

    /// <summary>The sending machine is deleted, unknown or belongs to another tenant.</summary>
    MachineDeleted,

    /// <summary>The snapshot reported an error, which was recorded on the cluster and the sender's candidate row.</summary>
    ErrorRecorded,

    /// <summary>
    /// The snapshot belongs to no known cluster and carries too little to found one, because it reports
    /// an error or no CA fingerprint, so nothing was written.
    /// </summary>
    Unresolved,
}
