// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Machines;

/// <summary>
/// Bounds for resolving a filter to a set of machine ids.
/// </summary>
public static class MachineSelectionLimits
{
    /// <summary>
    /// The most ids a single selection query will return. Chosen to match the largest seeded tier
    /// machine limit so a tenant on any shipped plan can select its whole fleet in one call. It is
    /// a backstop rather than a business rule: a deployment that raises its tier limits past this
    /// will see selections reported as truncated rather than silently short.
    /// </summary>
    public const int MaxSelectableIds = 10000;
}
