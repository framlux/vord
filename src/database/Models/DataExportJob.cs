// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents a background data export job for a tenant.
/// </summary>
[Table(TableNames.DataExportJobs)]
public sealed class DataExportJob
{
    /// <summary>
    /// Unique identifier for the export job.
    /// </summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public int Id { get; set; }

    /// <summary>
    /// The tenant whose data is being exported.
    /// </summary>
    [Column("TenantId"), NotNull]
    public required int TenantId { get; set; }

    /// <summary>
    /// Current status of the export job.
    /// </summary>
    [Column("Status"), NotNull]
    public required DataExportJobStatus Status { get; set; }

    /// <summary>
    /// The user who requested the export.
    /// </summary>
    [Column("RequestedByUserId"), NotNull]
    public required int RequestedByUserId { get; set; }

    /// <summary>
    /// When the export was requested.
    /// </summary>
    [Column("RequestedAt"), NotNull]
    public required DateTimeOffset RequestedAt { get; set; }

    /// <summary>
    /// When the export transitioned from Pending to Processing. Used by the orphan reaper to
    /// detect stuck Processing rows after a worker crash.
    /// </summary>
    [Column("StartedAt")]
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// When the export completed (successfully or with failure).
    /// </summary>
    [Column("CompletedAt")]
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The object storage key for the exported file.
    /// </summary>
    [Column("ObjectKey"), NotNull]
    public required string ObjectKey { get; set; }

    /// <summary>
    /// When the exported file expires and should be deleted from storage.
    /// </summary>
    [Column("ExpiresAt"), NotNull]
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Random token for shareable, pre-authenticated download URLs.
    /// </summary>
    [Column("DownloadToken"), NotNull]
    public required string DownloadToken { get; set; }

    /// <summary>
    /// Error message if the job failed.
    /// </summary>
    [Column("ErrorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Size of the exported file in bytes.
    /// </summary>
    [Column("FileSizeBytes")]
    public long? FileSizeBytes { get; set; }

    /// <summary>
    /// Number of processing attempts that have failed. Used by DataExportProcessingJob to cap
    /// retries on a poison job — after MaxFailures the row transitions to Failed instead of
    /// being reset to Pending, so a permanently-broken job no longer generates one Failed
    /// Hangfire entry per minute forever.
    /// </summary>
    [Column("FailureCount"), NotNull]
    public int FailureCount { get; set; }
}
