// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Which of a tenant's machines are paused for exceeding its machine limit. The oldest machines by
/// id stay active, so a single id describes the whole set and every surface can apply it without
/// re-reading the machines.
/// </summary>
public readonly record struct QuotaCutoff(QuotaCutoffKind Kind, long CutoffId)
{
    /// <summary>Nothing paused.</summary>
    public static QuotaCutoff None { get; } = new(QuotaCutoffKind.None, 0);

    /// <summary>Everything paused.</summary>
    public static QuotaCutoff All { get; } = new(QuotaCutoffKind.All, 0);

    /// <summary>Machines after <paramref name="cutoffId"/> are paused.</summary>
    public static QuotaCutoff After(long cutoffId)
    {
        return new QuotaCutoff(QuotaCutoffKind.After, cutoffId);
    }

    /// <summary>Whether the machine is paused under this cutoff.</summary>
    public bool IsPaused(long machineId)
    {
        return Kind switch
        {
            QuotaCutoffKind.All => true,
            QuotaCutoffKind.After => machineId > CutoffId,
            _ => false,
        };
    }
}
