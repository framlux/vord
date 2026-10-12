// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Models;

/// <summary>One condition of a node in a cluster snapshot that is not in its healthy state.</summary>
/// <param name="Type">The condition type, such as <c>MemoryPressure</c>.</param>
/// <param name="Status">The condition status, <c>True</c> or <c>Unknown</c>.</param>
/// <param name="LastTransitionTime">When the condition last changed status, or null when the cluster did not say.</param>
public sealed record ClusterSnapshotCondition(string Type, string Status, DateTimeOffset? LastTransitionTime);
