// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Startup;

/// <summary>
/// Validates CORS origins at startup. In Production an empty origins list silently breaks the
/// SPA and pushes operators toward a wildcard configuration (which is invalid under
/// <c>AllowCredentials</c> per the CORS spec); a wildcard explicitly is even worse. Fail fast
/// here so misconfiguration surfaces before the first user request.
/// </summary>
public static class CorsStartupValidator
{
    /// <summary>
    /// Validates the supplied origins for the given environment. Throws
    /// <see cref="InvalidOperationException"/> if origins is empty or contains a wildcard in
    /// Production. Lower environments allow empty so dev runs can skip the configuration step.
    /// </summary>
    /// <param name="origins">Configured CORS origins.</param>
    /// <param name="environmentName">Hosting environment name.</param>
    public static void Validate(string[] origins, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(environmentName);
        if (string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase) == false)
        {
            return;
        }

        if (origins.Length == 0)
        {
            throw new InvalidOperationException(
                "Cors:Origins must be a non-empty list of explicit origins in Production. Empty configuration silently breaks the SPA and invites unsafe operator workarounds.");
        }

        foreach (string origin in origins)
        {
            if (origin == "*")
            {
                throw new InvalidOperationException(
                    "Cors:Origins must not contain '*' in Production. A wildcard combined with AllowCredentials is invalid per the CORS spec and unsafe in all cases.");
            }
        }
    }
}
