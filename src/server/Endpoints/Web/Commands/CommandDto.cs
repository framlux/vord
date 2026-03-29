// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Commands;

/// <summary>
/// Response DTO for a remote command.
/// </summary>
public sealed class CommandDto
{
    /// <summary>
    /// The database ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The client-generated command UUID.
    /// </summary>
    public string CommandId { get; set; } = string.Empty;

    /// <summary>
    /// The target machine ID.
    /// </summary>
    public long MachineId { get; set; }

    /// <summary>
    /// The command type.
    /// </summary>
    public string CommandType { get; set; } = string.Empty;

    /// <summary>
    /// The command status.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// When the command was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the command expires.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// When the command was delivered to the agent.
    /// </summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>
    /// When the command execution completed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The exit code from execution.
    /// </summary>
    public int? ExitCode { get; set; }

    /// <summary>
    /// The result message from the agent.
    /// </summary>
    public string? ResultMessage { get; set; }
}
