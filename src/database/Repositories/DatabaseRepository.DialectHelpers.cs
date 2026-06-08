// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Data.Sqlite;
using Npgsql;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Cross-dialect helpers shared by partial implementations of <see cref="DatabaseRepository"/>.
/// Kept in a dedicated file so multiple repositories (alert condition state, integration
/// delivery attempts, etc.) can call the same exception-mapping logic without duplication.
/// </summary>
public partial class DatabaseRepository
{
    /// <summary>
    /// Identifies the database-specific exception that signals a unique-index violation.
    /// PostgreSQL surfaces SQLSTATE 23505 via <see cref="PostgresException"/>; SQLite surfaces
    /// the precise extended code 2067 (SQLITE_CONSTRAINT_UNIQUE) or 1555 (PRIMARY KEY) via
    /// <see cref="SqliteException"/>. We deliberately do NOT match the generic constraint code
    /// 19 — that would also cover NOT NULL / CHECK violations, which must surface as bugs
    /// rather than being absorbed as "already claimed".
    /// </summary>
    /// <param name="ex">The exception thrown by the database driver.</param>
    /// <returns><c>true</c> when the exception represents a unique-index violation.</returns>
    private static bool IsUniqueViolation(Exception ex)
    {
        if ((ex is PostgresException pg) && (pg.SqlState == "23505"))
        {
            return true;
        }

        if (ex is SqliteException sq)
        {
            const int sqliteConstraintUnique = 2067;
            const int sqliteConstraintPrimaryKey = 1555;
            if ((sq.SqliteExtendedErrorCode == sqliteConstraintUnique)
                || (sq.SqliteExtendedErrorCode == sqliteConstraintPrimaryKey))
            {
                return true;
            }
        }

        return false;
    }
}
