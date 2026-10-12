// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Models;

/// <summary>A Kubernetes node together with the name of the machine it is linked to.</summary>
/// <param name="Node">The node.</param>
/// <param name="MachineName">The linked machine's name, or null while no enrolled machine is linked.</param>
public sealed record KubernetesNodeWithMachine(KubernetesNode Node, string? MachineName);
