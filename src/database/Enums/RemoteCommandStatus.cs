// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the status of a remote command.
/// </summary>
public enum RemoteCommandStatus : short
{
    /// <summary>Command is waiting to be delivered to the agent.</summary>
    Pending = 0,
    /// <summary>Command has been delivered to the agent.</summary>
    Delivered = 1,
    /// <summary>Command has been executed successfully.</summary>
    Executed = 2,
    /// <summary>Command execution failed.</summary>
    Failed = 3,
    /// <summary>Command expired before delivery or execution.</summary>
    Expired = 4,
    /// <summary>Command was rejected by the agent (invalid signature, revoked key, etc.).</summary>
    Rejected = 5
}
