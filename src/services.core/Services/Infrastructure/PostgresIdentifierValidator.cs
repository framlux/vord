// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.RegularExpressions;

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// Validates strings used as PostgreSQL identifiers (table names, column names) before they
/// are interpolated into raw DDL/DML. Per PostgreSQL spec an unquoted identifier must start
/// with a letter or underscore and contain only letters, digits, and underscores; the maximum
/// length is 63 characters (NAMEDATALEN - 1). Quoted identifiers can contain arbitrary
/// characters, but the Vord codebase does not use that form — the validator enforces the
/// unquoted shape.
/// </summary>
public static class PostgresIdentifierValidator
{
    private static readonly Regex ValidIdentifier = new(
        @"^[A-Za-z_][A-Za-z0-9_]{0,62}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Throws <see cref="ArgumentException"/> if <paramref name="identifier"/> does not match
    /// the safe-unquoted-identifier shape. Use this on every value that will be interpolated
    /// directly into a SQL statement; never trust callers to pre-validate.
    /// </summary>
    /// <param name="identifier">The identifier to validate.</param>
    /// <param name="paramName">Name of the originating parameter (for the exception).</param>
    public static void Validate(string identifier, string paramName)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (ValidIdentifier.IsMatch(identifier) == false)
        {
            throw new ArgumentException(
                $"'{identifier}' is not a safe Postgres identifier. Identifiers must start with a letter or underscore and contain only letters, digits, and underscores (max length 63).",
                paramName);
        }
    }
}
