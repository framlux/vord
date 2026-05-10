// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="MachineHandler.UpdateAsync"/>.
/// </summary>
public class MachineHandlerUpdateTests
{
    private static DatabaseRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }

    private static MachineHandler CreateHandler(TestDatabaseFactory dbFactory, InMemoryMachinePingService? pingService = null)
    {
        InMemoryMachinePingService ping = pingService ?? new InMemoryMachinePingService();
        ServerConfigurationService configService = new(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());
        DatabaseRepository repo = CreateRepo(dbFactory);

        return new MachineHandler(repo, repo, repo, repo, repo, repo, ping, configService, Substitute.For<IBillingApiClient>(), Substitute.For<ISubscriptionService>(), NullLogger<MachineHandler>.Instance);
    }

    private static async Task<long> SeedMachine(
        TestDatabaseFactory dbFactory,
        int tenantId = 1,
        bool isDeleted = false,
        string hostname = "host-test",
        MachineTypes machineType = MachineTypes.BareMetalServer,
        OperatingSystems operatingSystem = OperatingSystems.Ubuntu)
    {
        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: hostname);
        machine.IsDeleted = isDeleted;
        machine.MachineType = machineType;
        machine.OperatingSystem = operatingSystem;
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        return machine.Id;
    }

    private static async Task<long> SeedMachineWithSummary(
        TestDatabaseFactory dbFactory,
        int tenantId = 1,
        string hostname = "host-test",
        string? telemetryHostname = "telemetry-host")
    {
        long machineId = await SeedMachine(dbFactory, tenantId: tenantId, hostname: hostname);

        MachineStateSummary summary = TestDataBuilder.BuildMachineStateSummary(
            machineId: machineId,
            tenantId: tenantId,
            name: hostname);
        summary.Hostname = telemetryHostname;
        await dbFactory.Context.InsertAsync(summary);

        return machineId;
    }

    // ========== Happy-path tests ==========

    [Test]
    public async Task UpdateAsync_ValidInput_ReturnsOkWithUpdatedDto()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachineWithSummary(dbFactory, hostname: "original-name", telemetryHostname: "telem-host");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<ApiResponse<MachineDto>> result = await handler.UpdateAsync(
            machineId, 1, 1, "updated-name", "new description", "new location", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.StatusCode).IsEqualTo(200);
        await Assert.That(result.Data).IsNotNull();

        MachineDto? dto = result.Data!.Data;
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.Id).IsEqualTo(machineId);
        await Assert.That(dto.Name).IsEqualTo("updated-name");
        await Assert.That(dto.Description).IsEqualTo("new description");
        await Assert.That(dto.Location).IsEqualTo("new location");
        await Assert.That(dto.Hostname).IsEqualTo("telem-host");
    }

    [Test]
    public async Task UpdateAsync_SyncsMachineStateSummaryName()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachineWithSummary(dbFactory, hostname: "original-name");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<ApiResponse<MachineDto>> result = await handler.UpdateAsync(
            machineId, 1, 1, "synced-name", null, null, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();

        MachineStateSummary? summary = await dbFactory.Context.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.Name).IsEqualTo("synced-name");
    }

    // ========== Validation branch tests ==========

    [Test]
    public async Task UpdateAsync_NullTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<ApiResponse<MachineDto>> result = await handler.UpdateAsync(
            machineId, null, 1, "some-name", null, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
        await Assert.That(result.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task UpdateAsync_EmptyName_ReturnsBadRequest()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<ApiResponse<MachineDto>> result = await handler.UpdateAsync(
            machineId, 1, 1, "", null, null, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).IsNotNull();
        await Assert.That(string.IsNullOrEmpty(result.ErrorMessage)).IsFalse();
    }

    [Test]
    public async Task UpdateAsync_WhitespaceOnlyName_ReturnsBadRequest()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<ApiResponse<MachineDto>> result = await handler.UpdateAsync(
            machineId, 1, 1, "   ", null, null, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).IsNotNull();
        await Assert.That(string.IsNullOrEmpty(result.ErrorMessage)).IsFalse();
    }

    [Test]
    public async Task UpdateAsync_NameExceeds250_ReturnsBadRequest()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineHandler handler = CreateHandler(dbFactory);

        string longName = new string('a', 251);
        ServiceResult<ApiResponse<MachineDto>> result = await handler.UpdateAsync(
            machineId, 1, 1, longName, null, null, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).IsNotNull();
        await Assert.That(string.IsNullOrEmpty(result.ErrorMessage)).IsFalse();
    }

    [Test]
    public async Task UpdateAsync_LocationExceeds250_ReturnsBadRequest()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineHandler handler = CreateHandler(dbFactory);

        string longLocation = new string('b', 251);
        ServiceResult<ApiResponse<MachineDto>> result = await handler.UpdateAsync(
            machineId, 1, 1, "valid-name", null, longLocation, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).IsNotNull();
        await Assert.That(string.IsNullOrEmpty(result.ErrorMessage)).IsFalse();
    }

    [Test]
    public async Task UpdateAsync_MachineNotFound_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<ApiResponse<MachineDto>> result = await handler.UpdateAsync(
            999, 1, 1, "some-name", null, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
        await Assert.That(result.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task UpdateAsync_DeletedMachine_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, isDeleted: true);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<ApiResponse<MachineDto>> result = await handler.UpdateAsync(
            machineId, 1, 1, "some-name", null, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
        await Assert.That(result.StatusCode).IsEqualTo(404);
    }

    // ========== Edge case tests ==========

    [Test]
    public async Task UpdateAsync_NullDescription_ClearsField()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, hostname: "desc-machine");

        // Set an initial description directly in the database
        await dbFactory.Context.Machines
            .Where(m => m.Id == machineId)
            .Set(m => m.Description, "initial description")
            .UpdateAsync();

        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<ApiResponse<MachineDto>> result = await handler.UpdateAsync(
            machineId, 1, 1, "desc-machine", null, null, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();

        Machine? updated = await dbFactory.Context.Machines
            .FirstOrDefaultAsync(m => m.Id == machineId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Description).IsNull();
    }

    [Test]
    public async Task UpdateAsync_NullLocation_ClearsField()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, hostname: "loc-machine");

        // Set an initial location directly in the database
        await dbFactory.Context.Machines
            .Where(m => m.Id == machineId)
            .Set(m => m.Location, "initial location")
            .UpdateAsync();

        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<ApiResponse<MachineDto>> result = await handler.UpdateAsync(
            machineId, 1, 1, "loc-machine", null, null, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();

        Machine? updated = await dbFactory.Context.Machines
            .FirstOrDefaultAsync(m => m.Id == machineId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Location).IsNull();
    }

    [Test]
    public async Task UpdateAsync_NameWithLeadingTrailingSpaces_IsTrimmed()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachineWithSummary(dbFactory, hostname: "original");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<ApiResponse<MachineDto>> result = await handler.UpdateAsync(
            machineId, 1, 1, " hello ", null, null, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data).IsNotNull();
        await Assert.That(result.Data!.Data).IsNotNull();
        await Assert.That(result.Data!.Data!.Name).IsEqualTo("hello");

        // Verify the trimmed value was persisted to the database
        Machine? updated = await dbFactory.Context.Machines
            .FirstOrDefaultAsync(m => m.Id == machineId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Name).IsEqualTo("hello");
    }
}
