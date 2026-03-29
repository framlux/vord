// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.MigrationRunner.Services;

/// <summary>
/// Tracks migration execution state for readiness health check.
/// </summary>
public sealed class MigrationState
{
    /// <summary>
    /// Indicates whether migrations have completed (successfully or with failure).
    /// </summary>
    public bool Completed { get; private set; }

    /// <summary>
    /// Indicates whether migrations succeeded.
    /// </summary>
    public bool Succeeded { get; private set; }

    /// <summary>
    /// If migrations failed, contains the exception; otherwise, null.
    /// </summary>
    public Exception? Error { get; private set; }

    /// <summary>
    /// Marks the migration state as successful.
    /// </summary>
    public void MarkSuccess()
    {
        Completed = true;
        Succeeded = true;
        Error = null;
    }

    /// <summary>
    /// Marks the migration state as failed with the given exception.
    /// </summary>
    /// <param name="ex"></param>
    public void MarkFailure(Exception ex)
    {
        Completed = true;
        Succeeded = false;
        Error = ex;
    }
}
