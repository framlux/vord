// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models.Kubernetes;

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// The machines around one cluster's reporting: the ones enrolled as reporter candidates, and the ones
/// that could be enrolled.
/// </summary>
/// <param name="Candidates">The enrolled candidates, ordered by machine name.</param>
/// <param name="EligibleMachines">The machines that can be enrolled, ordered by machine name.</param>
public sealed record KubernetesReporterMachines(
    IReadOnlyList<KubernetesCandidateDto> Candidates,
    IReadOnlyList<KubernetesEligibleMachineDto> EligibleMachines);
