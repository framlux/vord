// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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
        IMachineStateService? stateService = null,
        IQuotaPauseService? quotaPause = null)
    {
        pingService ??= new InMemoryMachinePingService();
        stateService ??= Substitute.For<IMachineStateService>();
        DatabaseRepository repo = CreateRepo(dbFactory);

        return new MachineDetailHandler(repo, repo, pingService, stateService, quotaPause ?? TestQuotaPause.Nothing(), TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineDetailHandler>.Instance);
    }

    private static async Task SeedSummary(TestDatabaseFactory dbFactory, long machineId, short healthStatus)
    {
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(
            machineId: machineId, healthStatus: healthStatus, lastSeenAt: DateTimeOffset.UtcNow));
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
        await SeedSummary(dbFactory, machineId, healthStatus: 0);

        MachineDetailHandler handler = CreateHandler(dbFactory);

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
        await SeedSummary(dbFactory, machineId, healthStatus: 0);
        MachineDetailHandler handler = CreateHandler(dbFactory);

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

    // ========== GetStatusAsync HealthStatus regression tests ==========

    [Test]
    public async Task GetStatusAsync_OfflineMachine_ReturnsHealthStatusOffline()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);

        MachineStateSummary summary = TestDataBuilder.BuildMachineStateSummary(
            machineId: machineId, cpuPercent: 20, memoryPercent: 30, healthStatus: 3);
        await dbFactory.Context.InsertAsync(summary);

        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.IsOnline).IsFalse();
        await Assert.That(result.Data!.HealthStatus).IsEqualTo(MachineHealthStatus.Offline);
    }

    [Test]
    public async Task GetStatusAsync_OnlineHealthyMachine_ReturnsHealthStatusHealthy()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);

        MachineStateSummary summary = TestDataBuilder.BuildMachineStateSummary(
            machineId: machineId, cpuPercent: 20, memoryPercent: 30);
        await dbFactory.Context.InsertAsync(summary);

        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.IsOnline).IsTrue();
        await Assert.That(result.Data!.HealthStatus).IsEqualTo(MachineHealthStatus.Healthy);
    }

    [Test]
    public async Task GetStatusAsync_SweptColumnCritical_ReturnsHealthStatusCritical()
    {
        // Raw CPU is well inside the healthy band while the swept column says Critical. The
        // status endpoint reports the column, so it can never contradict the machine list the
        // user reached this machine from.
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);

        MachineStateSummary summary = TestDataBuilder.BuildMachineStateSummary(
            machineId: machineId, cpuPercent: 10, memoryPercent: 10, healthStatus: 2);
        await dbFactory.Context.InsertAsync(summary);

        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.IsOnline).IsTrue();
        await Assert.That(result.Data!.HealthStatus).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task GetStatusAsync_RawMetricsInWarningBand_StillReportsTheSweptColumn()
    {
        // Memory sits at 85, above the warning threshold the sweep applies, but the column has
        // not been swept to Warning yet. Reporting Warning here would mean this endpoint had
        // re-derived the rule for itself.
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);

        MachineStateSummary summary = TestDataBuilder.BuildMachineStateSummary(
            machineId: machineId, cpuPercent: 20, memoryPercent: 85, healthStatus: 0);
        await dbFactory.Context.InsertAsync(summary);

        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.IsOnline).IsTrue();
        await Assert.That(result.Data!.HealthStatus).IsEqualTo(MachineHealthStatus.Healthy);
    }

    [Test]
    public async Task GetStatusAsync_NoSummary_ReportsOfflineOnBothAxes()
    {
        // A machine with no summary row has never been heard from, and the fleet query the
        // machine list is built from reports exactly that case as Offline. Liveness and health
        // now come from the same row, so the two axes cannot disagree about it.
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);

        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.IsOnline).IsFalse();
        await Assert.That(result.Data!.HealthStatus).IsEqualTo(MachineHealthStatus.Offline);
    }

    // ========== Paused state ==========

    [Test]
    public async Task GetStatusAsync_MachinePastTheCutoff_IsPausedWhateverHealthItFroze()
    {
        using TestDatabaseFactory dbFactory = new();
        long active = await SeedMachine(dbFactory);
        long paused = await SeedMachine(dbFactory);
        await SeedSummary(dbFactory, active, healthStatus: 0);
        await SeedSummary(dbFactory, paused, healthStatus: 2);
        MachineDetailHandler handler = CreateHandler(dbFactory, quotaPause: TestQuotaPause.With(QuotaCutoff.After(active), 1));

        ServiceResult<MachineStatusDto> activeResult = await handler.GetStatusAsync(active, 1, CancellationToken.None);
        ServiceResult<MachineStatusDto> pausedResult = await handler.GetStatusAsync(paused, 1, CancellationToken.None);

        await Assert.That(activeResult.Data!.IsPaused).IsFalse();
        await Assert.That(pausedResult.Data!.IsPaused).IsTrue();

        // The stored health is still reported; the caller shows Paused in place of it.
        await Assert.That(pausedResult.Data!.HealthStatus).IsEqualTo(MachineHealthStatus.Critical);
    }

    [Test]
    public async Task GetStatusAsync_PauseDecisionUnavailable_ReportsNotPausedAndStillSucceeds()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        await SeedSummary(dbFactory, machineId, healthStatus: 0);
        MachineDetailHandler handler = CreateHandler(dbFactory, quotaPause: TestQuotaPause.Unavailable());

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.IsPaused).IsFalse();
    }

    [Test]
    public async Task GetDetailAsync_MachinePastTheCutoff_IsPaused()
    {
        using TestDatabaseFactory dbFactory = new();
        long active = await SeedMachine(dbFactory);
        long paused = await SeedMachine(dbFactory);
        MachineDetailHandler handler = CreateHandler(dbFactory, quotaPause: TestQuotaPause.With(QuotaCutoff.After(active), 1));

        ServiceResult<MachineDto> activeResult = await handler.GetDetailAsync(active, 1, CancellationToken.None);
        ServiceResult<MachineDto> pausedResult = await handler.GetDetailAsync(paused, 1, CancellationToken.None);

        await Assert.That(activeResult.Data!.IsPaused).IsFalse();
        await Assert.That(pausedResult.Data!.IsPaused).IsTrue();
    }

    [Test]
    public async Task GetDetailAsync_EveryMachinePaused_IsPaused()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineDetailHandler handler = CreateHandler(dbFactory, quotaPause: TestQuotaPause.With(QuotaCutoff.All, 1));

        ServiceResult<MachineDto> result = await handler.GetDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.Data!.IsPaused).IsTrue();
    }

    [Test]
    public async Task Constructor_NullQuotaPause_Throws()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = CreateRepo(dbFactory);

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            _ = new MachineDetailHandler(repo, repo, new InMemoryMachinePingService(), Substitute.For<IMachineStateService>(), null!, TestMetricsFactory.CreateQuotaPauseMetrics(), NullLogger<MachineDetailHandler>.Instance);

            return Task.CompletedTask;
        });

        await Assert.That(ex!.ParamName).IsEqualTo("quotaPause");
    }

    [Test]
    public async Task Constructor_NullQuotaPauseMetrics_Throws()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = CreateRepo(dbFactory);

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            _ = new MachineDetailHandler(repo, repo, new InMemoryMachinePingService(), Substitute.For<IMachineStateService>(), TestQuotaPause.Nothing(), null!, NullLogger<MachineDetailHandler>.Instance);

            return Task.CompletedTask;
        });

        await Assert.That(ex!.ParamName).IsEqualTo("quotaPauseMetrics");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = CreateRepo(dbFactory);

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            _ = new MachineDetailHandler(repo, repo, new InMemoryMachinePingService(), Substitute.For<IMachineStateService>(), TestQuotaPause.Nothing(), TestMetricsFactory.CreateQuotaPauseMetrics(), null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }
}
