// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// The capability bits an agent reports about itself when it fetches its configuration.
/// </summary>
public static class AgentCapabilities
{
    /// <summary>
    /// Set when the host has opted in to remote commands. The value is part of the wire contract with
    /// the agent.
    /// </summary>
    public const ulong RemoteCommands = 1UL;

    /// <summary>
    /// Whether a reported capability mask has the remote commands bit set.
    /// </summary>
    /// <param name="capabilities">The capability mask the agent reported.</param>
    /// <returns>True when remote commands are enabled on the host.</returns>
    public static bool HasRemoteCommands(ulong capabilities)
    {
        return (capabilities & RemoteCommands) != 0;
    }
}
