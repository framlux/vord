// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Server.Services.Telemetry;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="MachineStateUpdater"/>.
/// </summary>
public class MachineStateUpdaterTests
{
    private static MachineStateUpdater CreateUpdater()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        ILogger<MachineStateUpdater> logger = new NullLogger<MachineStateUpdater>();
        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        IServerSettingsCache settingsCache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        ServerConfigurationService configService = new(settingsCache, redis);

        return new MachineStateUpdater(scopeFactory, logger, dialect, configService);
    }

    [Test]
    public async Task Deserialize_ValidSystemInfoJson_ReturnsObject()
    {
        string json = """{"hostname":"server1","cpu_brand":"Intel","cpu_physical_cores":4,"physical_memory":8589934592,"ip_addresses":["10.0.0.1"]}""";

        SystemInfoPayload? result = CreateUpdater().Deserialize<SystemInfoPayload>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Hostname).IsEqualTo("server1");
        await Assert.That(result.CpuBrand).IsEqualTo("Intel");
        await Assert.That(result.CpuPhysicalCores).IsEqualTo(4);
        await Assert.That(result.PhysicalMemory).IsEqualTo(8589934592L);
        await Assert.That(result.IpAddresses.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Deserialize_ValidOsVersionJson_ReturnsObject()
    {
        string json = """{"name":"Ubuntu","version":"22.04","build":"5.15.0-91-generic"}""";

        OsVersionPayload? result = CreateUpdater().Deserialize<OsVersionPayload>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("Ubuntu");
        await Assert.That(result.Version).IsEqualTo("22.04");
        await Assert.That(result.Build).IsEqualTo("5.15.0-91-generic");
    }

    [Test]
    public async Task Deserialize_ValidCpuUsageJson_ReturnsObject()
    {
        string json = """{"cpu_usage_percent":75}""";

        CpuUsagePayload? result = CreateUpdater().Deserialize<CpuUsagePayload>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.CpuUsagePercent).IsEqualTo(75);
    }

    [Test]
    public async Task Deserialize_ValidMemoryUsageJson_ReturnsObject()
    {
        string json = """{"memory_total":16000000000,"memory_used":8000000000,"memory_usage_percent":50}""";

        MemoryUsagePayload? result = CreateUpdater().Deserialize<MemoryUsagePayload>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.MemoryUsagePercent).IsEqualTo(50);
        await Assert.That(result.MemoryUsed).IsEqualTo(8000000000L);
    }

    [Test]
    public async Task Deserialize_ValidCpuInfoJson_ReturnsObject()
    {
        string json = """{"processor_type":"x86_64","number_of_cores":"8","logical_processors":16}""";

        CpuInfoPayload? result = CreateUpdater().Deserialize<CpuInfoPayload>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.ProcessorType).IsEqualTo("x86_64");
        await Assert.That(result.NumberOfCores).IsEqualTo("8");
        await Assert.That(result.LogicalProcessors).IsEqualTo(16);
    }

    [Test]
    public async Task Deserialize_ValidMemoryInfoJson_ReturnsObject()
    {
        string json = """{"memory_total":16000000000,"memory_free":4000000000,"memory_available":6000000000,"swap_total":2000000000,"swap_free":1500000000}""";

        MemoryInfoPayload? result = CreateUpdater().Deserialize<MemoryInfoPayload>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.SwapTotal).IsEqualTo(2000000000L);
        await Assert.That(result.SwapFree).IsEqualTo(1500000000L);
    }

    [Test]
    public async Task Deserialize_ValidPackageUpdatesJson_ReturnsObject()
    {
        string json = """{"package_manager":"apt","updates":[{"name":"openssl","current_version":"1.0","available_version":"1.1","is_security_update":true},{"name":"vim","current_version":"8.0","available_version":"8.1","is_security_update":false}]}""";

        PackageUpdatesPayload? result = CreateUpdater().Deserialize<PackageUpdatesPayload>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Updates.Count).IsEqualTo(2);
        await Assert.That(result.Updates.Count(u => u.IsSecurityUpdate)).IsEqualTo(1);
    }

    [Test]
    public async Task Deserialize_ValidServiceStatusJson_ReturnsObject()
    {
        string json = """{"services":[{"unit":"sshd.service","active_state":"active","sub_state":"running"},{"unit":"nginx.service","active_state":"failed","sub_state":"dead"}]}""";

        ServiceStatusPayload? result = CreateUpdater().Deserialize<ServiceStatusPayload>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Services.Count).IsEqualTo(2);

        int failedCount = result.Services.Count(s =>
            string.Equals(s.ActiveState, "failed", StringComparison.OrdinalIgnoreCase));

        await Assert.That(failedCount).IsEqualTo(1);
    }

    [Test]
    public async Task Deserialize_InvalidJson_ReturnsNull()
    {
        string json = "this is not valid json {{{";

        SystemInfoPayload? result = CreateUpdater().Deserialize<SystemInfoPayload>(json);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Deserialize_EmptyString_ReturnsNull()
    {
        string json = "";

        CpuUsagePayload? result = CreateUpdater().Deserialize<CpuUsagePayload>(json);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Deserialize_EmptyJsonObject_ReturnsObjectWithDefaults()
    {
        string json = "{}";

        CpuUsagePayload? result = CreateUpdater().Deserialize<CpuUsagePayload>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.CpuUsagePercent).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateAsync_UnknownTelemetryType_ThrowsWhenDbUnavailable()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider provider = Substitute.For<IServiceProvider>();
        scope.ServiceProvider.Returns(provider);
        scopeFactory.CreateScope().Returns(scope);

        ILogger<MachineStateUpdater> logger = new NullLogger<MachineStateUpdater>();
        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        dialect.UpsertLastTelemetry.Returns("SELECT 1");
        IServerSettingsCache settingsCache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        ServerConfigurationService configService = new(settingsCache, redis);
        MachineStateUpdater updater = new(scopeFactory, logger, dialect, configService);

        // Type 99 is unknown, should fall through to UpsertLastTelemetry.
        // The DB execution will fail (no DatabaseContext registered), and the
        // exception should propagate so callers can handle partial failures.
        await Assert.That(async () =>
            await updater.UpdateAsync(1, 99, "{}", DateTimeOffset.UtcNow, CancellationToken.None)
        ).Throws<Exception>();
    }

    [Test]
    public async Task UpdateAsync_SystemInfo_InvalidJson_ReturnsWithoutException()
    {
        (MachineStateUpdater updater, _) = CreateUpdaterWithDb();

        // Invalid JSON causes Deserialize to return null, so UpsertSystemInfo returns early
        // without calling db.ExecuteAsync — no SQL execution, no exception
        await updater.UpdateAsync(1, TelemetryTypeIds.SystemInfo, "not json", DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Test]
    public async Task UpdateAsync_OsVersion_InvalidJson_ReturnsWithoutException()
    {
        (MachineStateUpdater updater, _) = CreateUpdaterWithDb();

        await updater.UpdateAsync(1, TelemetryTypeIds.OsVersion, "{{{", DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Test]
    public async Task UpdateAsync_CpuUsage_InvalidJson_ReturnsWithoutException()
    {
        (MachineStateUpdater updater, _) = CreateUpdaterWithDb();

        await updater.UpdateAsync(1, TelemetryTypeIds.CpuUsage, "bad", DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Test]
    public async Task UpdateAsync_MemoryUsage_InvalidJson_ReturnsWithoutException()
    {
        (MachineStateUpdater updater, _) = CreateUpdaterWithDb();

        await updater.UpdateAsync(1, TelemetryTypeIds.MemoryUsage, "bad", DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Test]
    public async Task UpdateAsync_CpuInfo_InvalidJson_ReturnsWithoutException()
    {
        (MachineStateUpdater updater, _) = CreateUpdaterWithDb();

        await updater.UpdateAsync(1, TelemetryTypeIds.CpuInfo, "bad", DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Test]
    public async Task UpdateAsync_MemoryInfo_InvalidJson_ReturnsWithoutException()
    {
        (MachineStateUpdater updater, _) = CreateUpdaterWithDb();

        await updater.UpdateAsync(1, TelemetryTypeIds.MemoryInfo, "bad", DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Test]
    public async Task UpdateAsync_PackageUpdates_InvalidJson_ReturnsWithoutException()
    {
        (MachineStateUpdater updater, _) = CreateUpdaterWithDb();

        await updater.UpdateAsync(1, TelemetryTypeIds.PackageUpdates, "bad", DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Test]
    public async Task UpdateAsync_ServiceStatus_InvalidJson_ReturnsWithoutException()
    {
        (MachineStateUpdater updater, _) = CreateUpdaterWithDb();

        await updater.UpdateAsync(1, TelemetryTypeIds.ServiceStatus, "bad", DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Test]
    public async Task UpdateAsync_ExceptionDuringUpsert_LogsErrorAndRethrows()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider provider = Substitute.For<IServiceProvider>();
        scope.ServiceProvider.Returns(provider);
        scopeFactory.CreateScope().Returns(scope);

        // GetRequiredService<DatabaseContext>() will throw because nothing is registered
        ILogger<MachineStateUpdater> logger = Substitute.For<ILogger<MachineStateUpdater>>();
        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        IServerSettingsCache settingsCache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        ServerConfigurationService configService = new(settingsCache, redis);
        MachineStateUpdater updater = new(scopeFactory, logger, dialect, configService);

        // Valid JSON so Deserialize succeeds, but ExecuteAsync will fail due to missing DB
        string validJson = """{"hostname":"test","cpu_brand":"Intel","cpu_physical_cores":4,"physical_memory":8000000000,"ip_addresses":[]}""";

        await Assert.That(async () =>
            await updater.UpdateAsync(1, TelemetryTypeIds.SystemInfo, validJson, DateTimeOffset.UtcNow, CancellationToken.None)
        ).Throws<Exception>();

        // Verify the error was logged
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task UpdateAsync_DiskUsage_InvalidJson_StillAttemptsUpsert()
    {
        // DiskUsage is a raw JSON passthrough (no deserialization step), so even
        // invalid JSON will be passed to db.ExecuteAsync. With no real DB, this
        // will throw, confirming the switch case is reached.
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider provider = Substitute.For<IServiceProvider>();
        scope.ServiceProvider.Returns(provider);
        scopeFactory.CreateScope().Returns(scope);

        ILogger<MachineStateUpdater> logger = new NullLogger<MachineStateUpdater>();
        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        dialect.UpsertDiskUsage.Returns("SELECT 1");
        IServerSettingsCache settingsCache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        ServerConfigurationService configService = new(settingsCache, redis);
        MachineStateUpdater updater = new(scopeFactory, logger, dialect, configService);

        await Assert.That(async () =>
            await updater.UpdateAsync(1, TelemetryTypeIds.DiskUsage, "invalid", DateTimeOffset.UtcNow, CancellationToken.None)
        ).Throws<Exception>();
    }

    [Test]
    public async Task Deserialize_ServiceStatus_CaseInsensitiveFailed_CountsCorrectly()
    {
        // Verify that "FAILED" (uppercase) is also counted as failed
        string json = """{"services":[{"unit":"sshd.service","active_state":"FAILED","sub_state":"dead"},{"unit":"nginx.service","active_state":"active","sub_state":"running"}]}""";

        ServiceStatusPayload? result = CreateUpdater().Deserialize<ServiceStatusPayload>(json);

        await Assert.That(result).IsNotNull();

        int failedCount = result!.Services.Count(s =>
            string.Equals(s.ActiveState, "failed", StringComparison.OrdinalIgnoreCase));

        await Assert.That(failedCount).IsEqualTo(1);
    }

    [Test]
    public async Task Deserialize_PackageUpdates_NoSecurityUpdates_CountsZero()
    {
        string json = """{"package_manager":"apt","updates":[{"name":"vim","current_version":"8.0","available_version":"8.1","is_security_update":false}]}""";

        PackageUpdatesPayload? result = CreateUpdater().Deserialize<PackageUpdatesPayload>(json);

        await Assert.That(result).IsNotNull();

        int securityCount = result!.Updates.Count(u => u.IsSecurityUpdate);

        await Assert.That(securityCount).IsEqualTo(0);
        await Assert.That(result.Updates.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Deserialize_PackageUpdates_EmptyUpdates_ReturnsZeroCounts()
    {
        string json = """{"package_manager":"apt","updates":[]}""";

        PackageUpdatesPayload? result = CreateUpdater().Deserialize<PackageUpdatesPayload>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Updates.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Deserialize_SystemInfo_EmptyIpAddresses_ReturnsEmptyList()
    {
        string json = """{"hostname":"server1","cpu_brand":"Intel","cpu_physical_cores":4,"physical_memory":8000000000,"ip_addresses":[]}""";

        SystemInfoPayload? result = CreateUpdater().Deserialize<SystemInfoPayload>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.IpAddresses.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Deserialize_SystemInfo_MultipleIpAddresses_ReturnsAll()
    {
        string json = """{"hostname":"server1","cpu_brand":"AMD","cpu_physical_cores":16,"physical_memory":32000000000,"ip_addresses":["10.0.0.1","192.168.1.100","172.16.0.5"]}""";

        SystemInfoPayload? result = CreateUpdater().Deserialize<SystemInfoPayload>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.IpAddresses.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Deserialize_CpuInfo_InvalidCoresString_DefaultsToZero()
    {
        string json = """{"processor_type":"x86_64","number_of_cores":"abc","logical_processors":16}""";

        CpuInfoPayload? result = CreateUpdater().Deserialize<CpuInfoPayload>(json);

        await Assert.That(result).IsNotNull();
        // The MachineStateUpdater.UpsertCpuInfoAsync does: int.TryParse(info.NumberOfCores, out int cores) ? cores : 0
        // So "abc" → 0
        bool parsed = int.TryParse(result!.NumberOfCores, out int cores);

        await Assert.That(parsed).IsEqualTo(false);
        await Assert.That(cores).IsEqualTo(0);
    }

    [Test]
    public async Task Deserialize_ServiceStatus_AllActive_ZeroFailed()
    {
        string json = """{"services":[{"unit":"sshd.service","active_state":"active","sub_state":"running"},{"unit":"cron.service","active_state":"active","sub_state":"running"}]}""";

        ServiceStatusPayload? result = CreateUpdater().Deserialize<ServiceStatusPayload>(json);

        await Assert.That(result).IsNotNull();

        int total = result!.Services.Count;
        int failed = result.Services.Count(s =>
            string.Equals(s.ActiveState, "failed", StringComparison.OrdinalIgnoreCase));

        await Assert.That(total).IsEqualTo(2);
        await Assert.That(failed).IsEqualTo(0);
    }

    [Test]
    public async Task Deserialize_ServiceStatus_EmptyServices_ReturnsZeroCounts()
    {
        string json = """{"services":[]}""";

        ServiceStatusPayload? result = CreateUpdater().Deserialize<ServiceStatusPayload>(json);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Services.Count).IsEqualTo(0);
    }

    // ========== UpdateAsync with real SQLite — verifies DB persistence ==========

    [Test]
    public async Task UpdateAsync_SystemInfo_ValidPayload_PersistsHostnameAndCpuFields()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        string json = """{"hostname":"web-01","cpu_brand":"AMD Ryzen 9","cpu_physical_cores":16,"physical_memory":68719476736,"ip_addresses":["10.0.0.1","192.168.1.5"],"hardware_vendor":"Dell","hardware_model":"PowerEdge","hardware_serial":"SN123","uptime_seconds":86400,"bios_version":"2.1.0"}""";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await updater.UpdateAsync(1, TelemetryTypeIds.SystemInfo, json, now, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.Hostname).IsEqualTo("web-01");
        await Assert.That(state.CpuBrand).IsEqualTo("AMD Ryzen 9");
        await Assert.That(state.CpuCores).IsEqualTo(16);
        await Assert.That(state.MemoryTotalBytes).IsEqualTo(68719476736L);
    }

    [Test]
    public async Task UpdateAsync_SystemInfo_MultipleIps_SerializesAsJsonArray()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        string json = """{"hostname":"multi-ip","cpu_brand":"Intel","cpu_physical_cores":4,"physical_memory":8000000000,"ip_addresses":["10.0.0.1","172.16.0.5","192.168.1.100"]}""";

        await updater.UpdateAsync(1, TelemetryTypeIds.SystemInfo, json, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.IpAddresses).Contains("10.0.0.1");
        await Assert.That(state.IpAddresses).Contains("172.16.0.5");
        await Assert.That(state.IpAddresses).Contains("192.168.1.100");
    }

    [Test]
    public async Task UpdateAsync_OsVersion_ValidPayload_PersistsOsFields()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        string json = """{"name":"Ubuntu","version":"22.04","build":"5.15.0-91-generic"}""";

        await updater.UpdateAsync(1, TelemetryTypeIds.OsVersion, json, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.OsName).IsEqualTo("Ubuntu");
        await Assert.That(state.OsVersion).IsEqualTo("22.04");
        await Assert.That(state.Kernel).IsEqualTo("5.15.0-91-generic");
    }

    [Test]
    public async Task UpdateAsync_CpuUsage_ValidPayload_PersistsCpuPercent()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        string json = """{"cpu_usage_percent":75}""";

        await updater.UpdateAsync(1, TelemetryTypeIds.CpuUsage, json, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.CpuUsagePercent).IsEqualTo(75);
    }

    [Test]
    public async Task UpdateAsync_MemoryUsage_ValidPayload_PersistsMemoryFields()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        string json = """{"memory_total":16000000000,"memory_used":8000000000,"memory_usage_percent":50}""";

        await updater.UpdateAsync(1, TelemetryTypeIds.MemoryUsage, json, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.MemoryUsedBytes).IsEqualTo(8000000000L);
        await Assert.That(state.MemoryUsagePercent).IsEqualTo(50);
    }

    [Test]
    public async Task UpdateAsync_CpuInfo_ValidPayload_ParsesCoresAndPersists()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        string json = """{"processor_type":"x86_64","number_of_cores":"8","logical_processors":16}""";

        await updater.UpdateAsync(1, TelemetryTypeIds.CpuInfo, json, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.CpuType).IsEqualTo("x86_64");
        await Assert.That(state.CpuPhysicalCpus).IsEqualTo(8);
        await Assert.That(state.CpuLogicalCpus).IsEqualTo(16);
    }

    [Test]
    public async Task UpdateAsync_MemoryInfo_ValidPayload_PersistsSwapFields()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        string json = """{"memory_total":16000000000,"memory_free":4000000000,"memory_available":6000000000,"swap_total":2000000000,"swap_free":1500000000}""";

        await updater.UpdateAsync(1, TelemetryTypeIds.MemoryInfo, json, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.SwapTotalBytes).IsEqualTo(2000000000L);
        await Assert.That(state.SwapFreeBytes).IsEqualTo(1500000000L);
    }

    [Test]
    public async Task UpdateAsync_DiskUsage_RawJsonPassthrough_PersistsVerbatim()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        string json = """[{"mount":"/","used_percent":45}]""";

        await updater.UpdateAsync(1, TelemetryTypeIds.DiskUsage, json, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.DiskUsages).IsEqualTo(json);
    }

    [Test]
    public async Task UpdateAsync_HardwareHealth_RawJsonPassthrough_PersistsVerbatim()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        string json = """{"smart_status":"PASSED","temperature":42}""";

        await updater.UpdateAsync(1, TelemetryTypeIds.HardwareHealth, json, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.HardwareHealth).IsEqualTo(json);
    }

    [Test]
    public async Task UpdateAsync_PackageUpdates_CountsSecurityUpdates()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        string json = """{"package_manager":"apt","updates":[{"name":"openssl","current_version":"1.0","available_version":"1.1","is_security_update":true},{"name":"vim","current_version":"8.0","available_version":"8.1","is_security_update":false},{"name":"curl","current_version":"7.0","available_version":"7.1","is_security_update":true}]}""";

        await updater.UpdateAsync(1, TelemetryTypeIds.PackageUpdates, json, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.PendingUpdates).IsEqualTo(3);
        await Assert.That(state.SecurityUpdates).IsEqualTo(2);
    }

    [Test]
    public async Task UpdateAsync_ServiceStatus_CountsFailedServices()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        string json = """{"services":[{"unit":"sshd.service","active_state":"active","sub_state":"running"},{"unit":"nginx.service","active_state":"failed","sub_state":"dead"},{"unit":"cron.service","active_state":"active","sub_state":"running"}]}""";

        await updater.UpdateAsync(1, TelemetryTypeIds.ServiceStatus, json, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.TotalServices).IsEqualTo(3);
        await Assert.That(state.FailedServices).IsEqualTo(1);
    }

    [Test]
    public async Task UpdateAsync_SshSessions_SqliteMerge_MergesWithExisting()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);

        // Insert an initial SSH session.
        string first = """{"user":"root","timestamp":"2026-01-01T00:00:00Z"}""";
        await updater.UpdateAsync(1, TelemetryTypeIds.SshSessions, first, DateTimeOffset.UtcNow, CancellationToken.None);

        // Insert a second SSH session — should merge with existing.
        string second = """{"user":"admin","timestamp":"2026-01-02T00:00:00Z"}""";
        await updater.UpdateAsync(1, TelemetryTypeIds.SshSessions, second, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.SshSessions).Contains("root");
        await Assert.That(state.SshSessions).Contains("admin");
    }

    // ========== Edge/error cases with real DB ==========

    [Test]
    public async Task UpdateAsync_SystemInfo_EmptyIpList_PersistsNullIpJson()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        string json = """{"hostname":"no-ips","cpu_brand":"Intel","cpu_physical_cores":4,"physical_memory":8000000000,"ip_addresses":[]}""";

        await updater.UpdateAsync(1, TelemetryTypeIds.SystemInfo, json, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        // Empty IP list results in null IP JSON column.
        await Assert.That(state!.IpAddresses).IsNull();
    }

    [Test]
    public async Task UpdateAsync_CpuInfo_NonNumericCores_DefaultsToZero()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        string json = """{"processor_type":"ARM","number_of_cores":"N/A","logical_processors":4}""";

        await updater.UpdateAsync(1, TelemetryTypeIds.CpuInfo, json, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        // Non-parseable core count defaults to 0.
        await Assert.That(state!.CpuPhysicalCpus).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateAsync_ServiceStatus_AllFailed_CountsCorrectly()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        string json = """{"services":[{"unit":"svc1.service","active_state":"failed","sub_state":"dead"},{"unit":"svc2.service","active_state":"failed","sub_state":"dead"}]}""";

        await updater.UpdateAsync(1, TelemetryTypeIds.ServiceStatus, json, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.TotalServices).IsEqualTo(2);
        await Assert.That(state.FailedServices).IsEqualTo(2);
    }

    [Test]
    public async Task UpdateAsync_CpuUsage_NegativePercent_StoresAsIs()
    {
        using TestDatabaseFactory dbFactory = new();
        MachineStateUpdater updater = CreateUpdaterWithSqlite(dbFactory);
        // Out-of-range values are stored without validation.
        string json = """{"cpu_usage_percent":-5}""";

        await updater.UpdateAsync(1, TelemetryTypeIds.CpuUsage, json, DateTimeOffset.UtcNow, CancellationToken.None);

        MachineState? state = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == 1)
            .FirstOrDefaultAsync();

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.CpuUsagePercent).IsEqualTo(-5);
    }

    /// <summary>
    /// Creates a MachineStateUpdater with a real TestDatabaseFactory-backed scope.
    /// For types that need Deserialize to return null (invalid JSON), the method returns
    /// early before hitting db.ExecuteAsync, so no SQL is actually executed.
    /// </summary>
    private static (MachineStateUpdater Updater, TestDatabaseFactory DbFactory) CreateUpdaterWithDb()
    {
        TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<MachineStateUpdater> logger = new NullLogger<MachineStateUpdater>();
        ISqlDialect dialect = Substitute.For<ISqlDialect>();

        IServerSettingsCache settingsCache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        ServerConfigurationService configService = new(settingsCache, redis);
        MachineStateUpdater updater = new(scopeFactory, logger, dialect, configService);

        return (updater, dbFactory);
    }

    /// <summary>
    /// Creates a MachineStateUpdater with real SqliteSqlDialect for full DB persistence tests.
    /// </summary>
    private static MachineStateUpdater CreateUpdaterWithSqlite(TestDatabaseFactory dbFactory)
    {
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<MachineStateUpdater> logger = new NullLogger<MachineStateUpdater>();
        SqliteSqlDialect dialect = new();
        IServerSettingsCache settingsCache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        ServerConfigurationService configService = new(settingsCache, redis);
        MachineStateUpdater updater = new(scopeFactory, logger, dialect, configService);

        return updater;
    }
}
