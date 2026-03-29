// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Dashboard;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
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
        DashboardHandler handler = new(dbFactory.Context, pingService, configService);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.TotalMachines).IsEqualTo(0);
        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(0);
        await Assert.That(result.Data!.PendingApprovals).IsEqualTo(0);
        await Assert.That(result.Data!.ExpiringCertificates).IsEqualTo(0);
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
        DashboardHandler handler = new(dbFactory.Context, pingService, configService);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.TotalMachines).IsEqualTo(2); // Excludes deleted
        await Assert.That(result.Data!.OnlineMachines).IsEqualTo(1); // Only m1
    }

    [Test]
    public async Task GetSummaryAsync_PendingApprovals_AlwaysZero()
    {
        using TestDatabaseFactory dbFactory = new();

        InMemoryMachinePingService pingService = new();
        ServerConfigurationService configService = new(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());
        DashboardHandler handler = new(dbFactory.Context, pingService, configService);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.PendingApprovals).IsEqualTo(0);
    }

    [Test]
    public async Task GetSummaryAsync_WithExpiringCerts_CountsExpiringOnly()
    {
        using TestDatabaseFactory dbFactory = new();

        // Create a machine to attach certificates to
        Machine m1 = TestDataBuilder.BuildMachine(tenantId: 1);
        m1.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m1);

        // Expiring cert (within 30 days)
        await dbFactory.Context.InsertAsync(new MachineCertificate
        {
            MachineId = m1.Id,
            Thumbprint = "expiring-cert",
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-330),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(15),
        });

        // Already expired cert (should not count)
        await dbFactory.Context.InsertAsync(new MachineCertificate
        {
            MachineId = m1.Id,
            Thumbprint = "expired-cert",
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-400),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-5),
        });

        // Far future cert (should not count)
        await dbFactory.Context.InsertAsync(new MachineCertificate
        {
            MachineId = m1.Id,
            Thumbprint = "future-cert",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(365),
        });

        // Revoked cert (should not count)
        await dbFactory.Context.InsertAsync(new MachineCertificate
        {
            MachineId = m1.Id,
            Thumbprint = "revoked-cert",
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-330),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(15),
            RevokedAt = DateTimeOffset.UtcNow.AddDays(-1),
        });

        InMemoryMachinePingService pingService = new();
        ServerConfigurationService configService = new(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());
        DashboardHandler handler = new(dbFactory.Context, pingService, configService);

        ServiceResult<DashboardSummaryDto> result = await handler.GetSummaryAsync(1, CancellationToken.None);

        await Assert.That(result.Data!.ExpiringCertificates).IsEqualTo(1);
    }
}
