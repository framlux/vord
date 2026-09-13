// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Dashboard;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="DashboardHandler"/>.
/// </summary>
public class DashboardHandlerTests
{
    [Test]
    public async Task GetSummaryAsync_EmptyFleet_ReturnsAllZeros()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = CreateRepo(dbFactory);
        DashboardHandler handler = new(repo, repo);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.TotalMachines).IsEqualTo(0);
        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(0);
        await Assert.That(result.Data!.PendingApprovals).IsEqualTo(0);
    }

    [Test]
    public async Task GetSummaryAsync_WithMachines_CountsOnlineFromTheSweptHealthStatus()
    {
        using TestDatabaseFactory dbFactory = new();

        // Create 2 active machines and 1 deleted
        Machine m1 = TestDataBuilder.BuildMachine(tenantId: 1);
        m1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m1);

        Machine m2 = TestDataBuilder.BuildMachine(tenantId: 1);
        m2.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m2);

        Machine m3 = TestDataBuilder.BuildMachine(tenantId: 1);
        m3.IsDeleted = true;
        m3.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m3);

        // Only m1 is online: the sweep left it Healthy while m2 was declared Offline.
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
            machineId: m1.Id, tenantId: 1, healthStatus: 0));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
            machineId: m2.Id, tenantId: 1, healthStatus: 3));

        DatabaseRepository repo = CreateRepo(dbFactory);
        DashboardHandler handler = new(repo, repo);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.TotalMachines).IsEqualTo(2); // Excludes deleted
        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(1); // Only m1
    }

    [Test]
    public async Task GetSummaryAsync_WarningAndCriticalMachines_CountAsOnline()
    {
        // Offline is the only status that means "we have not heard from it"; a degraded machine
        // is still online, and the count must not treat unhealthy as unreachable.
        using TestDatabaseFactory dbFactory = new();

        Machine warning = TestDataBuilder.BuildMachine(tenantId: 1);
        warning.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(warning);

        Machine critical = TestDataBuilder.BuildMachine(tenantId: 1);
        critical.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(critical);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
            machineId: warning.Id, tenantId: 1, healthStatus: 1));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
            machineId: critical.Id, tenantId: 1, healthStatus: 2));

        DatabaseRepository repo = CreateRepo(dbFactory);
        DashboardHandler handler = new(repo, repo);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(2);
    }

    [Test]
    public async Task GetSummaryAsync_MachineWithNoSummaryRow_CountsAsOffline()
    {
        // A machine that has never been heard from has no summary row, and every read path
        // treats that absence as offline.
        using TestDatabaseFactory dbFactory = new();

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        DatabaseRepository repo = CreateRepo(dbFactory);
        DashboardHandler handler = new(repo, repo);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.TotalMachines).IsEqualTo(1);
        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(0);
    }

    [Test]
    public async Task GetSummaryAsync_PendingApprovals_ReturnsZero_FeatureNotYetImplemented()
    {
        // Pending approvals are not yet implemented in the dashboard summary.
        // This test documents that the field always returns zero until the approval
        // workflow feature is built.
        using TestDatabaseFactory dbFactory = new();

        Machine m1 = TestDataBuilder.BuildMachine(tenantId: 1);
        m1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m1);

        DatabaseRepository repo = CreateRepo(dbFactory);
        DashboardHandler handler = new(repo, repo);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.PendingApprovals).IsEqualTo(0);
    }

    // ========== Helper methods ==========

    private static DatabaseRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }
}
