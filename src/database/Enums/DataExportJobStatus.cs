// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Status of a data export job.
/// </summary>
public enum DataExportJobStatus
{
    /// <summary>
    /// Job has been created and is waiting to be processed.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Job is currently being processed.
    /// </summary>
    Processing = 1,

    /// <summary>
    /// Job has completed successfully.
    /// </summary>
    Complete = 2,

    /// <summary>
    /// Job has failed.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// Export file has expired and been deleted from storage.
    /// </summary>
    Expired = 4
}
