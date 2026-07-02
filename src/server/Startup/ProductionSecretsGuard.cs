// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Startup;

/// <summary>
/// Refuses to start in Production with placeholder or empty critical secrets so a deployment can
/// never silently run with the shipped example credentials.
/// </summary>
public static class ProductionSecretsGuard
{
    private static readonly string[] Placeholders = ["CHANGE_ME", "changeme", "password", "REPLACE_ME"];

    /// <summary>
    /// Validates critical secrets for the given environment. Throws in Production when a required
    /// secret is missing, empty, or a known placeholder.
    /// </summary>
    /// <param name="environmentName">The hosting environment name.</param>
    /// <param name="databasePassword">The configured database password.</param>
    public static void Validate(string environmentName, string? databasePassword)
    {
        ArgumentNullException.ThrowIfNull(environmentName);

        bool isProduction = string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
        if (isProduction == false)
        {
            return;
        }

        if (IsMissingOrPlaceholder(databasePassword))
        {
            throw new InvalidOperationException(
                "Database:Password is empty or a placeholder in Production. Set a real secret before starting.");
        }
    }

    private static bool IsMissingOrPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        foreach (string placeholder in Placeholders)
        {
            if (string.Equals(value, placeholder, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
