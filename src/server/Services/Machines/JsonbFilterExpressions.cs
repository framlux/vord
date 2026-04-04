// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// LinqToDB custom SQL expressions for PostgreSQL JSONB predicates.
/// These methods are translated to SQL by LinqToDB and cannot be called in C# code.
/// Use <see cref="Infrastructure.ISqlDialect.SupportsJsonbFilters"/> to check availability before calling.
/// </summary>
public static class JsonbFilterExpressions
{
    /// <summary>
    /// Returns true if any disk in the DiskUsages JSONB array has usage_percent >= the given minimum.
    /// </summary>
    [Sql.Expression(
        "({0} IS NOT NULL AND EXISTS (SELECT 1 FROM jsonb_array_elements({0}) AS elem WHERE (elem->>'usage_percent')::integer >= {1}))",
        ServerSideOnly = true, IsPredicate = true)]
    public static bool HasDiskUsageAbove(string? diskUsages, int minPercent)
        => throw new InvalidOperationException("Server-side only");

    /// <summary>
    /// Returns true if all disks in the DiskUsages JSONB array have usage_percent &lt;= the given maximum.
    /// Also requires DiskUsages to be non-null.
    /// </summary>
    [Sql.Expression(
        "({0} IS NOT NULL AND NOT EXISTS (SELECT 1 FROM jsonb_array_elements({0}) AS elem WHERE (elem->>'usage_percent')::integer > {1}))",
        ServerSideOnly = true, IsPredicate = true)]
    public static bool AllDiskUsageAtOrBelow(string? diskUsages, int maxPercent)
        => throw new InvalidOperationException("Server-side only");

    /// <summary>
    /// Returns true if any disk in the HardwareHealth disk_smart JSONB array has health_status = 'FAILED'.
    /// </summary>
    [Sql.Expression(
        "({0} IS NOT NULL AND EXISTS (SELECT 1 FROM jsonb_array_elements({0}->'disk_smart') AS elem WHERE UPPER(elem->>'health_status') = 'FAILED'))",
        ServerSideOnly = true, IsPredicate = true)]
    public static bool HasFailedDiskSmart(string? hardwareHealth)
        => throw new InvalidOperationException("Server-side only");

    /// <summary>
    /// Returns true if all disks in the HardwareHealth disk_smart JSONB array have health_status != 'FAILED'.
    /// Returns true when HardwareHealth is NULL (no data means no known issues).
    /// </summary>
    [Sql.Expression(
        "({0} IS NULL OR NOT EXISTS (SELECT 1 FROM jsonb_array_elements({0}->'disk_smart') AS elem WHERE UPPER(elem->>'health_status') = 'FAILED'))",
        ServerSideOnly = true, IsPredicate = true)]
    public static bool AllDiskSmartHealthy(string? hardwareHealth)
        => throw new InvalidOperationException("Server-side only");

    /// <summary>
    /// Returns true if any fan has RPM = 0 or any power supply has a non-OK status.
    /// </summary>
    [Sql.Expression(
        "({0} IS NOT NULL AND (EXISTS (SELECT 1 FROM jsonb_array_elements({0}->'fans') AS elem WHERE (elem->>'rpm')::integer = 0) OR EXISTS (SELECT 1 FROM jsonb_array_elements({0}->'power_supplies') AS elem WHERE UPPER(elem->>'status') != 'OK')))",
        ServerSideOnly = true, IsPredicate = true)]
    public static bool HasHardwareIssue(string? hardwareHealth)
        => throw new InvalidOperationException("Server-side only");

    /// <summary>
    /// Returns the maximum usage_percent across all disks in the DiskUsages JSONB array.
    /// Returns 0 when DiskUsages is NULL. Used for SQL-level ORDER BY on disk usage.
    /// </summary>
    [Sql.Expression(
        "COALESCE((SELECT MAX((elem->>'usage_percent')::integer) FROM jsonb_array_elements({0}) AS elem), 0)",
        ServerSideOnly = true)]
    public static int MaxDiskUsagePercent(string? diskUsages)
        => throw new InvalidOperationException("Server-side only");

    /// <summary>
    /// Returns true if no fan has RPM = 0 and all power supplies have OK status.
    /// Returns true when HardwareHealth is NULL (no data means no known issues).
    /// </summary>
    [Sql.Expression(
        "({0} IS NULL OR (NOT EXISTS (SELECT 1 FROM jsonb_array_elements({0}->'fans') AS elem WHERE (elem->>'rpm')::integer = 0) AND NOT EXISTS (SELECT 1 FROM jsonb_array_elements({0}->'power_supplies') AS elem WHERE UPPER(elem->>'status') != 'OK')))",
        ServerSideOnly = true, IsPredicate = true)]
    public static bool NoHardwareIssues(string? hardwareHealth)
        => throw new InvalidOperationException("Server-side only");
}
