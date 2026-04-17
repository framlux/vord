// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;

/// <summary>
/// Machine data returned to the UI.
/// </summary>
public sealed class MachineDto
{
    /// <summary>
    /// The machine's unique identifier.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The machine's name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The machine's description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The machine's physical location.
    /// </summary>
    public string? Location { get; set; }

    /// <summary>
    /// The machine's hostname from the pending machine record.
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// The machine's operating system.
    /// </summary>
    public OperatingSystems OperatingSystem { get; set; }

    /// <summary>
    /// The machine's hardware type.
    /// </summary>
    public MachineTypes MachineType { get; set; }

    /// <summary>
    /// The machine's serial number.
    /// </summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>
    /// The machine's asset tag.
    /// </summary>
    public string? AssetTag { get; set; }

    /// <summary>
    /// Whether the machine is currently online.
    /// </summary>
    public bool IsOnline { get; set; }

    /// <summary>
    /// The last time the machine sent a ping.
    /// </summary>
    public DateTimeOffset? LastPing { get; set; }

    /// <summary>
    /// When the machine was registered.
    /// </summary>
    public DateTimeOffset RegisteredOn { get; set; }

    /// <summary>
    /// Whether the machine has been soft-deleted.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Whether the agent on this machine accepts remote commands.
    /// </summary>
    public bool CommandsEnabled { get; set; }
}
