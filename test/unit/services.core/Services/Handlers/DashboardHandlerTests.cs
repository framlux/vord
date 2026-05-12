// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Models.Dashboard;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

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
        InMemoryMachinePingService pingService = new();
        ServerConfigurationService configService = new(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());
        DashboardHandler handler = new(CreateRepo(dbFactory), pingService, configService);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.TotalMachines).IsEqualTo(0);
        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(0);
        await Assert.That(result.Data!.PendingApprovals).IsEqualTo(0);
    }

    [Test]
    public async Task GetSummaryAsync_WithMachines_CorrectTotalAndOnlineCount()
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

        InMemoryMachinePingService pingService = new();
        await pingService.RecordPingAsync(m1.Id); // Only m1 is online

        ServerConfigurationService configService = new(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());
        DashboardHandler handler = new(CreateRepo(dbFactory), pingService, configService);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.TotalMachines).IsEqualTo(2); // Excludes deleted
        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(1); // Only m1
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

        InMemoryMachinePingService pingService = new();
        ServerConfigurationService configService = new(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());
        DashboardHandler handler = new(CreateRepo(dbFactory), pingService, configService);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.PendingApprovals).IsEqualTo(0);
    }

    // ========== Helper methods ==========

    private static DatabaseRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }
}
