// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Models;

/// <summary>One parsed local Kubernetes report from a machine, ready to apply to the inventory.</summary>
/// <param name="TelemetryId">The <see cref="MachineTelemetry.Id"/> of the row the report came from.</param>
/// <param name="TenantId">The tenant that owns the reporting machine.</param>
/// <param name="MachineId">The machine that sent the report.</param>
/// <param name="ServerReceivedAt">When the server accepted the report, which orders reports from one machine.</param>
/// <param name="Distro">The Kubernetes distribution the machine reported.</param>
/// <param name="NodeName">The Kubernetes node name the machine reported, empty when it runs no kubelet.</param>
/// <param name="HasKubelet">True when the machine runs a kubelet and so is a node.</param>
/// <param name="ControlPlane">True when the machine reported that it runs a control plane.</param>
/// <param name="CaFingerprint">The lowercase hex SHA-256 of the cluster CA certificate, or null when unreadable.</param>
/// <param name="KubeletVersion">The kubelet version the machine reported, if any.</param>
public sealed record LocalNodeReport(
    long TelemetryId,
    int TenantId,
    long MachineId,
    DateTimeOffset ServerReceivedAt,
    string Distro,
    string NodeName,
    bool HasKubelet,
    bool ControlPlane,
    string? CaFingerprint,
    string? KubeletVersion);
