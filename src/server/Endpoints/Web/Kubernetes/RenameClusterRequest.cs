// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Kubernetes;

/// <summary>
/// Request model for renaming a Kubernetes cluster.
/// </summary>
public sealed class RenameClusterRequest
{
    /// <summary>
    /// The new name for the cluster. Null, empty or whitespace resets the cluster to its derived name.
    /// </summary>
    public string? DisplayName { get; set; }
}
