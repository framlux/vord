// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Data.Common;

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// Classifies the errors the Kubernetes inventory projection meets when it applies a report.
/// </summary>
public static class KubernetesProjectionErrors
{
    /// <summary>PostgreSQL SQLSTATE for a serialization failure, which succeeds when the transaction is run again.</summary>
    private const string SerializationFailure = "40001";

    /// <summary>PostgreSQL SQLSTATE for a detected deadlock, which succeeds when the transaction is run again.</summary>
    private const string DeadlockDetected = "40P01";

    /// <summary>
    /// Decides whether an error is likely to go away on its own, so the same row is worth applying
    /// again. The causes behind a wrapper are checked too, since a provider may wrap the error it got.
    /// </summary>
    /// <param name="exception">The error the apply failed with.</param>
    /// <returns>True for connection trouble, timeouts, serialization failures and deadlocks.</returns>
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return true;
            }

            if (current is DbException database)
            {
                if (database.IsTransient == true)
                {
                    return true;
                }

                if (string.Equals(database.SqlState, SerializationFailure, StringComparison.Ordinal)
                    || string.Equals(database.SqlState, DeadlockDetected, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
