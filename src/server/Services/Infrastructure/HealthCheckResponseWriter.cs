// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Provides response writers for health check endpoints.
/// </summary>
public static class HealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Writes a minimal health check response (liveness probe).
    /// </summary>
    public static async Task WriteMinimal(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        string json = JsonSerializer.Serialize(new { status = report.Status.ToString() }, JsonOptions);
        await context.Response.WriteAsync(json);
    }

    /// <summary>
    /// Writes a detailed health check response including individual check results (readiness probe).
    /// </summary>
    public static async Task WriteDetailed(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        Dictionary<string, object> entries = [];
        foreach (KeyValuePair<string, HealthReportEntry> entry in report.Entries)
        {
            entries[entry.Key] = new
            {
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                duration = entry.Value.Duration.TotalMilliseconds
            };
        }

        object result = new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration.TotalMilliseconds,
            entries
        };

        string json = JsonSerializer.Serialize(result, JsonOptions);
        await context.Response.WriteAsync(json);
    }
}
