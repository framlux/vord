// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
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
/// Tests for <see cref="MachineHandler"/>.
/// </summary>
public class MachineHandlerTests
{
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

    private static MachineHandler CreateHandler(TestDatabaseFactory dbFactory, InMemoryMachinePingService? pingService = null)
    {
        InMemoryMachinePingService ping = pingService ?? new InMemoryMachinePingService();
        ServerConfigurationService configService = new(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());

        return new MachineHandler(dbFactory.Context, ping, configService, Substitute.For<IBillingApiClient>(), NullLogger<MachineHandler>.Instance);
    }

    // ========== DeleteAsync tests ==========

    [Test]
    public async Task DeleteAsync_MachineNotFound_Returns404()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<ApiResponse<object>> result = await handler.DeleteAsync(999, 1, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task DeleteAsync_AlreadyDeleted_Returns404()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, isDeleted: true);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<ApiResponse<object>> result = await handler.DeleteAsync(machineId, 1, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task DeleteAsync_Success_SoftDeletes()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<ApiResponse<object>> result = await handler.DeleteAsync(machineId, 1, 5, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();

        Machine? deleted = await dbFactory.Context.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
        await Assert.That(deleted).IsNotNull();
        await Assert.That(deleted!.IsDeleted).IsTrue();
        await Assert.That(deleted.DeletedByUserId).IsEqualTo(5);
        await Assert.That(deleted.DeletedOn.HasValue).IsTrue();
    }

    // ========== ListAsync tests ==========

    [Test]
    public async Task ListAsync_NoMachines_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(0);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(0);
        await Assert.That(result.Data!.Page).IsEqualTo(1);
        await Assert.That(result.Data!.PageSize).IsEqualTo(25);
        await Assert.That(result.Data!.HasNextPage).IsFalse();
        await Assert.That(result.Data!.HasPreviousPage).IsFalse();
    }

    [Test]
    public async Task ListAsync_WithMachines_ExcludesDeleted()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "active-host");
        await SeedMachine(dbFactory, isDeleted: true, hostname: "deleted-host");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Data!.TotalCount).IsEqualTo(1);
        await Assert.That(result.Data!.Items.Count).IsEqualTo(1);
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("active-host");
        await Assert.That(result.Data!.Items[0].IsDeleted).IsFalse();
    }

    [Test]
    public async Task ListAsync_Pagination_ReturnsCorrectPage()
    {
        using TestDatabaseFactory dbFactory = new();
        for (int i = 0; i < 5; i++)
        {
            await SeedMachine(dbFactory, hostname: $"machine-{i:D2}");
        }
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 2, 1, null, null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Data!.TotalCount).IsEqualTo(5);
        await Assert.That(result.Data!.Items.Count).IsEqualTo(2);
        await Assert.That(result.Data!.Page).IsEqualTo(1);
        await Assert.That(result.Data!.PageSize).IsEqualTo(2);
        await Assert.That(result.Data!.TotalPages).IsEqualTo(3);
        await Assert.That(result.Data!.HasNextPage).IsTrue();
        await Assert.That(result.Data!.HasPreviousPage).IsFalse();
        // Verify first page returns first two items sorted by name ascending
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("machine-00");
        await Assert.That(result.Data!.Items[1].Name).IsEqualTo("machine-01");
    }

    [Test]
    public async Task ListAsync_Pagination_LastPageReturnsRemainingItems()
    {
        using TestDatabaseFactory dbFactory = new();
        for (int i = 0; i < 5; i++)
        {
            await SeedMachine(dbFactory, hostname: $"machine-{i:D2}");
        }
        MachineHandler handler = CreateHandler(dbFactory);

        // Request the last page (page 3 of 3 with page size 2) to verify off-by-one boundary
        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(3, 2, 1, null, null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Data!.TotalCount).IsEqualTo(5);
        await Assert.That(result.Data!.Items.Count).IsEqualTo(1);
        await Assert.That(result.Data!.Page).IsEqualTo(3);
        await Assert.That(result.Data!.TotalPages).IsEqualTo(3);
        await Assert.That(result.Data!.HasNextPage).IsFalse();
        await Assert.That(result.Data!.HasPreviousPage).IsTrue();
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("machine-04");
    }

    [Test]
    public async Task ListAsync_Pagination_BeyondAvailableResults_ReturnsEmptyItems()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "only-machine");
        await SeedMachine(dbFactory, hostname: "second-machine");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(100, 25, 1, null, null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.TotalCount).IsEqualTo(2);
        await Assert.That(result.Data!.Items.Count).IsEqualTo(0);
        await Assert.That(result.Data!.Page).IsEqualTo(100);
        await Assert.That(result.Data!.PageSize).IsEqualTo(25);
    }

    [Test]
    public async Task ListAsync_SearchFilter_FiltersResults()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "webserver-prod");
        await SeedMachine(dbFactory, hostname: "database-prod");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, "webserver", null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(1);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(1);
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("webserver-prod");
    }

    [Test]
    public async Task ListAsync_SearchFilter_CaseInsensitive()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "WebServer-Prod");
        await SeedMachine(dbFactory, hostname: "database-prod");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, "webserver", null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(1);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(1);
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("WebServer-Prod");
    }

    [Test]
    public async Task ListAsync_SearchFilter_NoResults_ReturnsEmptyWithCorrectMetadata()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "webserver-prod");
        await SeedMachine(dbFactory, hostname: "database-prod");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, "nonexistent", null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(0);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(0);
        await Assert.That(result.Data!.Page).IsEqualTo(1);
        await Assert.That(result.Data!.PageSize).IsEqualTo(25);
    }

    [Test]
    public async Task ListAsync_SearchFilter_UnderscoreTreatedAsLiteral_MatchesOnlyUnderscoreNames()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "server_01");
        await SeedMachine(dbFactory, hostname: "server-01");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, "_01", null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(1);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(1);
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("server_01");
    }

    [Test]
    public async Task ListAsync_SearchFilter_MatchesMultipleResults()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "web-prod-01");
        await SeedMachine(dbFactory, hostname: "web-prod-02");
        await SeedMachine(dbFactory, hostname: "db-prod-01");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, "web", null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(2);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(2);
        // Verify all returned items contain the search term
        await Assert.That(result.Data!.Items.All(m => m.Name.Contains("web", StringComparison.OrdinalIgnoreCase))).IsTrue();
        // Verify sorted order
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("web-prod-01");
        await Assert.That(result.Data!.Items[1].Name).IsEqualTo("web-prod-02");
    }

    [Test]
    public async Task ListAsync_SortByName_Ascending()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "charlie");
        await SeedMachine(dbFactory, hostname: "alpha");
        await SeedMachine(dbFactory, hostname: "bravo");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Data!.Items.Count).IsEqualTo(3);
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("alpha");
        await Assert.That(result.Data!.Items[1].Name).IsEqualTo("bravo");
        await Assert.That(result.Data!.Items[2].Name).IsEqualTo("charlie");
    }

    [Test]
    public async Task ListAsync_SortByName_Descending()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "charlie");
        await SeedMachine(dbFactory, hostname: "alpha");
        await SeedMachine(dbFactory, hostname: "bravo");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, null, null, null, "name", "desc", CancellationToken.None);

        await Assert.That(result.Data!.Items.Count).IsEqualTo(3);
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("charlie");
        await Assert.That(result.Data!.Items[1].Name).IsEqualTo("bravo");
        await Assert.That(result.Data!.Items[2].Name).IsEqualTo("alpha");
    }

    [Test]
    public async Task ListAsync_SortByType_Ascending()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "vm-host", machineType: MachineTypes.VirtualMachine);
        await SeedMachine(dbFactory, hostname: "desktop-host", machineType: MachineTypes.Desktop);
        await SeedMachine(dbFactory, hostname: "baremetal-host", machineType: MachineTypes.BareMetalServer);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, null, null, null, "type", "asc", CancellationToken.None);

        await Assert.That(result.Data!.Items.Count).IsEqualTo(3);
        // MachineTypes enum: Desktop=1, BareMetalServer=3, VirtualMachine=4
        await Assert.That(result.Data!.Items[0].MachineType).IsEqualTo(MachineTypes.Desktop);
        await Assert.That(result.Data!.Items[1].MachineType).IsEqualTo(MachineTypes.BareMetalServer);
        await Assert.That(result.Data!.Items[2].MachineType).IsEqualTo(MachineTypes.VirtualMachine);
    }

    [Test]
    public async Task ListAsync_SortByRegisteredOn_Descending()
    {
        using TestDatabaseFactory dbFactory = new();
        // Seed machines with different registration times so sort order is deterministic
        long firstId = await SeedMachine(dbFactory, hostname: "old-machine");
        long secondId = await SeedMachine(dbFactory, hostname: "new-machine");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, null, null, null, "registeredon", "desc", CancellationToken.None);

        await Assert.That(result.Data!.Items.Count).IsEqualTo(2);
        // The second seeded machine has a later RegisteredOn, so it should appear first in descending order
        await Assert.That(result.Data!.Items[0].Id).IsEqualTo(secondId);
        await Assert.That(result.Data!.Items[1].Id).IsEqualTo(firstId);
    }

    [Test]
    public async Task ListAsync_InvalidPageSize_DefaultsTo25()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, -5, 1, null, null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Data!.PageSize).IsEqualTo(25);
    }

    [Test]
    public async Task ListAsync_PageSizeAboveMax_DefaultsTo25()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 101, 1, null, null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Data!.PageSize).IsEqualTo(25);
    }

    [Test]
    public async Task ListAsync_OnlineStatusFilter_ReturnsOnlyOnlineMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        long onlineMachineId = await SeedMachine(dbFactory, hostname: "online-host");
        await SeedMachine(dbFactory, hostname: "offline-host");

        InMemoryMachinePingService pingService = new();
        await pingService.RecordPingAsync(onlineMachineId);

        MachineHandler handler = CreateHandler(dbFactory, pingService);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, null, null, "online", "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(1);
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("online-host");
        await Assert.That(result.Data!.Items[0].IsOnline).IsTrue();
        await Assert.That(result.Data!.TotalCount).IsEqualTo(1);
    }

    [Test]
    public async Task ListAsync_OfflineStatusFilter_ReturnsOnlyOfflineMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        long onlineMachineId = await SeedMachine(dbFactory, hostname: "online-host");
        await SeedMachine(dbFactory, hostname: "offline-host");

        InMemoryMachinePingService pingService = new();
        await pingService.RecordPingAsync(onlineMachineId);

        MachineHandler handler = CreateHandler(dbFactory, pingService);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, null, null, "offline", "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(1);
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("offline-host");
        await Assert.That(result.Data!.Items[0].IsOnline).IsFalse();
        await Assert.That(result.Data!.TotalCount).IsEqualTo(1);
    }

    [Test]
    public async Task ListAsync_ReturnsCorrectMachineDtoFields()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, hostname: "details-host", machineType: MachineTypes.VirtualMachine, operatingSystem: OperatingSystems.Fedora);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.Data!.Items.Count).IsEqualTo(1);
        MachineDto dto = result.Data!.Items[0];
        await Assert.That(dto.Id).IsEqualTo(machineId);
        await Assert.That(dto.Name).IsEqualTo("details-host");
        await Assert.That(dto.MachineType).IsEqualTo(MachineTypes.VirtualMachine);
        await Assert.That(dto.OperatingSystem).IsEqualTo(OperatingSystems.Fedora);
        await Assert.That(dto.IsDeleted).IsFalse();
        await Assert.That(dto.IsOnline).IsFalse();
    }

    [Test]
    public async Task ListAsync_SearchAndOsFilter_CombinesFilters()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "web-ubuntu", operatingSystem: OperatingSystems.Ubuntu);
        await SeedMachine(dbFactory, hostname: "web-fedora", operatingSystem: OperatingSystems.Fedora);
        await SeedMachine(dbFactory, hostname: "db-ubuntu", operatingSystem: OperatingSystems.Ubuntu);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, "web", "Ubuntu", null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(1);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(1);
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("web-ubuntu");
        await Assert.That(result.Data!.Items[0].OperatingSystem).IsEqualTo(OperatingSystems.Ubuntu);
    }

    [Test]
    public async Task ListAsync_TypeFilter_FiltersCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "vm-host", machineType: MachineTypes.VirtualMachine);
        await SeedMachine(dbFactory, hostname: "baremetal-host", machineType: MachineTypes.BareMetalServer);
        await SeedMachine(dbFactory, hostname: "desktop-host", machineType: MachineTypes.Desktop);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, null, "VirtualMachine", null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(1);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(1);
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("vm-host");
        await Assert.That(result.Data!.Items[0].MachineType).IsEqualTo(MachineTypes.VirtualMachine);
    }

    [Test]
    public async Task ListAsync_OsFilter_FiltersCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "fedora-host", operatingSystem: OperatingSystems.Fedora);
        await SeedMachine(dbFactory, hostname: "ubuntu-host", operatingSystem: OperatingSystems.Ubuntu);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, "Fedora", null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(1);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(1);
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("fedora-host");
        await Assert.That(result.Data!.Items[0].OperatingSystem).IsEqualTo(OperatingSystems.Fedora);
    }

    [Test]
    public async Task ListAsync_SearchAndTypeFilter_CombinesFilters()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "web-vm", machineType: MachineTypes.VirtualMachine);
        await SeedMachine(dbFactory, hostname: "web-baremetal", machineType: MachineTypes.BareMetalServer);
        await SeedMachine(dbFactory, hostname: "db-vm", machineType: MachineTypes.VirtualMachine);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, "web", null, "VirtualMachine", null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(1);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(1);
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("web-vm");
    }

    [Test]
    public async Task ListAsync_InvalidOsFilter_IgnoredGracefully()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "machine-a");
        await SeedMachine(dbFactory, hostname: "machine-b");
        MachineHandler handler = CreateHandler(dbFactory);

        // An invalid OS filter string that does not parse to the enum should be ignored
        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, "InvalidOS", null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(2);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(2);
    }

    [Test]
    public async Task ListAsync_InvalidTypeFilter_IgnoredGracefully()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "machine-a");
        await SeedMachine(dbFactory, hostname: "machine-b");
        MachineHandler handler = CreateHandler(dbFactory);

        // An invalid type filter string that does not parse to the enum should be ignored
        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, null, "InvalidType", null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(2);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(2);
    }

    // ========== Cross-Tenant Isolation tests ==========

    [Test]
    public async Task DeleteAsync_WrongTenant_Returns404()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory, tenantId: 2);
        MachineHandler handler = CreateHandler(dbFactory);

        // Attempt to delete tenant 2's machine using tenant 1's context
        ServiceResult<ApiResponse<object>> result = await handler.DeleteAsync(machineId, 1, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();

        // Verify machine was not deleted
        Machine? machine = await dbFactory.Context.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
        await Assert.That(machine).IsNotNull();
        await Assert.That(machine!.IsDeleted).IsFalse();
    }

    [Test]
    public async Task DeleteAsync_NullTenant_Returns404()
    {
        using TestDatabaseFactory dbFactory = new();
        long machineId = await SeedMachine(dbFactory);
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<ApiResponse<object>> result = await handler.DeleteAsync(machineId, null, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task ListAsync_WrongTenant_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 2, hostname: "tenant2-host");
        MachineHandler handler = CreateHandler(dbFactory);

        // List machines for tenant 1 should not see tenant 2's machines
        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.TotalCount).IsEqualTo(0);
        await Assert.That(result.Data!.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ListAsync_CorrectTenant_OnlyReturnsTenantMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, tenantId: 1, hostname: "tenant1-host");
        await SeedMachine(dbFactory, tenantId: 2, hostname: "tenant2-host");
        await SeedMachine(dbFactory, tenantId: 1, hostname: "tenant1-host2");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, 1, null, null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.TotalCount).IsEqualTo(2);
        await Assert.That(result.Data!.Items.Count).IsEqualTo(2);
        // Verify both returned items belong to tenant 1 by checking expected names
        await Assert.That(result.Data!.Items[0].Name).IsEqualTo("tenant1-host");
        await Assert.That(result.Data!.Items[1].Name).IsEqualTo("tenant1-host2");
    }

    [Test]
    public async Task ListAsync_NullTenant_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedMachine(dbFactory, hostname: "some-host");
        MachineHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<MachineDto>> result = await handler.ListAsync(1, 25, null, null, null, null, null, "name", "asc", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.TotalCount).IsEqualTo(0);
        await Assert.That(result.Data!.Items.Count).IsEqualTo(0);
        await Assert.That(result.Data!.Page).IsEqualTo(1);
    }
}
