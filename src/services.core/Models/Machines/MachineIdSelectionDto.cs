// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Machines;

/// <summary>
/// The machines a filter matches, as ids, for building a selection without transferring the
/// machines themselves.
/// </summary>
public sealed class MachineIdSelectionDto
{
    /// <summary>
    /// The matching machine ids, in ascending id order, capped at <see cref="MachineSelectionLimits.MaxSelectableIds"/>.
    /// </summary>
    public List<long> Ids { get; set; } = new();

    /// <summary>
    /// How many machines the filter matches in total, counted before the cap.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Whether the cap withheld ids. When true, <see cref="Ids"/> is a prefix of the match rather
    /// than the whole of it, and a caller must not present the selection as complete.
    /// </summary>
    public bool Truncated { get; set; }
}
