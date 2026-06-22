// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for machine-state-related methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class MachineStateRepositoryTests
{
    /// <summary>
    /// Seeds a user, tenant, machine, and initial summary/detail rows for update-oriented tests.
    /// </summary>
    private static async Task<(int userId, int tenantId, long machineId)> SeedMachineWithStateAsync(TestDatabaseFactory dbFactory)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);
        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        // Insert summary and detail rows (these are required for update tests)
        MachineStateSummary summary = TestDataBuilder.BuildMachineStateSummary(machineId: machineId, tenantId: tenantId);
        await dbFactory.Context.InsertAsync(summary);
        MachineStateDetail detail = new() { MachineId = machineId };
        await dbFactory.Context.InsertAsync(detail);

        return (userId, tenantId, machineId);
    }

    // ========== InsertSummaryAsync tests ==========

    [Test]
    public async Task InsertSummaryAsync_ValidSummary_InsertsAndIsRetrievable()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        MachineStateSummary summary = TestDataBuilder.BuildMachineStateSummary(machineId: 100, tenantId: 1, name: "test-box");
        await repo.InsertSummaryAsync(summary);

        MachineStateSummary? retrieved = await repo.GetSummaryForMachineAsync(100);

        await Assert.That(retrieved).IsNotNull();
        await Assert.That(retrieved!.MachineId).IsEqualTo(100L);
        await Assert.That(retrieved.Name).IsEqualTo("test-box");
        await Assert.That(retrieved.TenantId).IsEqualTo(1);
    }

    [Test]
    public async Task InsertSummaryAsync_NullInput_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(async () => await repo.InsertSummaryAsync(null!)).Throws<ArgumentNullException>();
    }

    // ========== InsertDetailAsync tests ==========

    [Test]
    public async Task InsertDetailAsync_ValidDetail_InsertsSuccessfully()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        MachineStateDetail detail = new() { MachineId = 200, CpuBrand = "Intel Xeon" };
        await repo.InsertDetailAsync(detail);

        // Verify by reading back through the context directly
        MachineStateDetail? retrieved = await dbFactory.Context.MachineStateDetails
            .Where(d => d.MachineId == 200)
            .FirstOrDefaultAsync();

        await Assert.That(retrieved).IsNotNull();
        await Assert.That(retrieved!.MachineId).IsEqualTo(200L);
        await Assert.That(retrieved.CpuBrand).IsEqualTo("Intel Xeon");
    }

    [Test]
    public async Task InsertDetailAsync_NullInput_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(async () => await repo.InsertDetailAsync(null!)).Throws<ArgumentNullException>();
    }

    // ========== InsertTelemetryAsync tests ==========

    [Test]
    public async Task InsertTelemetryAsync_ValidRow_InsertsAndAssignsId()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        MachineTelemetry row = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 3);
        await repo.InsertTelemetryAsync(row);

        List<MachineTelemetry> all = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(all.Count).IsEqualTo(1);
        await Assert.That(all[0].TelemetryType).IsEqualTo((short)3);
    }

    [Test]
    public async Task InsertTelemetryAsync_NullInput_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(async () => await repo.InsertTelemetryAsync(null!)).Throws<ArgumentNullException>();
    }

    // ========== BulkInsertTelemetryAsync tests ==========

    [Test]
    public async Task BulkInsertTelemetryAsync_MultipleRows_InsertsAll()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<MachineTelemetry> rows = new()
        {
            TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 1),
            TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 2),
            TestDataBuilder.BuildMachineTelemetry(machineId: 2, tenantId: 1, telemetryType: 1),
        };

        await repo.BulkInsertTelemetryAsync(rows);

        List<MachineTelemetry> all = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(all.Count).IsEqualTo(3);
    }

    [Test]
    public async Task BulkInsertTelemetryAsync_NullInput_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(async () => await repo.BulkInsertTelemetryAsync(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task BulkInsertTelemetryAsync_EmptyList_InsertsNothing()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await repo.BulkInsertTelemetryAsync(new List<MachineTelemetry>());

        List<MachineTelemetry> all = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(all.Count).IsEqualTo(0);
    }

    // ========== GetDistinctTenantIdsAsync tests ==========

    [Test]
    public async Task GetDistinctTenantIdsAsync_EmptyTable_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<int> result = await repo.GetDistinctTenantIdsAsync();

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetDistinctTenantIdsAsync_MultipleTenants_ReturnsDistinctIds()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 1, tenantId: 10));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 2, tenantId: 20));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 3, tenantId: 10));

        List<int> result = await repo.GetDistinctTenantIdsAsync();

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result).Contains(10);
        await Assert.That(result).Contains(20);
    }

    // ========== GetSummaryForMachineAsync tests ==========

    [Test]
    public async Task GetSummaryForMachineAsync_ExistingSummary_ReturnsSummary()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 42, tenantId: 1, name: "found-box"));

        MachineStateSummary? result = await repo.GetSummaryForMachineAsync(42);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.MachineId).IsEqualTo(42L);
        await Assert.That(result.Name).IsEqualTo("found-box");
    }

    [Test]
    public async Task GetSummaryForMachineAsync_NoSummary_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        MachineStateSummary? result = await repo.GetSummaryForMachineAsync(99999);

        await Assert.That(result).IsNull();
    }

    // ========== GetSummariesForTenantMachinesAsync tests ==========

    [Test]
    public async Task GetSummariesForTenantMachinesAsync_WithActiveMachines_ReturnsSummaries()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int _, int tenantId, long machineId) = await SeedMachineWithStateAsync(dbFactory);

        List<MachineStateSummary> result = await repo.GetSummariesForTenantMachinesAsync(tenantId);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].MachineId).IsEqualTo(machineId);
    }

    [Test]
    public async Task GetSummariesForTenantMachinesAsync_EmptyTenant_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<MachineStateSummary> result = await repo.GetSummariesForTenantMachinesAsync(99999);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetSummariesForTenantMachinesAsync_DeletedMachines_ExcludesDeleted()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Active machine with state summary
        Machine activeMachine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long activeMachineId = await dbFactory.Context.InsertWithInt64IdentityAsync(activeMachine);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: activeMachineId, tenantId: tenantId));

        // Deleted machine with state summary
        Machine deletedMachine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        deletedMachine.IsDeleted = true;
        long deletedMachineId = await dbFactory.Context.InsertWithInt64IdentityAsync(deletedMachine);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: deletedMachineId, tenantId: tenantId));

        List<MachineStateSummary> result = await repo.GetSummariesForTenantMachinesAsync(tenantId);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].MachineId).IsEqualTo(activeMachineId);
    }

    // ========== GetHostnameMapAsync tests ==========

    [Test]
    public async Task GetHostnameMapAsync_WithData_ReturnsMachineIdToHostnameMapping()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 1, tenantId: 1));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 2, tenantId: 1));

        Dictionary<long, string?> result = await repo.GetHostnameMapAsync(new List<long> { 1, 2 });

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.ContainsKey(1)).IsTrue();
        await Assert.That(result.ContainsKey(2)).IsTrue();
        // Default hostname from BuildMachineStateSummary is "host-{machineId}"
        await Assert.That(result[1]).IsEqualTo("host-1");
        await Assert.That(result[2]).IsEqualTo("host-2");
    }

    [Test]
    public async Task GetHostnameMapAsync_EmptyList_ReturnsEmptyDictionary()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Dictionary<long, string?> result = await repo.GetHostnameMapAsync(new List<long>());

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetHostnameMapAsync_PartialMatch_ReturnsOnlyMatchingEntries()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 10, tenantId: 1));

        // Request includes an ID that does not exist in the table
        Dictionary<long, string?> result = await repo.GetHostnameMapAsync(new List<long> { 10, 999 });

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result.ContainsKey(10)).IsTrue();
        await Assert.That(result.ContainsKey(999)).IsFalse();
    }

    // ========== GetNameMapAsync tests ==========

    [Test]
    public async Task GetNameMapAsync_WithData_ReturnsMachineIdToNameMapping()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 5, tenantId: 1, name: "web-server"));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 6, tenantId: 1, name: "db-server"));

        Dictionary<long, string> result = await repo.GetNameMapAsync(new List<long> { 5, 6 });

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[5]).IsEqualTo("web-server");
        await Assert.That(result[6]).IsEqualTo("db-server");
    }

    [Test]
    public async Task GetNameMapAsync_EmptyList_ReturnsEmptyDictionary()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Dictionary<long, string> result = await repo.GetNameMapAsync(new List<long>());

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== GetSummariesByMachineIdsAsync tests ==========

    [Test]
    public async Task GetSummariesByMachineIdsAsync_WithData_ReturnsDictionaryKeyedByMachineId()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 11, tenantId: 1, name: "alpha"));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 12, tenantId: 1, name: "beta"));

        Dictionary<long, MachineStateSummary> result = await repo.GetSummariesByMachineIdsAsync(new List<long> { 11, 12 });

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[11].Name).IsEqualTo("alpha");
        await Assert.That(result[12].Name).IsEqualTo("beta");
    }

    [Test]
    public async Task GetSummariesByMachineIdsAsync_EmptyList_ReturnsEmptyDictionary()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Dictionary<long, MachineStateSummary> result = await repo.GetSummariesByMachineIdsAsync(new List<long>());

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== GetSummaryListByMachineIdsAsync tests ==========

    [Test]
    public async Task GetSummaryListByMachineIdsAsync_WithData_ReturnsList()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 21, tenantId: 1));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: 22, tenantId: 1));

        List<MachineStateSummary> result = await repo.GetSummaryListByMachineIdsAsync(new List<long> { 21, 22 });

        await Assert.That(result.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetSummaryListByMachineIdsAsync_EmptyList_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<MachineStateSummary> result = await repo.GetSummaryListByMachineIdsAsync(new List<long>());

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== UpdateSummaryNameAsync tests ==========

    [Test]
    public async Task UpdateSummaryNameAsync_ExistingRow_UpdatesName()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int _, int _, long machineId) = await SeedMachineWithStateAsync(dbFactory);

        await repo.UpdateSummaryNameAsync(machineId, "renamed-box");

        MachineStateSummary? updated = await repo.GetSummaryForMachineAsync(machineId);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Name).IsEqualTo("renamed-box");
    }

    // ========== GetTelemetryBatchAsync tests ==========

    [Test]
    public async Task GetTelemetryBatchAsync_WithRows_ReturnsCorrectBatch()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Insert telemetry rows with explicit ReceivedAt within the streaming window
        MachineTelemetry row1 = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 1);
        row1.ReceivedAt = now.AddMinutes(-5);
        await repo.InsertTelemetryAsync(row1);

        MachineTelemetry row2 = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 2);
        row2.ReceivedAt = now.AddMinutes(-3);
        await repo.InsertTelemetryAsync(row2);

        MachineTelemetry row3 = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 3);
        row3.ReceivedAt = now.AddMinutes(-1);
        await repo.InsertTelemetryAsync(row3);

        // Fetch batch with high water mark of 0 and a streaming window in the past
        DateTimeOffset streamingWindow = now.AddMinutes(-10);
        List<MachineTelemetry> batch = await repo.GetTelemetryBatchAsync(0, streamingWindow, batchSize: 10);

        await Assert.That(batch.Count).IsEqualTo(3);
        // Verify ascending order by Id
        await Assert.That(batch[0].Id < batch[1].Id).IsTrue();
        await Assert.That(batch[1].Id < batch[2].Id).IsTrue();
    }

    [Test]
    public async Task GetTelemetryBatchAsync_RespectsHighWaterMark_OnlyReturnsNewerRows()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        MachineTelemetry row1 = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 1);
        row1.ReceivedAt = now.AddMinutes(-5);
        await repo.InsertTelemetryAsync(row1);

        MachineTelemetry row2 = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 2);
        row2.ReceivedAt = now.AddMinutes(-3);
        await repo.InsertTelemetryAsync(row2);

        // Get all rows to discover the first row's ID
        DateTimeOffset streamingWindow = now.AddMinutes(-10);
        List<MachineTelemetry> allRows = await repo.GetTelemetryBatchAsync(0, streamingWindow, batchSize: 10);
        long firstRowId = allRows[0].Id;

        // Fetch batch using first row's ID as the high water mark
        List<MachineTelemetry> batch = await repo.GetTelemetryBatchAsync(firstRowId, streamingWindow, batchSize: 10);

        await Assert.That(batch.Count).IsEqualTo(1);
        await Assert.That(batch[0].Id > firstRowId).IsTrue();
    }

    [Test]
    public async Task GetTelemetryBatchAsync_RespectsStreamingWindow_ExcludesOldRows()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Insert a row that is too old for the streaming window
        MachineTelemetry oldRow = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 1);
        oldRow.ReceivedAt = now.AddHours(-2);
        await repo.InsertTelemetryAsync(oldRow);

        // Insert a row within the streaming window
        MachineTelemetry newRow = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 2);
        newRow.ReceivedAt = now.AddMinutes(-5);
        await repo.InsertTelemetryAsync(newRow);

        // Set the streaming window to 1 hour ago, so the old row is excluded
        DateTimeOffset streamingWindow = now.AddHours(-1);
        List<MachineTelemetry> batch = await repo.GetTelemetryBatchAsync(0, streamingWindow, batchSize: 10);

        await Assert.That(batch.Count).IsEqualTo(1);
        await Assert.That(batch[0].TelemetryType).IsEqualTo((short)2);
    }

    // ========== GetLatestTelemetryPerTypeAsync tests ==========

    [Test]
    public async Task GetLatestTelemetryPerTypeAsync_MultipleTypes_ReturnsLatestPerType()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Two rows for type 1 - only the latest should be returned
        MachineTelemetry type1Old = TestDataBuilder.BuildMachineTelemetry(machineId: 50, tenantId: 1, telemetryType: 1, payload: """{"old":true}""");
        type1Old.ReceivedAt = now.AddMinutes(-30);
        await repo.InsertTelemetryAsync(type1Old);

        MachineTelemetry type1New = TestDataBuilder.BuildMachineTelemetry(machineId: 50, tenantId: 1, telemetryType: 1, payload: """{"old":false}""");
        type1New.ReceivedAt = now.AddMinutes(-5);
        await repo.InsertTelemetryAsync(type1New);

        // One row for type 2
        MachineTelemetry type2 = TestDataBuilder.BuildMachineTelemetry(machineId: 50, tenantId: 1, telemetryType: 2, payload: """{"type":2}""");
        type2.ReceivedAt = now.AddMinutes(-10);
        await repo.InsertTelemetryAsync(type2);

        Dictionary<short, MachineTelemetry> result = await repo.GetLatestTelemetryPerTypeAsync(50, daysBack: 1);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.ContainsKey(1)).IsTrue();
        await Assert.That(result.ContainsKey(2)).IsTrue();
        await Assert.That(result[1].Payload).IsEqualTo("""{"old":false}""");
        await Assert.That(result[2].Payload).IsEqualTo("""{"type":2}""");
    }

    [Test]
    public async Task GetLatestTelemetryPerTypeAsync_OldDataBeyondDaysBack_ExcludesOldRows()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Row received 10 days ago
        MachineTelemetry oldRow = TestDataBuilder.BuildMachineTelemetry(machineId: 60, tenantId: 1, telemetryType: 1);
        oldRow.ReceivedAt = now.AddDays(-10);
        await repo.InsertTelemetryAsync(oldRow);

        // Row received 1 hour ago
        MachineTelemetry recentRow = TestDataBuilder.BuildMachineTelemetry(machineId: 60, tenantId: 1, telemetryType: 2);
        recentRow.ReceivedAt = now.AddHours(-1);
        await repo.InsertTelemetryAsync(recentRow);

        // Only look back 3 days - the 10-day-old row should be excluded
        Dictionary<short, MachineTelemetry> result = await repo.GetLatestTelemetryPerTypeAsync(60, daysBack: 3);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result.ContainsKey(2)).IsTrue();
    }

    // ========== GetRecentTelemetryAsync tests ==========

    [Test]
    public async Task GetRecentTelemetryAsync_MultipleRows_ReturnsInDescendingOrder()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        MachineTelemetry row1 = TestDataBuilder.BuildMachineTelemetry(machineId: 70, tenantId: 1, telemetryType: 5, payload: """{"order":1}""");
        row1.ReceivedAt = now.AddMinutes(-30);
        await repo.InsertTelemetryAsync(row1);

        MachineTelemetry row2 = TestDataBuilder.BuildMachineTelemetry(machineId: 70, tenantId: 1, telemetryType: 5, payload: """{"order":2}""");
        row2.ReceivedAt = now.AddMinutes(-20);
        await repo.InsertTelemetryAsync(row2);

        MachineTelemetry row3 = TestDataBuilder.BuildMachineTelemetry(machineId: 70, tenantId: 1, telemetryType: 5, payload: """{"order":3}""");
        row3.ReceivedAt = now.AddMinutes(-10);
        await repo.InsertTelemetryAsync(row3);

        List<MachineTelemetry> result = await repo.GetRecentTelemetryAsync(70, telemetryType: 5, limit: 10);

        await Assert.That(result.Count).IsEqualTo(3);
        // Most recent first (descending by ReceivedAt)
        await Assert.That(result[0].Payload).IsEqualTo("""{"order":3}""");
        await Assert.That(result[2].Payload).IsEqualTo("""{"order":1}""");
    }

    [Test]
    public async Task GetRecentTelemetryAsync_RespectsLimit_ReturnsOnlyRequestedCount()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 5; i++)
        {
            MachineTelemetry row = TestDataBuilder.BuildMachineTelemetry(machineId: 80, tenantId: 1, telemetryType: 7);
            row.ReceivedAt = now.AddMinutes(-i);
            await repo.InsertTelemetryAsync(row);
        }

        List<MachineTelemetry> result = await repo.GetRecentTelemetryAsync(80, telemetryType: 7, limit: 2);

        await Assert.That(result.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetRecentTelemetryAsync_DifferentType_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        MachineTelemetry row = TestDataBuilder.BuildMachineTelemetry(machineId: 90, tenantId: 1, telemetryType: 1);
        await repo.InsertTelemetryAsync(row);

        // Query for a different telemetry type than what was inserted
        List<MachineTelemetry> result = await repo.GetRecentTelemetryAsync(90, telemetryType: 99, limit: 10);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== GetTelemetryByMachineIdsAndTypeAsync tests ==========

    [Test]
    public async Task GetTelemetryByMachineIdsAndTypeAsync_FiltersByMachineIdsAndType_ReturnsDescendingOrder()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Machine 1, type 1 (should be returned)
        MachineTelemetry m1t1a = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 1, payload: """{"a":1}""");
        m1t1a.ReceivedAt = now.AddMinutes(-20);
        await repo.InsertTelemetryAsync(m1t1a);

        MachineTelemetry m1t1b = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 1, payload: """{"a":2}""");
        m1t1b.ReceivedAt = now.AddMinutes(-5);
        await repo.InsertTelemetryAsync(m1t1b);

        // Machine 2, type 1 (should be returned)
        MachineTelemetry m2t1 = TestDataBuilder.BuildMachineTelemetry(machineId: 2, tenantId: 1, telemetryType: 1, payload: """{"b":1}""");
        m2t1.ReceivedAt = now.AddMinutes(-10);
        await repo.InsertTelemetryAsync(m2t1);

        // Machine 1, type 2 (different type, should NOT be returned)
        MachineTelemetry m1t2 = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 2, payload: """{"c":1}""");
        m1t2.ReceivedAt = now.AddMinutes(-8);
        await repo.InsertTelemetryAsync(m1t2);

        // Machine 3, type 1 (different machine, should NOT be returned)
        MachineTelemetry m3t1 = TestDataBuilder.BuildMachineTelemetry(machineId: 3, tenantId: 1, telemetryType: 1, payload: """{"d":1}""");
        m3t1.ReceivedAt = now.AddMinutes(-2);
        await repo.InsertTelemetryAsync(m3t1);

        List<MachineTelemetry> result = await repo.GetTelemetryByMachineIdsAndTypeAsync(new List<long> { 1, 2 }, 1);

        await Assert.That(result.Count).IsEqualTo(3);
        // Verify descending order by ReceivedAt
        await Assert.That(result[0].ReceivedAt >= result[1].ReceivedAt).IsTrue();
        await Assert.That(result[1].ReceivedAt >= result[2].ReceivedAt).IsTrue();
    }

    // ========== GetTelemetryExportBatchAsync tests ==========

    [Test]
    public async Task GetTelemetryExportBatchAsync_CursorPaginationAndBatchSize_ReturnsCorrectBatch()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Insert 5 telemetry rows for machine 1
        for (int i = 0; i < 5; i++)
        {
            MachineTelemetry row = TestDataBuilder.BuildMachineTelemetry(machineId: 1, tenantId: 1, telemetryType: 1);
            row.ReceivedAt = now.AddMinutes(-i);
            await repo.InsertTelemetryAsync(row);
        }

        // Get first batch of 2 starting from id 0
        List<MachineTelemetry> batch1 = await repo.GetTelemetryExportBatchAsync(new List<long> { 1 }, 0, 2);

        await Assert.That(batch1.Count).IsEqualTo(2);
        // Verify ascending order by Id
        await Assert.That(batch1[0].Id < batch1[1].Id).IsTrue();

        // Get next batch using the last id from batch1 as cursor
        long cursor = batch1[1].Id;
        List<MachineTelemetry> batch2 = await repo.GetTelemetryExportBatchAsync(new List<long> { 1 }, cursor, 2);

        await Assert.That(batch2.Count).IsEqualTo(2);
        // All IDs in batch2 should be greater than the cursor
        await Assert.That(batch2[0].Id > cursor).IsTrue();
        await Assert.That(batch2[0].Id < batch2[1].Id).IsTrue();

        // Get final batch - should have only 1 remaining row
        long cursor2 = batch2[1].Id;
        List<MachineTelemetry> batch3 = await repo.GetTelemetryExportBatchAsync(new List<long> { 1 }, cursor2, 2);

        await Assert.That(batch3.Count).IsEqualTo(1);
        await Assert.That(batch3[0].Id > cursor2).IsTrue();
    }

    // ========== GetFleetHealthAggregationAsync tests ==========

    [Test]
    public async Task GetFleetHealthAggregationAsync_MultipleMachines_ReturnsHealthCountsAndSecurityUpdatesSum()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Machine 1: healthy (health=0) with 5 security updates
        Machine m1 = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long m1Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m1);
        MachineStateSummary s1 = TestDataBuilder.BuildMachineStateSummary(machineId: m1Id, tenantId: tenantId, healthStatus: 0);
        s1.SecurityUpdates = 5;
        await dbFactory.Context.InsertAsync(s1);

        // Machine 2: warning (health=1) with 3 security updates
        Machine m2 = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long m2Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m2);
        MachineStateSummary s2 = TestDataBuilder.BuildMachineStateSummary(machineId: m2Id, tenantId: tenantId, healthStatus: 1);
        s2.SecurityUpdates = 3;
        await dbFactory.Context.InsertAsync(s2);

        // Machine 3: critical (health=2) with 7 security updates
        Machine m3 = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long m3Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m3);
        MachineStateSummary s3 = TestDataBuilder.BuildMachineStateSummary(machineId: m3Id, tenantId: tenantId, healthStatus: 2);
        s3.SecurityUpdates = 7;
        await dbFactory.Context.InsertAsync(s3);

        (List<(short HealthStatus, int Count)> healthCounts, int totalSecurityUpdates) = await repo.GetFleetHealthAggregationAsync(tenantId);

        await Assert.That(healthCounts.Count).IsEqualTo(3);
        await Assert.That(totalSecurityUpdates).IsEqualTo(15);

        // Verify each health status has exactly 1 machine
        (short HealthStatus, int Count) healthyGroup = healthCounts.First(h => h.HealthStatus == 0);
        await Assert.That(healthyGroup.Count).IsEqualTo(1);
        (short HealthStatus, int Count) warningGroup = healthCounts.First(h => h.HealthStatus == 1);
        await Assert.That(warningGroup.Count).IsEqualTo(1);
        (short HealthStatus, int Count) criticalGroup = healthCounts.First(h => h.HealthStatus == 2);
        await Assert.That(criticalGroup.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetFleetHealthAggregationAsync_EmptyTenant_ReturnsEmptyHealthCountsAndZeroSecurityUpdates()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        (List<(short HealthStatus, int Count)> healthCounts, int totalSecurityUpdates) = await repo.GetFleetHealthAggregationAsync(tenantId);

        await Assert.That(healthCounts.Count).IsEqualTo(0);
        await Assert.That(totalSecurityUpdates).IsEqualTo(0);
    }

    // ========== GetFleetMachinePageAsync tests ==========

    [Test]
    public async Task GetFleetMachinePageAsync_BasicPagination_ReturnsTotalCountAndRequestedPage()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Seed 5 machines with summary rows
        for (int i = 0; i < 5; i++)
        {
            Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
            long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
            await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machineId, tenantId: tenantId));
        }

        (List<FleetMachineRow> rows, int totalCount) = await repo.GetFleetMachinePageAsync(tenantId, null, null, "name", false, 0, 2);

        await Assert.That(totalCount).IsEqualTo(5);
        await Assert.That(rows.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetFleetMachinePageAsync_StatusFilter_ReturnsOnlyMatchingHealth()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Healthy machine
        Machine healthy = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long healthyId = await dbFactory.Context.InsertWithInt64IdentityAsync(healthy);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: healthyId, tenantId: tenantId, healthStatus: 0));

        // Warning machine
        Machine warning = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long warningId = await dbFactory.Context.InsertWithInt64IdentityAsync(warning);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: warningId, tenantId: tenantId, healthStatus: 1));

        // Critical machine
        Machine critical = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long criticalId = await dbFactory.Context.InsertWithInt64IdentityAsync(critical);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: criticalId, tenantId: tenantId, healthStatus: 2));

        (List<FleetMachineRow> rows, int totalCount) = await repo.GetFleetMachinePageAsync(tenantId, "healthy", null, "name", false, 0, 10);

        await Assert.That(totalCount).IsEqualTo(1);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].HealthStatus).IsEqualTo((short)0);
    }

    [Test]
    public async Task GetFleetMachinePageAsync_TextSearch_ReturnsMatchingMachines()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Machine with a searchable name
        Machine webServer = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: "web-prod-01");
        long webId = await dbFactory.Context.InsertWithInt64IdentityAsync(webServer);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: webId, tenantId: tenantId, name: "web-prod-01"));

        // Machine with a different name
        Machine dbServer = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: "db-prod-01");
        long dbId = await dbFactory.Context.InsertWithInt64IdentityAsync(dbServer);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: dbId, tenantId: tenantId, name: "db-prod-01"));

        (List<FleetMachineRow> rows, int totalCount) = await repo.GetFleetMachinePageAsync(tenantId, null, "web", "name", false, 0, 10);

        await Assert.That(totalCount).IsEqualTo(1);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Name).IsEqualTo("web-prod-01");
    }

    [Test]
    public async Task GetFleetMachinePageAsync_SortByCpuDescending_ReturnsMachinesInDescendingCpuOrder()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Machine with low CPU
        Machine lowCpu = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long lowId = await dbFactory.Context.InsertWithInt64IdentityAsync(lowCpu);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: lowId, tenantId: tenantId, cpuPercent: 10));

        // Machine with high CPU
        Machine highCpu = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long highId = await dbFactory.Context.InsertWithInt64IdentityAsync(highCpu);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: highId, tenantId: tenantId, cpuPercent: 90));

        // Machine with mid CPU
        Machine midCpu = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long midId = await dbFactory.Context.InsertWithInt64IdentityAsync(midCpu);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: midId, tenantId: tenantId, cpuPercent: 50));

        (List<FleetMachineRow> rows, int totalCount) = await repo.GetFleetMachinePageAsync(tenantId, null, null, "cpu", true, 0, 10);

        await Assert.That(totalCount).IsEqualTo(3);
        await Assert.That(rows.Count).IsEqualTo(3);
        // Verify descending order: highest CPU first
        await Assert.That(rows[0].CpuUsagePercent).IsEqualTo(90);
        await Assert.That(rows[1].CpuUsagePercent).IsEqualTo(50);
        await Assert.That(rows[2].CpuUsagePercent).IsEqualTo(10);
    }

    // ========== SearchFleetMachinesAsync tests ==========

    [Test]
    public async Task SearchFleetMachinesAsync_CpuRangeFilter_ReturnsOnlyMachinesWithinRange()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Machine with CPU 20
        Machine low = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long lowId = await dbFactory.Context.InsertWithInt64IdentityAsync(low);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: lowId, tenantId: tenantId, cpuPercent: 20));

        // Machine with CPU 50
        Machine mid = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long midId = await dbFactory.Context.InsertWithInt64IdentityAsync(mid);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: midId, tenantId: tenantId, cpuPercent: 50));

        // Machine with CPU 80
        Machine high = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long highId = await dbFactory.Context.InsertWithInt64IdentityAsync(high);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: highId, tenantId: tenantId, cpuPercent: 80));

        FleetSearchParameters parameters = new()
        {
            CpuMin = 40,
            CpuMax = 60,
            Skip = 0,
            Take = 10,
        };

        (List<FleetMachineRow> rows, int totalCount) = await repo.SearchFleetMachinesAsync(tenantId, parameters);

        await Assert.That(totalCount).IsEqualTo(1);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].CpuUsagePercent).IsEqualTo(50);
    }

    [Test]
    public async Task SearchFleetMachinesAsync_HealthStatusFilter_ReturnsOnlyMatchingStatuses()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Seed machines with health 0, 1, 2, 3
        short[] statuses = { 0, 1, 2, 3 };
        foreach (short status in statuses)
        {
            Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
            long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
            await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machineId, tenantId: tenantId, healthStatus: status));
        }

        FleetSearchParameters parameters = new()
        {
            HealthStatusValues = new List<short> { 1, 2 },
            Skip = 0,
            Take = 10,
        };

        (List<FleetMachineRow> rows, int totalCount) = await repo.SearchFleetMachinesAsync(tenantId, parameters);

        await Assert.That(totalCount).IsEqualTo(2);
        await Assert.That(rows.Count).IsEqualTo(2);
        // All returned rows should have health status 1 or 2
        await Assert.That(rows.All(r => (r.HealthStatus == 1) || (r.HealthStatus == 2))).IsTrue();
    }

    [Test]
    public async Task SearchFleetMachinesAsync_CombinedSearchAndHealthFilter_ReturnsMatchingResults()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // web-alpha, healthy
        Machine webAlpha = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: "web-alpha");
        long webAlphaId = await dbFactory.Context.InsertWithInt64IdentityAsync(webAlpha);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: webAlphaId, tenantId: tenantId, name: "web-alpha", healthStatus: 0));

        // web-beta, warning
        Machine webBeta = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: "web-beta");
        long webBetaId = await dbFactory.Context.InsertWithInt64IdentityAsync(webBeta);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: webBetaId, tenantId: tenantId, name: "web-beta", healthStatus: 1));

        // db-gamma, warning
        Machine dbGamma = TestDataBuilder.BuildMachine(tenantId: tenantId, hostname: "db-gamma");
        long dbGammaId = await dbFactory.Context.InsertWithInt64IdentityAsync(dbGamma);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: dbGammaId, tenantId: tenantId, name: "db-gamma", healthStatus: 1));

        // Search for "web" with health status = warning (1)
        FleetSearchParameters parameters = new()
        {
            Search = "web",
            HealthStatusValues = new List<short> { 1 },
            Skip = 0,
            Take = 10,
        };

        (List<FleetMachineRow> rows, int totalCount) = await repo.SearchFleetMachinesAsync(tenantId, parameters);

        // Only web-beta should match (name contains "web" AND health status is 1)
        await Assert.That(totalCount).IsEqualTo(1);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Name).IsEqualTo("web-beta");
    }

    // ========== SweepHealthStatusAsync tests ==========

    [Test]
    public async Task SweepHealthStatusAsync_WithKnownMetrics_UpdatesHealthStatusCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        DateTimeOffset recentTime = DateTimeOffset.UtcNow;

        // Machine 1: healthy - low CPU, low memory, no failed services, recently seen
        Machine m1 = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long m1Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m1);
        MachineStateSummary s1 = TestDataBuilder.BuildMachineStateSummary(machineId: m1Id, tenantId: tenantId, cpuPercent: 30, memoryPercent: 40, healthStatus: 0, lastSeenAt: recentTime);
        s1.FailedServices = 0;
        await dbFactory.Context.InsertAsync(s1);

        // Machine 2: should become critical - high CPU (95%+)
        Machine m2 = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long m2Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m2);
        MachineStateSummary s2 = TestDataBuilder.BuildMachineStateSummary(machineId: m2Id, tenantId: tenantId, cpuPercent: 96, memoryPercent: 40, healthStatus: 0, lastSeenAt: recentTime);
        s2.FailedServices = 0;
        await dbFactory.Context.InsertAsync(s2);

        // Machine 3: should become warning - CPU 80-94
        Machine m3 = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long m3Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m3);
        MachineStateSummary s3 = TestDataBuilder.BuildMachineStateSummary(machineId: m3Id, tenantId: tenantId, cpuPercent: 85, memoryPercent: 40, healthStatus: 0, lastSeenAt: recentTime);
        s3.FailedServices = 0;
        await dbFactory.Context.InsertAsync(s3);

        // Machine 4: should become critical - failed services > 0
        Machine m4 = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long m4Id = await dbFactory.Context.InsertWithInt64IdentityAsync(m4);
        MachineStateSummary s4 = TestDataBuilder.BuildMachineStateSummary(machineId: m4Id, tenantId: tenantId, cpuPercent: 10, memoryPercent: 10, healthStatus: 0, lastSeenAt: recentTime);
        s4.FailedServices = 2;
        await dbFactory.Context.InsertAsync(s4);

        SqliteSqlDialect dialect = new();
        int rowsAffected = await repo.SweepHealthStatusAsync(dialect.HealthSweepForTenant, tenantId, 300);

        // At least 3 machines should have had their status changed (m2, m3, m4 all started at 0)
        await Assert.That(rowsAffected >= 3).IsTrue();

        // Verify individual machine health statuses
        MachineStateSummary? updated1 = await repo.GetSummaryForMachineAsync(m1Id);
        await Assert.That(updated1).IsNotNull();
        await Assert.That(updated1!.HealthStatus).IsEqualTo((short)0);

        MachineStateSummary? updated2 = await repo.GetSummaryForMachineAsync(m2Id);
        await Assert.That(updated2).IsNotNull();
        await Assert.That(updated2!.HealthStatus).IsEqualTo((short)2);

        MachineStateSummary? updated3 = await repo.GetSummaryForMachineAsync(m3Id);
        await Assert.That(updated3).IsNotNull();
        await Assert.That(updated3!.HealthStatus).IsEqualTo((short)1);

        MachineStateSummary? updated4 = await repo.GetSummaryForMachineAsync(m4Id);
        await Assert.That(updated4).IsNotNull();
        await Assert.That(updated4!.HealthStatus).IsEqualTo((short)2);
    }

    // ========== GetFleetHealthAggregationAsync tests ==========

    [Test]
    public async Task GetFleetHealthAggregationAsync_GroupsByHealthStatusAndSumsSecurityUpdates()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Two healthy (status 0) and one critical (status 2). Security updates sum to 12.
        await SeedFleetMachineAsync(dbFactory, tenantId, healthStatus: 0, securityUpdates: 2);
        await SeedFleetMachineAsync(dbFactory, tenantId, healthStatus: 0, securityUpdates: 3);
        await SeedFleetMachineAsync(dbFactory, tenantId, healthStatus: 2, securityUpdates: 7);

        (List<(short HealthStatus, int Count)> healthCounts, int totalSecurityUpdates) =
            await repo.GetFleetHealthAggregationAsync(tenantId, CancellationToken.None);

        Dictionary<short, int> byStatus = healthCounts.ToDictionary(x => x.HealthStatus, x => x.Count);
        await Assert.That(byStatus[0]).IsEqualTo(2);
        await Assert.That(byStatus[2]).IsEqualTo(1);
        await Assert.That(totalSecurityUpdates).IsEqualTo(12);
    }

    [Test]
    public async Task GetFleetHealthAggregationAsync_NoMachines_ReturnsEmptyAndZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (List<(short HealthStatus, int Count)> healthCounts, int totalSecurityUpdates) =
            await repo.GetFleetHealthAggregationAsync(99999, CancellationToken.None);

        await Assert.That(healthCounts.Count).IsEqualTo(0);
        await Assert.That(totalSecurityUpdates).IsEqualTo(0);
    }

    // ========== GetTelemetryPageByMachineIdsAndTypeAsync / CountTelemetryByMachineIdsAndTypeAsync ==========

    [Test]
    public async Task GetTelemetryPageByMachineIdsAndType_AppliesTimeWindowOrderingAndPaging()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset since = now.AddDays(-2);

        // Three in-window rows for machine 1 (newest first: r3, r2, r1) and one out-of-window row.
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now.AddHours(-1), id: 1);
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now.AddMinutes(-30), id: 2);
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now.AddMinutes(-5), id: 3);
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now.AddDays(-10), id: 4);
        // A different telemetry type that must be excluded.
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 1, receivedAt: now, id: 5);

        int count = await repo.CountTelemetryByMachineIdsAndTypeAsync([1], 9, since, CancellationToken.None);
        await Assert.That(count).IsEqualTo(3);

        // First page of size 2: newest two rows (id 3 then id 2).
        List<MachineTelemetry> page1 = await repo.GetTelemetryPageByMachineIdsAndTypeAsync([1], 9, since, 0, 2, CancellationToken.None);
        await Assert.That(page1.Count).IsEqualTo(2);
        await Assert.That(page1[0].Id).IsEqualTo(3L);
        await Assert.That(page1[1].Id).IsEqualTo(2L);

        // Second page of size 2: the remaining row (id 1).
        List<MachineTelemetry> page2 = await repo.GetTelemetryPageByMachineIdsAndTypeAsync([1], 9, since, 2, 2, CancellationToken.None);
        await Assert.That(page2.Count).IsEqualTo(1);
        await Assert.That(page2[0].Id).IsEqualTo(1L);
    }

    [Test]
    public async Task GetTelemetryPageByMachineIdsAndType_EmptyMachineIds_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<MachineTelemetry> page = await repo.GetTelemetryPageByMachineIdsAndTypeAsync([], 9, DateTimeOffset.UtcNow.AddDays(-1), 0, 10, CancellationToken.None);
        int count = await repo.CountTelemetryByMachineIdsAndTypeAsync([], 9, DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);

        await Assert.That(page.Count).IsEqualTo(0);
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task CountTelemetryByMachineIdsAndType_OnlyCountsRowsWithinWindowAndType()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset since = now.AddDays(-1);

        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now, id: 1);
        await InsertTelemetryAsync(dbFactory, machineId: 2, telemetryType: 9, receivedAt: now, id: 2);
        await InsertTelemetryAsync(dbFactory, machineId: 3, telemetryType: 9, receivedAt: now, id: 3); // not in id set
        await InsertTelemetryAsync(dbFactory, machineId: 1, telemetryType: 9, receivedAt: now.AddDays(-5), id: 4); // out of window

        int count = await repo.CountTelemetryByMachineIdsAndTypeAsync([1, 2], 9, since, CancellationToken.None);

        await Assert.That(count).IsEqualTo(2);
    }

    private static async Task InsertTelemetryAsync(TestDatabaseFactory dbFactory, long machineId, short telemetryType, DateTimeOffset receivedAt, long id)
    {
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            Id = id,
            MachineId = machineId,
            TenantId = 1,
            TelemetryType = telemetryType,
            Payload = """{"user":"x"}""",
            ReceivedAt = receivedAt,
            SourceEventId = Guid.NewGuid().ToString("N"),
        });
    }

    private static async Task SeedFleetMachineAsync(TestDatabaseFactory dbFactory, int tenantId, short healthStatus, int securityUpdates)
    {
        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        MachineStateSummary summary = TestDataBuilder.BuildMachineStateSummary(machineId: machineId, tenantId: tenantId, healthStatus: healthStatus);
        summary.SecurityUpdates = securityUpdates;
        await dbFactory.Context.InsertAsync(summary);
    }
}
