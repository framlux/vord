// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>What applying a local Kubernetes report did to the inventory.</summary>
public enum LocalReportOutcome
{
    /// <summary>The machine was linked to a node of an existing cluster.</summary>
    Grouped,

    /// <summary>No cluster held the machine's fingerprint, so a new cluster was created for it.</summary>
    ClusterCreated,

    /// <summary>Several clusters share the fingerprint and the node name did not pick one, so nothing was written.</summary>
    Ambiguous,

    /// <summary>The machine belongs to no cluster and reported no fingerprint to group it by.</summary>
    NoFingerprint,

    /// <summary>The report describes a host with no kubelet, which is not a node.</summary>
    NotANode,

    /// <summary>The machine is deleted, unknown or belongs to another tenant.</summary>
    MachineDeleted,

    /// <summary>The report is older than newer state for the same node and was not applied.</summary>
    Superseded,

    /// <summary>
    /// The machine runs no kubelet but is an enrolled reporter candidate of a cluster, so only its
    /// candidate row was written.
    /// </summary>
    CandidateOnly,
}
