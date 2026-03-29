// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database;

/// <summary>
/// Specifies which database provider to use.
/// </summary>
public enum DatabaseProvider
{
    /// <summary>PostgreSQL (production).</summary>
    PostgreSQL,

    /// <summary>SQLite (testing).</summary>
    SQLite
}
