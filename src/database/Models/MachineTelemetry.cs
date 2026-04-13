// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents a stored telemetry record from a machine agent.
/// </summary>
[Table(TableNames.MachineTelemetry)]
public sealed class MachineTelemetry
{
    /// <summary>
    /// Unique identifier for the telemetry record.
    /// </summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public long Id { get; set; }

    /// <summary>
    /// The machine that sent the telemetry.
    /// </summary>
    [Column("MachineId"), NotNull]
    public required long MachineId { get; set; }

    /// <summary>
    /// The tenant that owns the machine. Denormalized for retention queries and partition readiness.
    /// </summary>
    [Column("TenantId"), NotNull]
    public required int TenantId { get; set; }

    /// <summary>
    /// The type of telemetry data (maps to TelemetryQueueItemTypes).
    /// </summary>
    [Column("TelemetryType"), NotNull]
    public required short TelemetryType { get; set; }

    /// <summary>
    /// The JSON payload of the telemetry data.
    /// </summary>
    [Column("Payload"), NotNull]
    public required string Payload { get; set; }

    /// <summary>
    /// When the telemetry was received by the consumer.
    /// </summary>
    [Column("ReceivedAt"), NotNull]
    public required DateTimeOffset ReceivedAt { get; set; }

    /// <summary>
    /// The CloudEvent ID for deduplication.
    /// </summary>
    [Column("SourceEventId"), MaxLength(64)]
    public string? SourceEventId { get; set; }
}
