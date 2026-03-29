// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
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

    private static MachineDetailHandler CreateHandler(
        TestDatabaseFactory dbFactory,
        InMemoryMachinePingService? pingService = null,
        IMachineStateService? stateService = null)
    {
        pingService ??= new InMemoryMachinePingService();
        stateService ??= Substitute.For<IMachineStateService>();
        ServerConfigurationService configService = new(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());

        return new MachineDetailHandler(dbFactory.Context, pingService, configService, stateService);
    }

    // ========== Constructor tests ==========

    [Test]
    public async Task Constructor_NullDatabaseContext_ThrowsArgumentNullException()
    {
        InMemoryMachinePingService pingService = new();
        ServerConfigurationService configService = new(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());
        IMachineStateService stateService = Substitute.For<IMachineStateService>();

        await Assert.That(() =>
            new MachineDetailHandler(null!, pingService, configService, stateService))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullPingService_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        ServerConfigurationService configService = new(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());
        IMachineStateService stateService = Substitute.For<IMachineStateService>();

        await Assert.That(() =>
            new MachineDetailHandler(dbFactory.Context, null!, configService, stateService))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullConfigService_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        InMemoryMachinePingService pingService = new();
        IMachineStateService stateService = Substitute.For<IMachineStateService>();

        await Assert.That(() =>
            new MachineDetailHandler(dbFactory.Context, pingService, null!, stateService))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullStateService_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        InMemoryMachinePingService pingService = new();
        ServerConfigurationService configService = new(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());

        await Assert.That(() =>
            new MachineDetailHandler(dbFactory.Context, pingService, configService, null!))
            .Throws<ArgumentNullException>();
    }

    // ========== GetDetailAsync tests ==========

    [Test]
    public async Task GetDetailAsync_NullTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineDto> result = await handler.GetDetailAsync(1, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetDetailAsync_MachineNotFound_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineDto> result = await handler.GetDetailAsync(999, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetDetailAsync_DeletedMachine_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, isDeleted: true);
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineDto> result = await handler.GetDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetDetailAsync_WrongTenant_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, tenantId: 2);
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineDto> result = await handler.GetDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetDetailAsync_ValidMachine_ReturnsMachineDto()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, hostname: "prod-web-01");
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineDto> result = await handler.GetDetailAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
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

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.IsOnline).IsEqualTo(true);
        await Assert.That(result.Data!.LastPing.HasValue).IsEqualTo(true);
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

        await Assert.That(result.IsNotFound).IsEqualTo(true);
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

        await Assert.That(result.IsSuccess).IsEqualTo(true);
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

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    // ========== GetStatusAsync tests ==========

    [Test]
    public async Task GetStatusAsync_NullTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(1, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetStatusAsync_MachineNotFound_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(999, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetStatusAsync_OfflineMachine_ReturnsOffline()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<MachineStatusDto> result = await handler.GetStatusAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.IsOnline).IsEqualTo(false);
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

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.IsOnline).IsEqualTo(true);
    }

    // ========== GetTelemetryAsync tests ==========

    [Test]
    public async Task GetTelemetryAsync_NullTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineTelemetryDto>> result =
            await handler.GetTelemetryAsync(1, null, 1, 25, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetTelemetryAsync_MachineNotFound_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineTelemetryDto>> result =
            await handler.GetTelemetryAsync(999, 1, 1, 25, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetTelemetryAsync_NoTelemetry_ReturnsEmptyPage()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineTelemetryDto>> result =
            await handler.GetTelemetryAsync(machineId, 1, 1, 25, null, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(0);
        await Assert.That(result.Data!.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetTelemetryAsync_WithTelemetry_ReturnsPaginated()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        for (int i = 0; i < 5; i++)
        {
            MachineTelemetry t = TestDataBuilder.BuildMachineTelemetry(machineId: machineId, telemetryType: 1);
            t.ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-i);
            await dbFactory.Context.InsertWithInt64IdentityAsync(t);
        }

        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineTelemetryDto>> result =
            await handler.GetTelemetryAsync(machineId, 1, 1, 2, null, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(5);
        await Assert.That(result.Data!.Items.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetTelemetryAsync_TypeFilter_FiltersResults()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineTelemetry t1 = TestDataBuilder.BuildMachineTelemetry(machineId: machineId, telemetryType: 1);
        MachineTelemetry t2 = TestDataBuilder.BuildMachineTelemetry(machineId: machineId, telemetryType: 2);
        MachineTelemetry t3 = TestDataBuilder.BuildMachineTelemetry(machineId: machineId, telemetryType: 1);
        await dbFactory.Context.InsertWithInt64IdentityAsync(t1);
        await dbFactory.Context.InsertWithInt64IdentityAsync(t2);
        await dbFactory.Context.InsertWithInt64IdentityAsync(t3);

        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineTelemetryDto>> result =
            await handler.GetTelemetryAsync(machineId, 1, 1, 25, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(2);
        await Assert.That(result.Data!.Items.All(d => d.TelemetryType == 1)).IsEqualTo(true);
    }

    [Test]
    public async Task GetTelemetryAsync_ExcludesSoftDeletedTelemetry()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineTelemetry active = TestDataBuilder.BuildMachineTelemetry(machineId: machineId);
        MachineTelemetry deleted = TestDataBuilder.BuildMachineTelemetry(machineId: machineId);
        deleted.DeletedAt = DateTimeOffset.UtcNow;
        await dbFactory.Context.InsertWithInt64IdentityAsync(active);
        await dbFactory.Context.InsertWithInt64IdentityAsync(deleted);

        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineTelemetryDto>> result =
            await handler.GetTelemetryAsync(machineId, 1, 1, 25, null, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(1);
    }

    // ========== GetLatestTelemetryAsync tests ==========

    [Test]
    public async Task GetLatestTelemetryAsync_NullTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<MachineTelemetryDto>> result =
            await handler.GetLatestTelemetryAsync(1, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetLatestTelemetryAsync_MachineNotFound_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<MachineTelemetryDto>> result =
            await handler.GetLatestTelemetryAsync(999, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetLatestTelemetryAsync_NoTelemetry_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<MachineTelemetryDto>> result =
            await handler.GetLatestTelemetryAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Count).IsEqualTo(0);
    }

    // ========== GetCertificatesAsync tests ==========

    [Test]
    public async Task GetCertificatesAsync_NullTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineCertificateDto>> result =
            await handler.GetCertificatesAsync(1, null, 1, 25, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetCertificatesAsync_MachineNotFound_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineCertificateDto>> result =
            await handler.GetCertificatesAsync(999, 1, 1, 25, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetCertificatesAsync_NoCertificates_ReturnsEmptyPage()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineCertificateDto>> result =
            await handler.GetCertificatesAsync(machineId, 1, 1, 25, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(0);
        await Assert.That(result.Data!.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetCertificatesAsync_WithCertificates_ReturnsPaginated()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        for (int i = 0; i < 3; i++)
        {
            MachineCertificate cert = TestDataBuilder.BuildMachineCertificate(machineId: machineId);
            await dbFactory.Context.InsertWithInt64IdentityAsync(cert);
        }

        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineCertificateDto>> result =
            await handler.GetCertificatesAsync(machineId, 1, 1, 2, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(3);
        await Assert.That(result.Data!.Items.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetLatestTelemetryAsync_MultipleTypes_ReturnsLatestPerType()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);

        // Seed 3 type-1 telemetry records with different timestamps
        MachineTelemetry t1a = TestDataBuilder.BuildMachineTelemetry(machineId: machineId, telemetryType: 1);
        t1a.ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        await dbFactory.Context.InsertWithInt64IdentityAsync(t1a);

        MachineTelemetry t1b = TestDataBuilder.BuildMachineTelemetry(machineId: machineId, telemetryType: 1);
        t1b.ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
        await dbFactory.Context.InsertWithInt64IdentityAsync(t1b);

        MachineTelemetry t1c = TestDataBuilder.BuildMachineTelemetry(machineId: machineId, telemetryType: 1);
        t1c.ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        long newestType1Id = await dbFactory.Context.InsertWithInt64IdentityAsync(t1c);

        // Seed 2 type-2 telemetry records with different timestamps
        MachineTelemetry t2a = TestDataBuilder.BuildMachineTelemetry(machineId: machineId, telemetryType: 2);
        t2a.ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-25);
        await dbFactory.Context.InsertWithInt64IdentityAsync(t2a);

        MachineTelemetry t2b = TestDataBuilder.BuildMachineTelemetry(machineId: machineId, telemetryType: 2);
        t2b.ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        long newestType2Id = await dbFactory.Context.InsertWithInt64IdentityAsync(t2b);

        MachineDetailHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<MachineTelemetryDto>> result =
            await handler.GetLatestTelemetryAsync(machineId, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Count).IsEqualTo(2);

        MachineTelemetryDto? type1Result = result.Data!.FirstOrDefault(d => d.TelemetryType == 1);
        MachineTelemetryDto? type2Result = result.Data!.FirstOrDefault(d => d.TelemetryType == 2);
        await Assert.That(type1Result).IsNotNull();
        await Assert.That(type2Result).IsNotNull();
        await Assert.That(type1Result!.Id).IsEqualTo(newestType1Id);
        await Assert.That(type2Result!.Id).IsEqualTo(newestType2Id);
    }
}
