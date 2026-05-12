// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="MachineDetailHandler"/>.
/// </summary>
public class MachineDetailHandlerTests
{
    private static async Task<long> SeedMachine(TestDatabaseFactory dbFactory, int tenantId = 1, bool isDeleted = false, string hostname = "host-test")
    {
        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: hostname);
        machine.IsDeleted = isDeleted;
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        return machine.Id;
    }

    private static DatabaseRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }

    private static MachineDetailHandler CreateHandler(
        TestDatabaseFactory dbFactory,
        InMemoryMachinePingService? pingService = null,
        IMachineStateService? stateService = null)
    {
        pingService ??= new InMemoryMachinePingService();
        stateService ??= Substitute.For<IMachineStateService>();
        ServerConfigurationService configService = new(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());
        DatabaseRepository repo = CreateRepo(dbFactory);

        return new MachineDetailHandler(repo, repo, pingService, configService, stateService);
    }

    // ========== GetDetailAsync tests ==========

    [Test]
    public async Task GetDetailAsync_NullTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineDto> result = await handler.GetDetailAsync(1, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetDetailAsync_MachineNotFound_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineDto> result = await handler.GetDetailAsync(999, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetDetailAsync_DeletedMachine_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, isDeleted: true);
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineDto> result = await handler.GetDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetDetailAsync_WrongTenant_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, tenantId: 2);
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineDto> result = await handler.GetDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetDetailAsync_ValidMachine_ReturnsMachineDto()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, hostname: "prod-web-01");
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineDto> result = await handler.GetDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Id).IsEqualTo(machineId);
        await Assert.That(result.Data!.Name).IsEqualTo("prod-web-01");
    }

    [Test]
    public async Task GetDetailAsync_OnlineMachine_ReportsOnline()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        InMemoryMachinePingService pingService = new();
        await pingService.RecordPingAsync(machineId);

        MachineDetailHandler handler = CreateHandler(dbFactory, pingService: pingService);

        ServiceResult<MachineDto> result = await handler.GetDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.IsOnline).IsTrue();
        await Assert.That(result.Data!.LastPing.HasValue).IsTrue();
    }

    // ========== GetFullDetailAsync tests ==========

    [Test]
    public async Task GetFullDetailAsync_MachineNotFound_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateService stateService = Substitute.For<IMachineStateService>();
        stateService.GetMachineDetailAsync(999, 1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MachineDetailDto?>(null));
        MachineDetailHandler handler = CreateHandler(dbFactory, stateService: stateService);

        ServiceResult<MachineDetailDto> result = await handler.GetFullDetailAsync(999, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetFullDetailAsync_ValidMachine_ReturnsMachineDetailDto()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailDto detail = new() { Id = 1, Name = "detail-machine" };
        IMachineStateService stateService = Substitute.For<IMachineStateService>();
        stateService.GetMachineDetailAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MachineDetailDto?>(detail));
        MachineDetailHandler handler = CreateHandler(dbFactory, stateService: stateService);

        ServiceResult<MachineDetailDto> result = await handler.GetFullDetailAsync(1, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Name).IsEqualTo("detail-machine");
    }

    [Test]
    public async Task GetFullDetailAsync_NullTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateService stateService = Substitute.For<IMachineStateService>();
        stateService.GetMachineDetailAsync(1, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MachineDetailDto?>(null));
        MachineDetailHandler handler = CreateHandler(dbFactory, stateService: stateService);

        ServiceResult<MachineDetailDto> result = await handler.GetFullDetailAsync(1, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    // ========== GetStatusAsync tests ==========

    [Test]
    public async Task GetStatusAsync_NullTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(1, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetStatusAsync_MachineNotFound_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(999, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetStatusAsync_OfflineMachine_ReturnsOffline()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.IsOnline).IsFalse();
    }

    [Test]
    public async Task GetStatusAsync_OnlineMachine_ReportsOnline()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        InMemoryMachinePingService pingService = new();
        await pingService.RecordPingAsync(machineId);
        MachineDetailHandler handler = CreateHandler(dbFactory, pingService: pingService);

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.IsOnline).IsTrue();
    }

    // ========== CommandsEnabled from agent capabilities ==========

    [Test]
    public async Task GetDetailAsync_NoCapabilities_CommandsEnabledIsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineDto> result = await handler.GetDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.CommandsEnabled).IsFalse();
    }

    [Test]
    public async Task GetDetailAsync_RemoteCommandsCapabilitySet_CommandsEnabledIsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        InMemoryMachinePingService pingService = new();
        await pingService.SetAgentCapabilitiesAsync(machineId, 1UL);

        MachineDetailHandler handler = CreateHandler(dbFactory, pingService: pingService);

        ServiceResult<MachineDto> result = await handler.GetDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.CommandsEnabled).IsTrue();
    }

    [Test]
    public async Task GetStatusAsync_NoCapabilities_CommandsEnabledIsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.CommandsEnabled).IsFalse();
    }

    [Test]
    public async Task GetStatusAsync_RemoteCommandsCapabilitySet_CommandsEnabledIsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        InMemoryMachinePingService pingService = new();
        await pingService.SetAgentCapabilitiesAsync(machineId, 1UL);

        MachineDetailHandler handler = CreateHandler(dbFactory, pingService: pingService);

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.CommandsEnabled).IsTrue();
    }
}
