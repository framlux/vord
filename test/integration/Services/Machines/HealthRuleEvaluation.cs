// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Integration.Services.Machines;

/// <summary>
/// Runs one <see cref="HealthRuleCase"/> through a dialect's health sweep and reports what the
/// database wrote, so the same case can be evaluated against PostgreSQL and against SQLite.
/// </summary>
public static class HealthRuleEvaluation
{
    // Not a status the rule can produce. Seeding it means a sweep that matched no rows is read as
    // a failure rather than mistaken for a sweep that computed the expected answer, which a seed
    // inside the valid range would allow whenever the expectation happened to equal the seed.
    private const short UnsweptSentinel = 9;

    /// <summary>
    /// Evaluates a case against the production PostgreSQL rule.
    /// </summary>
    /// <param name="connectionString">Connection string for a migrated PostgreSQL database.</param>
    /// <param name="testCase">The case to evaluate.</param>
    public static async Task<short> EvaluateInPostgresAsync(string connectionString, HealthRuleCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);

        DataOptions<DatabaseContext> options = new(new DataOptions().UsePostgreSQL(connectionString));
        using DatabaseContext db = new(options);

        int tenantId = await db.InsertWithInt32IdentityAsync(new Tenant
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Health Rule Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        });

        long registrationTokenId = await db.InsertWithInt64IdentityAsync(new RegistrationToken
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Health Rule Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            IsRevoked = false,
        });

        long machineId = await db.InsertWithInt64IdentityAsync(new Machine
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = "m",
            SerialNumber = Guid.NewGuid().ToString("N"),
            SystemId = Guid.NewGuid().ToString("N"),
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = registrationTokenId,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId,
        });

        await db.InsertAsync(BuildSummary(testCase, machineId, tenantId));

        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);
        await repo.SweepHealthStatusAsync(
            new PostgresSqlDialect().HealthSweepForTenant,
            tenantId,
            HealthRuleCases.OnlineThresholdSeconds,
            CancellationToken.None);

        MachineStateSummary swept = await db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .FirstAsync();

        return swept.HealthStatus;
    }

    /// <summary>
    /// Evaluates a case against the SQLite rule the unit and functional harnesses run on.
    /// </summary>
    /// <param name="testCase">The case to evaluate.</param>
    public static async Task<short> EvaluateInSqliteAsync(HealthRuleCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);

        using TestDatabaseFactory dbFactory = new();

        // Foreign keys are off in this harness, so the summary row stands on its own; the tenant
        // and machine rows the PostgreSQL side needs would add nothing the rule reads.
        const int TenantId = 1;
        const long MachineId = 1;
        await dbFactory.Context.InsertAsync(BuildSummary(testCase, MachineId, TenantId));

        DatabaseRepository repo = new(dbFactory.Context, NullLogger<DatabaseRepository>.Instance);
        await repo.SweepHealthStatusAsync(
            new SqliteSqlDialect().HealthSweepForTenant,
            TenantId,
            HealthRuleCases.OnlineThresholdSeconds,
            CancellationToken.None);

        MachineStateSummary swept = await dbFactory.Context.MachineStateSummaries
            .Where(s => s.MachineId == MachineId)
            .FirstAsync();

        return swept.HealthStatus;
    }

    private static MachineStateSummary BuildSummary(HealthRuleCase testCase, long machineId, int tenantId)
    {
        return new MachineStateSummary
        {
            MachineId = machineId,
            TenantId = tenantId,
            Name = "m",
            Hostname = "m",
            OperatingSystem = 0,
            MachineType = 0,
            CpuUsagePercent = testCase.CpuUsagePercent,
            MemoryUsagePercent = testCase.MemoryUsagePercent,
            MaxDiskUsagePercent = testCase.MaxDiskUsagePercent,
            FailedServices = testCase.FailedServices,
            HasDiskHealthIssue = testCase.HasDiskHealthIssue,
            HasHardwareIssue = testCase.HasHardwareIssue,
            HealthStatus = UnsweptSentinel,
            LastSeenAt = testCase.LastSeenSecondsAgo is int secondsAgo
                ? DateTimeOffset.UtcNow.AddSeconds(-secondsAgo)
                : null,
        };
    }
}
