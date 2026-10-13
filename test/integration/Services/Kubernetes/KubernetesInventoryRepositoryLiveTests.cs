// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FluentMigrator.Runner;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Framlux.FleetManagement.Test.Integration.Services.Kubernetes;

/// <summary>
/// Live tests of the Kubernetes inventory repository against real Postgres, for what SQLite cannot
/// show: that the projector's telemetry read can use the partial index over the Kubernetes types,
/// that applies for one tenant are serialised by the advisory lock, and that the dialect-specific
/// cursor upsert, the report resolution and garbage collection behave on the production database.
/// </summary>
/// <remarks>
/// The tests share one database and one cursor row, and garbage collection visits every tenant, so
/// they run one at a time.
/// </remarks>
[NotInParallel]
public sealed class KubernetesInventoryRepositoryLiveTests
{
    private const string PartialIndexName = "IX_MachineTelemetry_Kubernetes_Id";

    private const string FingerprintA = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";
    private const string FingerprintB = "0f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a6978";

    /// <summary>
    /// The most parameters, and the longest text, one statement may have while a large snapshot is
    /// applied: the repository puts at most 500 values in a list, and LinqToDB writes a list of the
    /// 11-character names these tests use into the text, so a chunk makes a statement of about 7,100
    /// characters while the whole 1,201-name list makes one of over 16,000.
    /// </summary>
    private const int LargestStatementParameters = 520;
    private const int LargestStatementLength = 8_000;

    /// <summary>The most values the repository puts in one <c>IN</c> list, which is its chunk size.</summary>
    private const int LargestInList = 500;

    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    /// <summary>Starts Postgres and runs migrations once for the class.</summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _migratedConnectionString = _fixture.ConnectionString;

        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(_migratedConnectionString)
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Warning));

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    /// <summary>Stops the container after the class.</summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task Live_TelemetryBatchQuery_UsesPartialIndex()
    {
        // Intent: the projector reads Kubernetes telemetry with the repository's own statement, not a
        // hand-written one, and that statement must be provable against the partial index. Thousands of
        // rows of other types and a handful of Kubernetes rows sit in one daily partition; without the
        // index the planner has nothing better than reading all of them, so the partition holding the
        // data must be read through an index descended from the partial index on the parent table.
        await using DatabaseContext seedDb = CreateContext();
        int tenantId = await SeedTenantAsync(seedDb);
        long machineId = await SeedMachineAsync(seedDb, tenantId);

        await using NpgsqlConnection connection = new(_migratedConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            INSERT INTO "MachineTelemetry"
                ("MachineId", "TenantId", "RetentionClass", "TelemetryType", "Payload", "ReceivedAt", "ServerReceivedAt", "SourceEventId")
            SELECT @machineId, @tenantId, 0, (g % 5) + 1, '{}', now(), now(), 'other-' || g
            FROM generate_series(1, 6000) AS g
            """,
            ("machineId", machineId), ("tenantId", tenantId));
        await ExecuteAsync(
            connection,
            """
            INSERT INTO "MachineTelemetry"
                ("MachineId", "TenantId", "RetentionClass", "TelemetryType", "Payload", "ReceivedAt", "ServerReceivedAt", "SourceEventId")
            SELECT @machineId, @tenantId, 0, 14, '{}', now(), now(), 'kubernetes-' || g
            FROM generate_series(1, 5) AS g
            """,
            ("machineId", machineId), ("tenantId", tenantId));
        await ExecuteAsync(connection, "ANALYZE \"MachineTelemetry\"");

        string loadedPartition = await ScalarAsync(
            connection,
            """
            SELECT c.relname FROM "MachineTelemetry" t JOIN pg_class c ON c.oid = t.tableoid
            WHERE t."TelemetryType" = 14 LIMIT 1
            """);

        (string statement, IReadOnlyList<MachineTelemetry> rows) = await CaptureBatchStatementAsync();
        string explain = await ScalarAsync(connection, $"EXPLAIN (FORMAT JSON) {statement}");

        List<(string Relation, string Index)> indexScans = [];
        List<string> sequentialScans = [];
        CollectScans(JsonDocument.Parse(explain).RootElement[0].GetProperty("Plan"), null, indexScans, sequentialScans);

        List<(string Relation, string Index)> loadedPartitionScans = indexScans.Where(scan => scan.Relation == loadedPartition).ToList();
        bool everyScanUsesPartialIndex = true;
        foreach ((string _, string indexName) in loadedPartitionScans)
        {
            List<string> ancestors = await IndexAncestorsAsync(connection, indexName);
            everyScanUsesPartialIndex = everyScanUsesPartialIndex && ancestors.Contains(PartialIndexName);
        }

        // The database is shared with the other tests of the class, which also store Kubernetes rows, so
        // the machine seeded here identifies its rows, and the thousands of other-type rows it seeded
        // must be absent whatever else the class has stored.
        List<MachineTelemetry> ownRows = rows.Where(r => r.MachineId == machineId).ToList();
        await Assert.That(ownRows.Count).IsEqualTo(5);
        await Assert.That(ownRows.All(r => r.TelemetryType == 14)).IsTrue();
        await Assert.That(rows.All(r => (r.TelemetryType == 14) || (r.TelemetryType == 15))).IsTrue();
        await Assert.That(loadedPartition).StartsWith("MachineTelemetry_Short_");
        await Assert.That(loadedPartitionScans.Count).IsGreaterThan(0);
        await Assert.That(everyScanUsesPartialIndex).IsTrue();
        await Assert.That(sequentialScans.Contains(loadedPartition)).IsFalse();
    }

    [Test]
    public async Task Live_OldestUnprojectedReceipt_ReadsOneRowThroughThePartialIndex()
    {
        // Intent: the lag gauge asks for the next unprojected row while the backlog may be at its
        // largest, so the statement must stop at the first row by id through the partial index over the
        // Kubernetes types, never aggregate the minimum receipt time over every unprojected row. Thousands
        // of rows of other types and a handful of Kubernetes rows sit in one daily partition; the
        // partition holding the data must be read through an index descended from the partial index,
        // and no aggregate may appear anywhere in the plan.
        await using DatabaseContext seedDb = CreateContext();
        int tenantId = await SeedTenantAsync(seedDb);
        long machineId = await SeedMachineAsync(seedDb, tenantId);
        try
        {
            long cursor = await seedDb.MachineTelemetry.MaxAsync(t => (long?)t.Id) ?? 0;

            await using NpgsqlConnection connection = new(_migratedConnectionString);
            await connection.OpenAsync();
            await ExecuteAsync(
                connection,
                """
                INSERT INTO "MachineTelemetry"
                    ("MachineId", "TenantId", "RetentionClass", "TelemetryType", "Payload", "ReceivedAt", "ServerReceivedAt", "SourceEventId")
                SELECT @machineId, @tenantId, 0, (g % 5) + 1, '{}', now(), now(), 'other-' || g
                FROM generate_series(1, 6000) AS g
                """,
                ("machineId", machineId), ("tenantId", tenantId));
            await ExecuteAsync(
                connection,
                """
                INSERT INTO "MachineTelemetry"
                    ("MachineId", "TenantId", "RetentionClass", "TelemetryType", "Payload", "ReceivedAt", "ServerReceivedAt", "SourceEventId")
                SELECT @machineId, @tenantId, 0, 14 + (g % 2), '{}', now(), now(), 'kubernetes-' || g
                FROM generate_series(1, 5) AS g
                """,
                ("machineId", machineId), ("tenantId", tenantId));
            await ExecuteAsync(connection, "ANALYZE \"MachineTelemetry\"");

            string loadedPartition = await ScalarAsync(
                connection,
                $"""
                SELECT c.relname FROM "MachineTelemetry" t JOIN pg_class c ON c.oid = t.tableoid
                WHERE t."TelemetryType" = 14 AND t."MachineId" = {machineId} LIMIT 1
                """);

            (string statement, DateTimeOffset? oldest) = await CaptureStatementAsync(repo => repo.GetOldestUnprojectedKubernetesReceiptAsync(
                cursor, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None));
            string explain = await ScalarAsync(connection, $"EXPLAIN (FORMAT JSON) {statement}");

            JsonElement plan = JsonDocument.Parse(explain).RootElement[0].GetProperty("Plan");
            List<(string Relation, string Index)> indexScans = [];
            List<string> sequentialScans = [];
            CollectScans(plan, null, indexScans, sequentialScans);

            List<(string Relation, string Index)> loadedPartitionScans = indexScans.Where(scan => scan.Relation == loadedPartition).ToList();
            bool everyScanUsesPartialIndex = true;
            foreach ((string _, string indexName) in loadedPartitionScans)
            {
                List<string> ancestors = await IndexAncestorsAsync(connection, indexName);
                everyScanUsesPartialIndex = everyScanUsesPartialIndex && ancestors.Contains(PartialIndexName);
            }

            await Assert.That(oldest).IsNotNull();
            await Assert.That(loadedPartition).StartsWith("MachineTelemetry_Short_");
            await Assert.That(loadedPartitionScans.Count).IsGreaterThan(0);
            await Assert.That(everyScanUsesPartialIndex).IsTrue();
            await Assert.That(sequentialScans.Contains(loadedPartition)).IsFalse();
            await Assert.That(ContainsNodeType(plan, "Aggregate")).IsFalse();
            await Assert.That(plan.GetProperty("Node Type").GetString()).IsEqualTo("Limit");
        }
        finally
        {
            await DeleteTelemetryAsync(seedDb, tenantId);
        }
    }

    [Test]
    public async Task Live_OldestUnprojectedReceipt_IsTheReceiptOfTheRowTheBatchReadReturnsFirst()
    {
        // Intent: lag counts only rows the projector will read, so on the production database the answer
        // must be the server receipt time of the first row the batch read returns past the same cursor,
        // whatever other rows sit before it: another type, one outside the read window, one not yet
        // visible. After the last readable row both reads come back empty.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        long machineId = await SeedMachineAsync(db, tenantId);
        try
        {
            DateTimeOffset now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset receivedAfter = now.AddDays(-8);
            DateTimeOffset visibleBefore = now.AddSeconds(-5);
            long cursor = await db.MachineTelemetry.MaxAsync(t => (long?)t.Id) ?? 0;

            async Task<long> InsertAsync(short type, DateTimeOffset receivedAt, DateTimeOffset serverReceivedAt)
            {
                return await db.InsertWithInt64IdentityAsync(new MachineTelemetry
                {
                    MachineId = machineId,
                    TenantId = tenantId,
                    TelemetryType = type,
                    Payload = "{}",
                    ReceivedAt = receivedAt,
                    ServerReceivedAt = serverReceivedAt,
                    SourceEventId = Guid.NewGuid().ToString("N"),
                });
            }

            await InsertAsync(6, now.AddMinutes(-9), now.AddMinutes(-9));
            await InsertAsync(14, receivedAfter, now.AddMinutes(-8));
            await InsertAsync(14, now.AddSeconds(-3), visibleBefore.AddSeconds(1));
            long node = await InsertAsync(14, now.AddMinutes(-2), now.AddMinutes(-2));
            long snapshot = await InsertAsync(15, now.AddSeconds(-30), now.AddSeconds(-30));

            DateTimeOffset? beforeAny = await repo.GetOldestUnprojectedKubernetesReceiptAsync(cursor, receivedAfter, visibleBefore, CancellationToken.None);
            DateTimeOffset? afterNode = await repo.GetOldestUnprojectedKubernetesReceiptAsync(node, receivedAfter, visibleBefore, CancellationToken.None);
            DateTimeOffset? afterSnapshot = await repo.GetOldestUnprojectedKubernetesReceiptAsync(snapshot, receivedAfter, visibleBefore, CancellationToken.None);
            IReadOnlyList<MachineTelemetry> firstBatch = await repo.GetKubernetesTelemetryBatchAsync(cursor, receivedAfter, visibleBefore, 1, CancellationToken.None);
            IReadOnlyList<MachineTelemetry> secondBatch = await repo.GetKubernetesTelemetryBatchAsync(node, receivedAfter, visibleBefore, 1, CancellationToken.None);
            IReadOnlyList<MachineTelemetry> thirdBatch = await repo.GetKubernetesTelemetryBatchAsync(snapshot, receivedAfter, visibleBefore, 1, CancellationToken.None);

            await Assert.That(beforeAny).IsEqualTo(now.AddMinutes(-2));
            await Assert.That(beforeAny).IsEqualTo(firstBatch[0].ServerReceivedAt);
            await Assert.That(afterNode).IsEqualTo(now.AddSeconds(-30));
            await Assert.That(afterNode).IsEqualTo(secondBatch[0].ServerReceivedAt);
            await Assert.That(afterSnapshot).IsNull();
            await Assert.That(thirdBatch.Count).IsEqualTo(0);
        }
        finally
        {
            await DeleteTelemetryAsync(db, tenantId);
        }
    }

    [Test]
    public async Task Live_TenantLock_SerialisesConcurrentApplies()
    {
        // Intent: two first reports from different machines of one tenant, carrying a fingerprint no
        // cluster holds yet, would each create a cluster if they could interleave. Both are held at the
        // tenant's Kubernetes lock until it is released; they then run one after the other, so exactly
        // one creates the cluster and the other joins it. The interleaving is forced by watching the
        // server's lock waits, never by sleeping.
        using CancellationTokenSource failsafe = new(TimeSpan.FromMinutes(2));
        await using DatabaseContext seedDb = CreateContext();
        int tenantId = await SeedTenantAsync(seedDb);
        long firstMachine = await SeedMachineAsync(seedDb, tenantId);
        long secondMachine = await SeedMachineAsync(seedDb, tenantId);
        DateTimeOffset at = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        string firstName = NewApplicationName("first");
        string secondName = NewApplicationName("second");

        await using DatabaseContext holderDb = CreateContext();
        DatabaseRepository holder = CreateRepo(holderDb);
        Task<LocalReportOutcome> first;
        Task<LocalReportOutcome> second;
        using (IDatabaseTransaction transaction = await holder.BeginTransactionAsync(CancellationToken.None))
        {
            await holder.AcquireKubernetesTenantLockAsync(tenantId, CancellationToken.None);

            first = ApplyOnNewConnectionAsync(
                firstName, new LocalNodeReport(1, tenantId, firstMachine, at, "k3s", "node-1", true, false, FingerprintA, "v1.30.4+k3s1", "certificate", false, null));
            bool firstBlocked = await PostgresLockProbe.WaitUntilBlockedAsync(
                _migratedConnectionString, firstName, first, failsafe.Token);

            second = ApplyOnNewConnectionAsync(
                secondName, new LocalNodeReport(2, tenantId, secondMachine, at, "k3s", "node-2", true, false, FingerprintA, "v1.30.4+k3s1", "certificate", false, null));
            bool secondBlocked = await PostgresLockProbe.WaitUntilBlockedAsync(
                _migratedConnectionString, secondName, second, failsafe.Token);

            await Assert.That(firstBlocked).IsTrue();
            await Assert.That(secondBlocked).IsTrue();
            await transaction.CommitAsync(CancellationToken.None);
        }

        LocalReportOutcome[] outcomes = await Task.WhenAll(first, second);

        await Assert.That(outcomes.Count(o => o == LocalReportOutcome.ClusterCreated)).IsEqualTo(1);
        await Assert.That(outcomes.Count(o => o == LocalReportOutcome.Grouped)).IsEqualTo(1);
        await Assert.That(await seedDb.KubernetesClusters.CountAsync(c => c.TenantId == tenantId)).IsEqualTo(1);
        await Assert.That(await seedDb.KubernetesNodes.CountAsync(n => n.TenantId == tenantId)).IsEqualTo(2);
    }

    [Test]
    public async Task Live_TenantLock_DoesNotBlockAnotherTenant()
    {
        // Intent: the lock is per tenant, so one tenant's long-running write never stalls the
        // projection of another tenant's reports.
        using CancellationTokenSource failsafe = new(TimeSpan.FromMinutes(2));
        await using DatabaseContext seedDb = CreateContext();
        int lockedTenant = await SeedTenantAsync(seedDb);
        int otherTenant = await SeedTenantAsync(seedDb);
        long otherMachine = await SeedMachineAsync(seedDb, otherTenant);
        DateTimeOffset at = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        await using DatabaseContext holderDb = CreateContext();
        DatabaseRepository holder = CreateRepo(holderDb);
        LocalReportOutcome outcome;
        using (IDatabaseTransaction transaction = await holder.BeginTransactionAsync(CancellationToken.None))
        {
            await holder.AcquireKubernetesTenantLockAsync(lockedTenant, CancellationToken.None);

            Task<LocalReportOutcome> other = ApplyOnNewConnectionAsync(
                NewApplicationName("other"),
                new LocalNodeReport(3, otherTenant, otherMachine, at, "k3s", "node-1", true, false, FingerprintA, null, "certificate", false, null));
            outcome = await other.WaitAsync(failsafe.Token);
        }

        await Assert.That(outcome).IsEqualTo(LocalReportOutcome.ClusterCreated);
    }

    [Test]
    public async Task Live_TenantLock_MakesGarbageCollectionWaitForTheHolder()
    {
        // Intent: garbage collection deletes under the same per-tenant lock as the projection, so
        // while another transaction holds a tenant's lock the collector must wait before touching that
        // tenant's rows, and only then remove them. The wait is observed in the server's lock table,
        // never by sleeping.
        using CancellationTokenSource failsafe = new(TimeSpan.FromMinutes(2));
        await using DatabaseContext seedDb = CreateContext();
        int tenantId = await SeedTenantAsync(seedDb);
        DateTimeOffset longAgo = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        long clusterId = await seedDb.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = FingerprintA,
            Distro = "k3s",
            FirstSeenAt = longAgo,
            LastSeenAt = longAgo,
        });
        await seedDb.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = "stale-node",
            LastLocalReportAt = longAgo,
        });
        string collectorName = NewApplicationName("collector");

        await using DatabaseContext holderDb = CreateContext();
        DatabaseRepository holder = CreateRepo(holderDb);
        Task<int> collect;
        using (IDatabaseTransaction transaction = await holder.BeginTransactionAsync(CancellationToken.None))
        {
            await holder.AcquireKubernetesTenantLockAsync(tenantId, CancellationToken.None);

            collect = Task.Run(async () =>
            {
                await using DatabaseContext collectorDb = CreateContext(collectorName);

                return await CreateRepo(collectorDb).CollectGarbageAsync(
                    new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);
            });
            bool blocked = await PostgresLockProbe.WaitUntilBlockedAsync(
                _migratedConnectionString, collectorName, collect, failsafe.Token);

            await Assert.That(blocked).IsTrue();
            await Assert.That(await seedDb.KubernetesNodes.CountAsync(n => n.TenantId == tenantId)).IsEqualTo(1);
            await transaction.CommitAsync(CancellationToken.None);
        }

        int deleted = await collect;

        // Collection visits every tenant of the shared database, so the total may include rows other
        // tests left; the tenant's own rows below pin what this test is about.
        await Assert.That(deleted).IsGreaterThanOrEqualTo(2);
        await Assert.That(await seedDb.KubernetesNodes.CountAsync(n => n.TenantId == tenantId)).IsEqualTo(0);
        await Assert.That(await seedDb.KubernetesClusters.CountAsync(c => c.TenantId == tenantId)).IsEqualTo(0);
    }

    [Test]
    public async Task Live_SoftDeleteMachine_WaitsForTheTenantLockThenUnlinksTheNode()
    {
        // Intent: soft-deleting a machine rewrites its node row, which is a Kubernetes write, so it
        // takes the tenant's lock inside the caller's transaction. While another transaction holds the
        // lock the delete must wait with the node still linked, and once the holder finishes the delete
        // completes and leaves the node unlinked. The wait is observed in the server's lock table,
        // never by sleeping.
        using CancellationTokenSource failsafe = new(TimeSpan.FromMinutes(2));
        await using DatabaseContext seedDb = CreateContext();
        int tenantId = await SeedTenantAsync(seedDb);
        long machineId = await SeedMachineAsync(seedDb, tenantId);
        long nodeId = await SeedLinkedNodeAsync(seedDb, tenantId, machineId);
        string deleterName = NewApplicationName("deleter");

        await using DatabaseContext holderDb = CreateContext();
        DatabaseRepository holder = CreateRepo(holderDb);
        Task<string?> delete;
        using (IDatabaseTransaction transaction = await holder.BeginTransactionAsync(CancellationToken.None))
        {
            await holder.AcquireKubernetesTenantLockAsync(tenantId, CancellationToken.None);

            delete = Task.Run(async () =>
            {
                await using DatabaseContext deleterDb = CreateContext(deleterName);
                DatabaseRepository deleter = CreateRepo(deleterDb);
                using IDatabaseTransaction deleteTransaction = await deleter.BeginTransactionAsync(CancellationToken.None);
                string? keyHash = await deleter.SoftDeleteMachineAsync(machineId, tenantId, 1, CancellationToken.None);
                await deleteTransaction.CommitAsync(CancellationToken.None);

                return keyHash;
            });
            bool blocked = await PostgresLockProbe.WaitUntilBlockedAsync(
                _migratedConnectionString, deleterName, delete, failsafe.Token);

            await Assert.That(blocked).IsTrue();
            await Assert.That((await seedDb.KubernetesNodes.SingleAsync(n => n.Id == nodeId)).MachineId).IsEqualTo(machineId);
            await transaction.CommitAsync(CancellationToken.None);
        }

        string? deletedKeyHash = await delete;

        await Assert.That(deletedKeyHash).IsNotNull();
        await Assert.That((await seedDb.KubernetesNodes.SingleAsync(n => n.Id == nodeId)).MachineId).IsNull();
        await Assert.That((await seedDb.Machines.SingleAsync(m => m.Id == machineId)).IsDeleted).IsTrue();
    }

    [Test]
    public async Task Live_RenameCluster_WaitsForTheTenantLockThenRenames()
    {
        // Intent: renaming a cluster is a Kubernetes write, so it takes the tenant's lock inside the
        // caller's transaction. While another transaction holds the lock the rename must wait with the
        // old name still in place, and once the holder finishes it completes. The wait is observed in
        // the server's lock table, never by sleeping.
        using CancellationTokenSource failsafe = new(TimeSpan.FromMinutes(2));
        await using DatabaseContext seedDb = CreateContext();
        int tenantId = await SeedTenantAsync(seedDb);
        long machineId = await SeedMachineAsync(seedDb, tenantId);
        long nodeId = await SeedLinkedNodeAsync(seedDb, tenantId, machineId);
        long clusterId = (await seedDb.KubernetesNodes.SingleAsync(n => n.Id == nodeId)).ClusterId;
        string renamerName = NewApplicationName("renamer");

        await using DatabaseContext holderDb = CreateContext();
        DatabaseRepository holder = CreateRepo(holderDb);
        Task<bool> rename;
        using (IDatabaseTransaction transaction = await holder.BeginTransactionAsync(CancellationToken.None))
        {
            await holder.AcquireKubernetesTenantLockAsync(tenantId, CancellationToken.None);

            rename = Task.Run(async () =>
            {
                await using DatabaseContext renamerDb = CreateContext(renamerName);
                DatabaseRepository renamer = CreateRepo(renamerDb);
                using IDatabaseTransaction renameTransaction = await renamer.BeginTransactionAsync(CancellationToken.None);
                bool renamed = await renamer.RenameClusterAsync(tenantId, clusterId, "Production", CancellationToken.None);
                await renameTransaction.CommitAsync(CancellationToken.None);

                return renamed;
            });
            bool blocked = await PostgresLockProbe.WaitUntilBlockedAsync(
                _migratedConnectionString, renamerName, rename, failsafe.Token);

            await Assert.That(blocked).IsTrue();
            await Assert.That((await seedDb.KubernetesClusters.SingleAsync(c => c.Id == clusterId)).DisplayName).IsNull();
            await transaction.CommitAsync(CancellationToken.None);
        }

        bool renamedAfterWait = await rename;

        await Assert.That(renamedAfterWait).IsTrue();
        await Assert.That((await seedDb.KubernetesClusters.SingleAsync(c => c.Id == clusterId)).DisplayName).IsEqualTo("Production");
    }

    [Test]
    public async Task Live_ClusterReads_ScopeToTheTenantAndJoinTheLinkedMachineName()
    {
        // Intent: the API's cluster reads run on the production database exactly as on SQLite: the
        // left join of a node to its machine yields the machine's name or null, and nothing of another
        // tenant's shows through however its cluster id was obtained.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        int otherTenantId = await SeedTenantAsync(db);
        long machineId = await SeedMachineAsync(db, tenantId);
        long otherMachineId = await SeedMachineAsync(db, otherTenantId);
        DateTimeOffset seenAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        long clusterId = await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = FingerprintA,
            Distro = "k3s",
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
        });
        long otherClusterId = await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = otherTenantId,
            CaFingerprint = FingerprintA,
            Distro = "k3s",
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
        });
        await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = "managed",
            MachineId = machineId,
            LastLocalReportAt = seenAt,
        });
        await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = "unmanaged",
            LastLocalReportAt = seenAt,
        });
        await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = otherTenantId,
            ClusterId = otherClusterId,
            NodeName = "foreign",
            MachineId = otherMachineId,
            LastLocalReportAt = seenAt,
        });

        IReadOnlyList<KubernetesNodeWithMachine> nodes = await repo.ListNodesAsync(tenantId, [clusterId, otherClusterId], CancellationToken.None);
        IReadOnlyList<KubernetesCluster> clusters = await repo.ListClustersAsync(tenantId, CancellationToken.None);
        IReadOnlyList<KubernetesCluster> byFingerprint = await repo.ListClustersByFingerprintAsync(tenantId, FingerprintA, CancellationToken.None);
        KubernetesCluster? foreign = await repo.GetClusterAsync(tenantId, otherClusterId, CancellationToken.None);
        KubernetesNode? foreignNode = await repo.GetNodeForMachineAsync(tenantId, otherMachineId, CancellationToken.None);
        KubernetesNode? ownNode = await repo.GetNodeForMachineAsync(tenantId, machineId, CancellationToken.None);

        await Assert.That(nodes.Count).IsEqualTo(2);
        await Assert.That(nodes.Single(n => n.Node.NodeName == "managed").MachineName).IsEqualTo("kubernetes-live-machine");
        await Assert.That(nodes.Single(n => n.Node.NodeName == "unmanaged").MachineName).IsNull();
        await Assert.That(clusters.Count).IsEqualTo(1);
        await Assert.That(byFingerprint.Count).IsEqualTo(1);
        await Assert.That(byFingerprint[0].Id).IsEqualTo(clusterId);
        await Assert.That(foreign).IsNull();
        await Assert.That(foreignNode).IsNull();
        await Assert.That(ownNode).IsNotNull();
    }

    [Test]
    public async Task Live_LatestNodePayloads_AnswersForSeveralMachinesInOneQueryAndScopesToTheTenant()
    {
        // Intent: the latest-report read runs on the production database as one statement with a
        // per-machine lookup over an array of machine ids. It returns each asked-for machine's newest
        // type-14 payload by server receipt time, and ignores other types, machines nobody asked for,
        // rows outside the window, and another tenant's rows that carry an asked-for machine id.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        int otherTenantId = await SeedTenantAsync(db);
        long first = await SeedMachineAsync(db, tenantId);
        long second = await SeedMachineAsync(db, tenantId);
        long silent = await SeedMachineAsync(db, tenantId);
        long notAsked = await SeedMachineAsync(db, tenantId);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        async Task InsertAsync(int rowTenantId, long machineId, short type, string payload, TimeSpan age)
        {
            DateTimeOffset at = now - age;
            await db.InsertWithInt64IdentityAsync(new MachineTelemetry
            {
                MachineId = machineId,
                TenantId = rowTenantId,
                TelemetryType = type,
                Payload = payload,
                ReceivedAt = at,
                ServerReceivedAt = at,
                SourceEventId = Guid.NewGuid().ToString("N"),
            });
        }

        await InsertAsync(tenantId, first, 14, "first-older", TimeSpan.FromMinutes(30));
        await InsertAsync(tenantId, first, 14, "first-newest", TimeSpan.FromMinutes(1));
        await InsertAsync(tenantId, first, 15, "first-snapshot", TimeSpan.FromSeconds(10));
        await InsertAsync(tenantId, second, 14, "second-only", TimeSpan.FromMinutes(10));
        await InsertAsync(otherTenantId, second, 14, "foreign-newer", TimeSpan.FromSeconds(5));
        await InsertAsync(tenantId, notAsked, 14, "not-asked", TimeSpan.FromMinutes(2));
        await InsertAsync(tenantId, silent, 1, "cpu", TimeSpan.FromMinutes(2));

        IReadOnlyDictionary<long, string> payloads = await repo.GetLatestKubernetesNodePayloadsAsync(
            tenantId, [first, second, silent], now.AddDays(-7), CancellationToken.None);
        IReadOnlyDictionary<long, string> narrowWindow = await repo.GetLatestKubernetesNodePayloadsAsync(
            tenantId, [first, second, silent], now.AddMinutes(-5), CancellationToken.None);
        IReadOnlyDictionary<long, string> none = await repo.GetLatestKubernetesNodePayloadsAsync(
            tenantId, [], now.AddDays(-7), CancellationToken.None);

        await Assert.That(payloads.Count).IsEqualTo(2);
        await Assert.That(payloads[first]).IsEqualTo("first-newest");
        await Assert.That(payloads[second]).IsEqualTo("second-only");
        await Assert.That(narrowWindow.Count).IsEqualTo(1);
        await Assert.That(narrowWindow[first]).IsEqualTo("first-newest");
        await Assert.That(none.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Live_LatestNodePayloads_FollowTheServersClockAndBreakTiesByRowId()
    {
        // Intent: on the production database the window and the ordering use the server's receipt time,
        // never the agent-derived ReceivedAt, which only bounds the partitions read. A slow agent clock
        // (old ReceivedAt, fresh ServerReceivedAt) is found, a fast one (fresh ReceivedAt, old
        // ServerReceivedAt) is not, and two reports accepted at the same instant resolve to the later row.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        long slowClock = await SeedMachineAsync(db, tenantId);
        long fastClock = await SeedMachineAsync(db, tenantId);
        long tie = await SeedMachineAsync(db, tenantId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset serverReceivedAfter = now.AddMinutes(-5);

        async Task InsertAsync(long machineId, string payload, DateTimeOffset receivedAt, DateTimeOffset serverReceivedAt)
        {
            await db.InsertWithInt64IdentityAsync(new MachineTelemetry
            {
                MachineId = machineId,
                TenantId = tenantId,
                TelemetryType = 14,
                Payload = payload,
                ReceivedAt = receivedAt,
                ServerReceivedAt = serverReceivedAt,
                SourceEventId = Guid.NewGuid().ToString("N"),
            });
        }

        await InsertAsync(slowClock, "slow-clock-report", now.AddDays(-3), now.AddMinutes(-1));
        await InsertAsync(fastClock, "fast-clock-report", now, now.AddMinutes(-10));
        DateTimeOffset tied = now.AddMinutes(-2);
        await InsertAsync(tie, "tie-stored-first", now.AddMinutes(-2), tied);
        await InsertAsync(tie, "tie-stored-last", now.AddMinutes(-4), tied);

        IReadOnlyDictionary<long, string> payloads = await repo.GetLatestKubernetesNodePayloadsAsync(
            tenantId, [slowClock, fastClock, tie], serverReceivedAfter, CancellationToken.None);

        await Assert.That(payloads.Count).IsEqualTo(2);
        await Assert.That(payloads[slowClock]).IsEqualTo("slow-clock-report");
        await Assert.That(payloads.ContainsKey(fastClock)).IsFalse();
        await Assert.That(payloads[tie]).IsEqualTo("tie-stored-last");
    }

    [Test]
    public async Task Live_LatestNodePayloads_MachineWithManyReports_ReturnsTheNewestRowAndBreaksTheTieByRowId()
    {
        // Intent: a machine that has reported for days has hundreds of type-14 rows. The read must still
        // answer with the one the server accepted last, and when several were accepted at that same
        // instant the row stored last wins, whatever the agent's own clock says about them.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        try
        {
            long other = await SeedMachineAsync(db, tenantId);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            SeededReports seeded = await SeedManyReportsAsync(db, tenantId, now);
            await db.InsertWithInt64IdentityAsync(new MachineTelemetry
            {
                MachineId = other,
                TenantId = tenantId,
                TelemetryType = 14,
                Payload = "other-machine-report",
                ReceivedAt = now.AddMinutes(-3),
                ServerReceivedAt = now.AddMinutes(-3),
                SourceEventId = Guid.NewGuid().ToString("N"),
            });

            IReadOnlyDictionary<long, string> payloads = await repo.GetLatestKubernetesNodePayloadsAsync(
                tenantId, [seeded.MachineId, other], now.AddDays(-7), CancellationToken.None);

            await Assert.That(payloads.Count).IsEqualTo(2);
            await Assert.That(payloads[seeded.MachineId]).IsEqualTo(seeded.NewestPayload);
            await Assert.That(payloads[other]).IsEqualTo("other-machine-report");
        }
        finally
        {
            await DeleteTelemetryAsync(db, tenantId);
        }
    }

    [Test]
    public async Task Live_LatestNodePayloads_DuplicateMachineIds_AreAskedAboutOnce()
    {
        // Intent: the statement looks up each element of the id array, so an id given twice would come
        // back twice and the keyed result could not be built. A caller that lists a machine twice still
        // gets its one latest report.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        try
        {
            long machineId = await SeedMachineAsync(db, tenantId);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach ((string payload, int minutesAgo) in new[] { ("older", 20), ("newer", 2) })
            {
                await db.InsertWithInt64IdentityAsync(new MachineTelemetry
                {
                    MachineId = machineId,
                    TenantId = tenantId,
                    TelemetryType = 14,
                    Payload = payload,
                    ReceivedAt = now.AddMinutes(-minutesAgo),
                    ServerReceivedAt = now.AddMinutes(-minutesAgo),
                    SourceEventId = Guid.NewGuid().ToString("N"),
                });
            }

            IReadOnlyDictionary<long, string> payloads = await repo.GetLatestKubernetesNodePayloadsAsync(
                tenantId, [machineId, machineId], now.AddDays(-7), CancellationToken.None);

            await Assert.That(payloads.Count).IsEqualTo(1);
            await Assert.That(payloads[machineId]).IsEqualTo("newer");
        }
        finally
        {
            await DeleteTelemetryAsync(db, tenantId);
        }
    }

    [Test]
    public async Task Live_LatestNodePayloads_PlanReadsAboutOneRowPerPartitionInsteadOfEveryReport()
    {
        // Intent: the cluster detail asks for every active machine of the tenant on every view, so the
        // statement must cost about one row per machine and partition, not one per report the machine has
        // ever sent in the window. Two hundred reports sit in the machine's partitions; the leaf scans
        // may produce only the newest row of each partition, the rows tied with it and the one row that
        // shows the tie has ended. The plan is the one the production statement gets, read back with
        // EXPLAIN ANALYZE, and the rows are what the scans really produced.
        await using DatabaseContext db = CreateContext();
        int tenantId = await SeedTenantAsync(db);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            SeededReports seeded = await SeedManyReportsAsync(db, tenantId, now);

            await using NpgsqlConnection connection = new(_migratedConnectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, "ANALYZE \"MachineTelemetry\"");

            string statement = await CaptureLatestNodePayloadsStatementAsync(tenantId, [seeded.MachineId], now.AddDays(-7));
            string explain = await ScalarAsync(connection, $"EXPLAIN (ANALYZE, FORMAT JSON) {statement}");
            JsonElement plan = JsonDocument.Parse(explain).RootElement[0].GetProperty("Plan");

            double rowsProduced = SumLeafScanRows(plan);

            await Assert.That(seeded.RowCount).IsEqualTo(200);
            await Assert.That(rowsProduced).IsGreaterThan(0);
            await Assert.That(rowsProduced).IsLessThan(50);
        }
        finally
        {
            await DeleteTelemetryAsync(db, tenantId);
        }
    }

    [Test]
    public async Task Live_CandidateForMachine_ScopesToTheTenant()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        int otherTenantId = await SeedTenantAsync(db);
        long machineId = await SeedMachineAsync(db, tenantId);
        long clusterId = await SeedLiveClusterAsync(db, tenantId, NewFingerprint());
        await SeedLiveCandidateAsync(db, tenantId, clusterId, machineId, DateTimeOffset.UtcNow);

        KubernetesReporterCandidate? own = await repo.GetCandidateForMachineAsync(tenantId, machineId, CancellationToken.None);
        KubernetesReporterCandidate? foreign = await repo.GetCandidateForMachineAsync(otherTenantId, machineId, CancellationToken.None);

        await Assert.That(own).IsNotNull();
        await Assert.That(own!.ClusterId).IsEqualTo(clusterId);
        await Assert.That(foreign).IsNull();
    }

    [Test]
    public async Task Live_ListCandidatesAndMachineNames_ScopeToTheTenant()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        int otherTenantId = await SeedTenantAsync(db);
        long machineA = await SeedMachineAsync(db, tenantId);
        long machineB = await SeedMachineAsync(db, tenantId);
        long foreignMachine = await SeedMachineAsync(db, otherTenantId);
        long first = await SeedLiveClusterAsync(db, tenantId, NewFingerprint());
        long second = await SeedLiveClusterAsync(db, tenantId, NewFingerprint());
        long foreignCluster = await SeedLiveClusterAsync(db, otherTenantId, NewFingerprint());
        await SeedLiveCandidateAsync(db, tenantId, second, machineB, DateTimeOffset.UtcNow);
        await SeedLiveCandidateAsync(db, tenantId, first, machineA, DateTimeOffset.UtcNow);
        await SeedLiveCandidateAsync(db, otherTenantId, foreignCluster, foreignMachine, DateTimeOffset.UtcNow);

        IReadOnlyList<KubernetesReporterCandidate> candidates = await repo.ListCandidatesAsync(
            tenantId, [first, second, foreignCluster], CancellationToken.None);
        IReadOnlyDictionary<long, string> names = await repo.GetMachineNamesInTenantAsync(
            tenantId, [machineA, machineB, foreignMachine], CancellationToken.None);

        await Assert.That(candidates.Select(c => (c.ClusterId, c.MachineId)).ToArray())
            .IsEquivalentTo(new[] { (first, machineA), (second, machineB) });
        await Assert.That(candidates[0].ClusterId).IsEqualTo(first);
        await Assert.That(names.Keys.Order().ToArray()).IsEquivalentTo(new[] { machineA, machineB }.Order().ToArray());
    }

    [Test]
    public async Task Live_MachineIdsLinkedToOtherClusters_FindNodesAndCandidatesOfAnotherClusterOnPostgres()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        int otherTenantId = await SeedTenantAsync(db);
        try
        {
            string sharedCa = NewFingerprint();
            long own = await SeedLiveClusterAsync(db, tenantId, sharedCa);
            long sibling = await SeedLiveClusterAsync(db, tenantId, sharedCa);
            long foreignCluster = await SeedLiveClusterAsync(db, otherTenantId, sharedCa);
            long nodeElsewhere = await SeedMachineAsync(db, tenantId);
            long candidateElsewhere = await SeedMachineAsync(db, tenantId);
            long candidateHere = await SeedMachineAsync(db, tenantId);
            long foreignLinked = await SeedMachineAsync(db, tenantId);
            await db.InsertWithInt64IdentityAsync(new KubernetesNode
            {
                TenantId = tenantId,
                ClusterId = sibling,
                NodeName = "sibling-node",
                MachineId = nodeElsewhere,
            });
            await SeedLiveCandidateAsync(db, tenantId, sibling, candidateElsewhere, DateTimeOffset.UtcNow);
            await SeedLiveCandidateAsync(db, tenantId, own, candidateHere, DateTimeOffset.UtcNow);
            await SeedLiveCandidateAsync(db, otherTenantId, foreignCluster, foreignLinked, DateTimeOffset.UtcNow);

            IReadOnlyCollection<long> linked = await repo.GetMachineIdsLinkedToOtherClustersAsync(tenantId, own, CancellationToken.None);

            await Assert.That(linked.Order().ToArray()).IsEquivalentTo(new[] { nodeElsewhere, candidateElsewhere }.Order().ToArray());
        }
        finally
        {
            // The node above has no report time, which garbage collection treats as stale, and it visits
            // every tenant of the shared database, so a failed assertion must not leave the row behind for
            // the collection test to count.
            await CleanUpKubernetesRowsAsync(db, tenantId);
            await CleanUpKubernetesRowsAsync(db, otherTenantId);
        }
    }

    [Test]
    public async Task Live_LatestTelemetryPerType_LeavesOutTheExcludedTypesOnPostgres()
    {
        // Intent: the machine page's read must not load the cluster snapshot on the production database
        // either, where the per-type grouping is a window function over the partitioned table.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        long machineId = await SeedMachineAsync(db, tenantId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (short type in new short[] { 1, 14, 15 })
        {
            await db.InsertWithInt64IdentityAsync(new MachineTelemetry
            {
                MachineId = machineId,
                TenantId = tenantId,
                TelemetryType = type,
                Payload = "{}",
                ReceivedAt = now.AddMinutes(-2),
                ServerReceivedAt = now.AddMinutes(-2),
                SourceEventId = Guid.NewGuid().ToString("N"),
            });
        }

        Dictionary<short, MachineTelemetry> everything = await repo.GetLatestTelemetryPerTypeAsync(machineId, 7, null, CancellationToken.None);
        Dictionary<short, MachineTelemetry> withoutSnapshot = await repo.GetLatestTelemetryPerTypeAsync(machineId, 7, [15], CancellationToken.None);

        await Assert.That(everything.Keys.Order().ToArray()).IsEquivalentTo(new short[] { 1, 14, 15 });
        await Assert.That(withoutSnapshot.Keys.Order().ToArray()).IsEquivalentTo(new short[] { 1, 14 });
    }

    [Test]
    public async Task Live_ActiveSigningKeyIdsForMachines_CountOnlyUnrevokedKeysOfTheTenantOnPostgres()
    {
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        int otherTenantId = await SeedTenantAsync(db);
        long machineA = await SeedMachineAsync(db, tenantId);
        long machineB = await SeedMachineAsync(db, tenantId);
        long foreignMachine = await SeedMachineAsync(db, otherTenantId);

        async Task<int> KeyAsync(int keyTenantId, DateTimeOffset? revokedAt = null)
        {
            return await db.InsertWithInt32IdentityAsync(new UserSigningKey
            {
                UserId = 1,
                TenantId = keyTenantId,
                Label = "Live Key",
                PublicKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
                PublicKeyFingerprint = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
                RevokedAt = revokedAt,
            });
        }

        async Task AuthorizeAsync(long machine, int key, int authTenantId, DateTimeOffset? revokedAt = null)
        {
            await db.InsertWithInt32IdentityAsync(new MachineAuthorizedKey
            {
                MachineId = machine,
                SigningKeyId = key,
                TenantId = authTenantId,
                AuthorizedAt = DateTimeOffset.UtcNow,
                AuthorizedByUserId = 1,
                RevokedAt = revokedAt,
            });
        }

        int active = await KeyAsync(tenantId);
        int revokedKey = await KeyAsync(tenantId, DateTimeOffset.UtcNow);
        int revokedAuthorization = await KeyAsync(tenantId);
        int foreignKey = await KeyAsync(otherTenantId);
        await AuthorizeAsync(machineA, active, tenantId);
        await AuthorizeAsync(machineA, revokedKey, tenantId);
        await AuthorizeAsync(machineB, revokedAuthorization, tenantId, DateTimeOffset.UtcNow);
        await AuthorizeAsync(foreignMachine, foreignKey, otherTenantId);

        IReadOnlyDictionary<long, IReadOnlyList<int>> result = await repo.GetActiveSigningKeyIdsForMachinesAsync(
            tenantId, [machineA, machineB, foreignMachine], CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[machineA].ToArray()).IsEquivalentTo(new[] { active });
    }

    [Test]
    public async Task Live_TenantPurge_WaitsForTheTenantLockThenRemovesTheKubernetesRows()
    {
        // Intent: the purge deletes a tenant's Kubernetes rows under the same per-tenant lock as the
        // projection, so a report being applied cannot recreate a node between the purge's deletes. While
        // another transaction holds the lock the purge must wait with the rows still present, and once the
        // holder finishes the purge removes the nodes, the clusters and the machines. The wait is observed
        // in the server's lock table, never by sleeping.
        using CancellationTokenSource failsafe = new(TimeSpan.FromMinutes(2));
        await using DatabaseContext seedDb = CreateContext();
        int tenantId = await SeedTenantAsync(seedDb);
        long machineId = await SeedMachineAsync(seedDb, tenantId);
        await SeedLinkedNodeAsync(seedDb, tenantId, machineId);
        string purgerName = NewApplicationName("purger");

        await using DatabaseContext holderDb = CreateContext();
        DatabaseRepository holder = CreateRepo(holderDb);
        Task purge;
        using (IDatabaseTransaction transaction = await holder.BeginTransactionAsync(CancellationToken.None))
        {
            await holder.AcquireKubernetesTenantLockAsync(tenantId, CancellationToken.None);

            purge = Task.Run(async () =>
            {
                await using DatabaseContext purgerDb = CreateContext(purgerName);
                DatabaseRepository purger = CreateRepo(purgerDb);
                using IDatabaseTransaction purgeTransaction = await purger.BeginTransactionAsync(CancellationToken.None);
                await purger.PurgeTenantOperationalDataAsync(tenantId, CancellationToken.None);
                await purgeTransaction.CommitAsync(CancellationToken.None);
            });
            bool blocked = await PostgresLockProbe.WaitUntilBlockedAsync(
                _migratedConnectionString, purgerName, purge, failsafe.Token);

            await Assert.That(blocked).IsTrue();
            await Assert.That(await seedDb.KubernetesNodes.CountAsync(n => n.TenantId == tenantId)).IsEqualTo(1);
            await transaction.CommitAsync(CancellationToken.None);
        }

        await purge;

        await Assert.That(await seedDb.KubernetesNodes.CountAsync(n => n.TenantId == tenantId)).IsEqualTo(0);
        await Assert.That(await seedDb.KubernetesClusters.CountAsync(c => c.TenantId == tenantId)).IsEqualTo(0);
        await Assert.That(await seedDb.Machines.CountAsync(m => m.TenantId == tenantId)).IsEqualTo(0);
    }

    [Test]
    public async Task Live_CursorUpsert_CreatesAdvancesAndNeverMovesBackwards()
    {
        // Intent: the Postgres cursor upsert creates the single row on first use, advances it, and
        // keeps the larger position when a replica that lost the projection lock writes a lower one,
        // leaving the timestamp of the last real advance untouched.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        await db.KubernetesInventoryProjectionCursors.DeleteAsync();
        DateTimeOffset first = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        await repo.SetProjectionCursorAsync(1_000_000_000_000, first, CancellationToken.None);
        await repo.SetProjectionCursorAsync(2_000_000_000_000, first.AddMinutes(1), CancellationToken.None);
        await repo.SetProjectionCursorAsync(1_500_000_000_000, first.AddMinutes(2), CancellationToken.None);

        KubernetesInventoryProjectionCursor row = await db.KubernetesInventoryProjectionCursors.SingleAsync();
        await Assert.That(row.Id).IsEqualTo(1);
        await Assert.That(row.Position).IsEqualTo(2_000_000_000_000);
        await Assert.That(row.UpdatedAt).IsEqualTo(first.AddMinutes(1));
        await Assert.That(await repo.GetProjectionCursorAsync(CancellationToken.None)).IsEqualTo(2_000_000_000_000);
    }

    [Test]
    public async Task Live_ApplyThenCollectGarbage_RunsTheWholeAlgorithmOnPostgres()
    {
        // Intent: the resolution rules, which the SQLite tests pin down branch by branch, also execute
        // correctly on the production database: grouping, a node moving between machines under the
        // unique machine link, a finished CA rotation re-keying the cluster, and garbage collection
        // removing the stale node rows and then the empty cluster.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        long m1 = await SeedMachineAsync(db, tenantId);
        long m2 = await SeedMachineAsync(db, tenantId);
        long m3 = await SeedMachineAsync(db, tenantId);
        DateTimeOffset t0 = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        LocalNodeReport Report(long telemetryId, long machineId, DateTimeOffset at, string nodeName, string fingerprint)
        {
            return new LocalNodeReport(telemetryId, tenantId, machineId, at, "k3s", nodeName, true, false, fingerprint, "v1.30.4+k3s1", "certificate", false, null);
        }

        LocalReportOutcome created = await repo.ApplyLocalNodeReportAsync(Report(1, m1, t0, "node-1", FingerprintA), t0, CancellationToken.None);
        LocalReportOutcome grouped = await repo.ApplyLocalNodeReportAsync(Report(2, m2, t0, "node-2", FingerprintA), t0, CancellationToken.None);
        LocalReportOutcome moved = await repo.ApplyLocalNodeReportAsync(Report(3, m3, t0.AddMinutes(1), "node-1", FingerprintA), t0, CancellationToken.None);

        await Assert.That(created).IsEqualTo(LocalReportOutcome.ClusterCreated);
        await Assert.That(grouped).IsEqualTo(LocalReportOutcome.Grouped);
        await Assert.That(moved).IsEqualTo(LocalReportOutcome.Grouped);
        await Assert.That(await db.KubernetesNodes.CountAsync(n => n.MachineId == m1)).IsEqualTo(0);
        await Assert.That((await db.KubernetesNodes.SingleAsync(n => n.MachineId == m3)).NodeName).IsEqualTo("node-1");

        await repo.ApplyLocalNodeReportAsync(Report(4, m2, t0.AddMinutes(10), "node-2", FingerprintB), t0, CancellationToken.None);
        await Assert.That((await db.KubernetesClusters.SingleAsync(c => c.TenantId == tenantId)).CaFingerprint).IsEqualTo(FingerprintA);
        await repo.ApplyLocalNodeReportAsync(Report(5, m3, t0.AddMinutes(20), "node-1", FingerprintB), t0, CancellationToken.None);

        KubernetesCluster cluster = await db.KubernetesClusters.SingleAsync(c => c.TenantId == tenantId);
        await Assert.That(cluster.CaFingerprint).IsEqualTo(FingerprintB);
        await Assert.That(await db.KubernetesNodes.CountAsync(n => (n.TenantId == tenantId) && n.FingerprintMismatch)).IsEqualTo(0);
        await Assert.That(cluster.LastSeenAt).IsEqualTo(t0.AddMinutes(20));

        int deleted = await repo.CollectGarbageAsync(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        // Collection visits every tenant of the shared database, so the total may include rows other
        // tests left; the tenant's own rows below pin what this test is about.
        await Assert.That(deleted).IsGreaterThanOrEqualTo(3);
        await Assert.That(await db.KubernetesClusters.CountAsync(c => c.TenantId == tenantId)).IsEqualTo(0);
        await Assert.That(await db.KubernetesNodes.CountAsync(n => n.TenantId == tenantId)).IsEqualTo(0);
    }

    [Test]
    public async Task Live_ReporterCandidates_FollowTheirMachinesAcrossClustersAndHostnameFallbacksReconcile()
    {
        // Intent: the candidate rules execute on the production database, where moving a candidate
        // rewrites a primary key column and the machine-id match runs as SQL: an enrolled node becomes a
        // candidate, an enrolled host with no kubelet becomes a candidate without a node, a node that
        // moves clusters takes its candidate along, a candidate stops being one when the machine says
        // it is not enrolled, and a hostname-fallback report merges into the reporter's row.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        long worker = await SeedMachineAsync(db, tenantId);
        long controller = await SeedMachineAsync(db, tenantId);
        long hostnameMachine = await SeedMachineAsync(db, tenantId);
        DateTimeOffset t0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        LocalNodeReport Report(
            long telemetryId, long machineId, DateTimeOffset at, string nodeName, string fingerprint, bool hasKubelet = true,
            string nodeNameSource = "certificate", bool enrolled = true, string? reporterError = null)
        {
            return new LocalNodeReport(
                telemetryId, tenantId, machineId, at, "k3s", nodeName, hasKubelet, false, fingerprint, "v1.30.4+k3s1",
                nodeNameSource, enrolled, reporterError);
        }

        LocalReportOutcome created = await repo.ApplyLocalNodeReportAsync(
            Report(1, worker, t0, "worker-1", FingerprintA, reporterError: "credentials_rejected"), t0, CancellationToken.None);
        long clusterA = (await db.KubernetesClusters.SingleAsync(c => c.TenantId == tenantId)).Id;
        LocalReportOutcome candidateOnly = await repo.ApplyLocalNodeReportAsync(
            Report(2, controller, t0.AddMinutes(1), string.Empty, FingerprintA, hasKubelet: false), t0, CancellationToken.None);

        await Assert.That(created).IsEqualTo(LocalReportOutcome.ClusterCreated);
        await Assert.That(candidateOnly).IsEqualTo(LocalReportOutcome.CandidateOnly);
        await Assert.That(await db.KubernetesNodes.CountAsync(n => n.TenantId == tenantId)).IsEqualTo(1);
        List<KubernetesReporterCandidate> candidates = await db.KubernetesReporterCandidates
            .Where(c => c.TenantId == tenantId).OrderBy(c => c.MachineId).ToListAsync();
        await Assert.That(candidates.Select(c => c.MachineId).ToList()).IsEquivalentTo(new List<long> { worker, controller });
        await Assert.That(candidates.Single(c => c.MachineId == worker).ReporterError).IsEqualTo("credentials_rejected");

        long clusterB = await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = FingerprintB,
            Distro = "k3s",
            FirstSeenAt = t0,
            LastSeenAt = t0,
        });
        await repo.ApplyLocalNodeReportAsync(
            Report(3, worker, t0.AddMinutes(15), "worker-1", FingerprintB), t0, CancellationToken.None);

        await Assert.That((await db.KubernetesNodes.SingleAsync(n => n.MachineId == worker)).ClusterId).IsEqualTo(clusterB);
        KubernetesReporterCandidate moved = await db.KubernetesReporterCandidates.SingleAsync(c => c.MachineId == worker);
        await Assert.That(moved.ClusterId).IsEqualTo(clusterB);
        await Assert.That(moved.ReporterError).IsNull();
        await Assert.That((await db.KubernetesReporterCandidates.SingleAsync(c => c.MachineId == controller)).ClusterId).IsEqualTo(clusterA);

        await repo.ApplyLocalNodeReportAsync(
            Report(4, worker, t0.AddMinutes(30), "worker-1", FingerprintB, enrolled: false), t0, CancellationToken.None);

        await Assert.That(await db.KubernetesReporterCandidates.CountAsync(c => c.MachineId == worker)).IsEqualTo(0);

        string systemId = (await db.Machines.SingleAsync(m => m.Id == hostnameMachine)).SystemId;
        long reporterRow = await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterA,
            NodeName = "cp-1.example.com",
            NodeMachineId = systemId.ToUpperInvariant(),
            LastReporterSeenAt = t0,
            Roles = "[\"control-plane\"]",
        });
        LocalReportOutcome reconciled = await repo.ApplyLocalNodeReportAsync(
            Report(5, hostnameMachine, t0.AddMinutes(40), "host-a", FingerprintA, nodeNameSource: "hostname", enrolled: false),
            t0, CancellationToken.None);

        await Assert.That(reconciled).IsEqualTo(LocalReportOutcome.Grouped);
        KubernetesNode merged = await db.KubernetesNodes.SingleAsync(n => n.MachineId == hostnameMachine);
        await Assert.That(merged.Id).IsEqualTo(reporterRow);
        await Assert.That(merged.NodeName).IsEqualTo("cp-1.example.com");
        await Assert.That(await db.KubernetesNodes.CountAsync(n => (n.TenantId == tenantId) && (n.NodeName == "host-a"))).IsEqualTo(0);
    }

    [Test]
    public async Task Live_GarbageCollection_AppliesTheReportingRulesOnPostgres()
    {
        // Intent: the reporting rules of garbage collection run as set-based SQL on Postgres: an expired
        // candidate is removed, a cluster with no candidate and a stale snapshot goes back to its local
        // state with its Lease epoch cleared (including the node no machine is linked to any more, which
        // a fresh local report once described), an agentless node survives a stale snapshot while a
        // candidate remains, and a cluster with neither nodes nor candidates is deleted.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        long expiredMachine = await SeedMachineAsync(db, tenantId);
        long liveMachine = await SeedMachineAsync(db, tenantId);
        DateTimeOffset now = new(2026, 10, 11, 14, 0, 0, TimeSpan.Zero);

        async Task<long> SeedClusterWithReporterStateAsync(string fingerprint, long? activeReporterMachineId, DateTimeOffset snapshotAt)
        {
            return await db.InsertWithInt64IdentityAsync(new KubernetesCluster
            {
                TenantId = tenantId,
                CaFingerprint = fingerprint,
                Distro = "k3s",
                FirstSeenAt = now.AddDays(-1),
                LastSeenAt = now.AddHours(-1),
                KubeSystemUid = $"uid-{Guid.NewGuid():N}",
                ClusterVersion = "v1.30.4+k3s1",
                ReporterError = "api_unreachable",
                RoleOutdated = true,
                Truncated = true,
                LeaseTransitions = 7,
                ActiveReporterMachineId = activeReporterMachineId,
                LastSnapshotReceivedAt = snapshotAt,
                LastSnapshotTelemetryId = 99,
                LeaseUid = "lease-uid",
                LeaseCreatedAt = now.AddDays(-1),
            });
        }

        async Task<long> SeedNodeAsync(long clusterId, string name, long? machineId, DateTimeOffset? localAt, DateTimeOffset seenAt)
        {
            return await db.InsertWithInt64IdentityAsync(new KubernetesNode
            {
                TenantId = tenantId,
                ClusterId = clusterId,
                NodeName = name,
                MachineId = machineId,
                LastLocalReportAt = localAt,
                NodeMachineId = "feedface",
                Roles = "[\"control-plane\"]",
                ReadyStatus = "True",
                Conditions = "[]",
                Unschedulable = true,
                PodCount = 4,
                PodCapacity = 110,
                LastReporterSeenAt = seenAt,
            });
        }

        long reverted = await SeedClusterWithReporterStateAsync(FingerprintA, expiredMachine, now.AddHours(-3));
        await db.InsertAsync(new KubernetesReporterCandidate
        {
            TenantId = tenantId,
            ClusterId = reverted,
            MachineId = expiredMachine,
            LastReportAt = now.AddDays(-8),
        });
        long linkedNode = await SeedNodeAsync(reverted, "linked", expiredMachine, now.AddHours(-1), now.AddHours(-3));
        long agentlessGone = await SeedNodeAsync(reverted, "agentless", null, null, now.AddHours(-3));
        long unlinkedGone = await SeedNodeAsync(reverted, "moved-away", null, now.AddMinutes(-5), now.AddHours(-3));

        long kept = await SeedClusterWithReporterStateAsync(FingerprintB, liveMachine, now.AddHours(-3));
        await db.InsertAsync(new KubernetesReporterCandidate
        {
            TenantId = tenantId,
            ClusterId = kept,
            MachineId = liveMachine,
            LastReportAt = now.AddHours(-1),
        });
        long agentlessKept = await SeedNodeAsync(kept, "agentless", null, null, now.AddHours(-3));
        long empty = await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Distro = "k3s",
            FirstSeenAt = now.AddDays(-1),
            LastSeenAt = now.AddDays(-1),
        });

        int deleted = await repo.CollectGarbageAsync(now, CancellationToken.None);

        await Assert.That(deleted).IsGreaterThanOrEqualTo(4);
        await Assert.That(await db.KubernetesReporterCandidates.CountAsync(c => c.MachineId == expiredMachine)).IsEqualTo(0);
        await Assert.That(await db.KubernetesReporterCandidates.CountAsync(c => c.MachineId == liveMachine)).IsEqualTo(1);
        await Assert.That(await db.KubernetesNodes.CountAsync(n => n.Id == agentlessGone)).IsEqualTo(0);
        await Assert.That(await db.KubernetesNodes.CountAsync(n => n.Id == unlinkedGone)).IsEqualTo(0);
        await Assert.That(await db.KubernetesClusters.CountAsync(c => c.Id == empty)).IsEqualTo(0);

        KubernetesNode node = await db.KubernetesNodes.SingleAsync(n => n.Id == linkedNode);
        await Assert.That(node.Roles).IsNull();
        await Assert.That(node.NodeMachineId).IsNull();
        await Assert.That(node.Conditions).IsNull();
        await Assert.That(node.Unschedulable).IsFalse();
        await Assert.That(node.PodCount).IsNull();
        await Assert.That(node.LastReporterSeenAt).IsNull();
        await Assert.That(node.MachineId).IsEqualTo(expiredMachine);
        await Assert.That(node.LastLocalReportAt).IsEqualTo(now.AddHours(-1));

        KubernetesCluster revertedCluster = await db.KubernetesClusters.SingleAsync(c => c.Id == reverted);
        await Assert.That(revertedCluster.KubeSystemUid).IsNull();
        await Assert.That(revertedCluster.ActiveReporterMachineId).IsNull();
        await Assert.That(revertedCluster.LeaseUid).IsNull();
        await Assert.That(revertedCluster.LeaseCreatedAt).IsNull();
        await Assert.That(revertedCluster.LeaseTransitions).IsEqualTo(0L);
        await Assert.That(revertedCluster.RoleOutdated).IsFalse();

        KubernetesCluster keptCluster = await db.KubernetesClusters.SingleAsync(c => c.Id == kept);
        await Assert.That(keptCluster.KubeSystemUid).IsNotNull();
        await Assert.That(keptCluster.LeaseTransitions).IsEqualTo(7L);
        await Assert.That((await db.KubernetesNodes.SingleAsync(n => n.Id == agentlessKept)).Roles).IsNotNull();
    }

    [Test]
    public async Task Live_SoftDelete_RemovesTheCandidateAndForgetsTheActiveReporterOnPostgres()
    {
        // Intent: a soft delete clears everything that names the machine from the Kubernetes tables, in
        // the caller's transaction, against the real foreign keys: its candidate row, its node link, and
        // the active reporter of the cluster it last reported for.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);

        try
        {
            long machineId = await SeedMachineAsync(db, tenantId);
            DateTimeOffset seenAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
            long clusterId = await db.InsertWithInt64IdentityAsync(new KubernetesCluster
            {
                TenantId = tenantId,
                CaFingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                Distro = "k3s",
                FirstSeenAt = seenAt,
                LastSeenAt = seenAt,
                ActiveReporterMachineId = machineId,
                LastSnapshotReceivedAt = seenAt,
            });
            long nodeId = await db.InsertWithInt64IdentityAsync(new KubernetesNode
            {
                TenantId = tenantId,
                ClusterId = clusterId,
                NodeName = "node-1",
                MachineId = machineId,
                LastLocalReportAt = seenAt,
            });
            await db.InsertAsync(new KubernetesReporterCandidate
            {
                TenantId = tenantId,
                ClusterId = clusterId,
                MachineId = machineId,
                LastReportAt = seenAt,
            });

            using (IDatabaseTransaction transaction = await repo.BeginTransactionAsync(CancellationToken.None))
            {
                string? deletedKeyHash = await repo.SoftDeleteMachineAsync(machineId, tenantId, 1, CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
                await Assert.That(deletedKeyHash).IsNotNull();
            }

            await Assert.That(await db.KubernetesReporterCandidates.CountAsync(c => c.MachineId == machineId)).IsEqualTo(0);
            await Assert.That((await db.KubernetesNodes.SingleAsync(n => n.Id == nodeId)).MachineId).IsNull();
            KubernetesCluster cluster = await db.KubernetesClusters.SingleAsync(c => c.Id == clusterId);
            await Assert.That(cluster.ActiveReporterMachineId).IsNull();
            await Assert.That(cluster.LastSnapshotReceivedAt).IsEqualTo(seenAt);
        }
        finally
        {
            await CleanUpKubernetesRowsAsync(db, tenantId);
        }
    }

    [Test]
    public async Task Live_SnapshotTenantLock_SerialisesTwoSnapshotsThatWouldBothFoundTheSameCluster()
    {
        // Intent: two holders report the same new cluster at the same moment. Both would insert a cluster
        // with the same UID if they could interleave, and the unique index would make one of them fail.
        // Both are held at the tenant's Kubernetes lock until it is released, then run one after the
        // other, so there is exactly one cluster whatever order they run in, and it ends up with the
        // state of the snapshot with the higher transition count. The interleaving is forced by watching
        // the server's lock waits, never by sleeping.
        using CancellationTokenSource failsafe = new(TimeSpan.FromMinutes(2));
        await using DatabaseContext seedDb = CreateContext();
        int tenantId = await SeedTenantAsync(seedDb);
        long holderA = await SeedMachineAsync(seedDb, tenantId);
        long holderB = await SeedMachineAsync(seedDb, tenantId);
        string uid = Guid.NewGuid().ToString();
        string fingerprint = NewFingerprint();
        DateTimeOffset at = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        string nameA = NewApplicationName("holder-a");
        string nameB = NewApplicationName("holder-b");

        await using DatabaseContext holderDb = CreateContext();
        DatabaseRepository holder = CreateRepo(holderDb);
        Task<SnapshotOutcome> first;
        Task<SnapshotOutcome> second;
        using (IDatabaseTransaction transaction = await holder.BeginTransactionAsync(CancellationToken.None))
        {
            await holder.AcquireKubernetesTenantLockAsync(tenantId, CancellationToken.None);

            first = ApplySnapshotOnNewConnectionAsync(
                nameA, LiveSnapshot(1, tenantId, holderA, at, uid, fingerprint, transitions: 1, nodes: [LiveNode("n1"), LiveNode("n3")]));
            bool firstBlocked = await PostgresLockProbe.WaitUntilBlockedAsync(_migratedConnectionString, nameA, first, failsafe.Token);

            second = ApplySnapshotOnNewConnectionAsync(
                nameB, LiveSnapshot(2, tenantId, holderB, at.AddSeconds(1), uid, fingerprint, transitions: 2, nodes: [LiveNode("n1"), LiveNode("n2")]));
            bool secondBlocked = await PostgresLockProbe.WaitUntilBlockedAsync(_migratedConnectionString, nameB, second, failsafe.Token);

            await Assert.That(firstBlocked).IsTrue();
            await Assert.That(secondBlocked).IsTrue();
            await transaction.CommitAsync(CancellationToken.None);
        }

        SnapshotOutcome outcomeA = await first;
        SnapshotOutcome outcomeB = await second;

        await Assert.That(outcomeB).IsEqualTo(SnapshotOutcome.Accepted);
        await Assert.That((outcomeA == SnapshotOutcome.Accepted) || (outcomeA == SnapshotOutcome.Fenced)).IsTrue();
        KubernetesCluster cluster = await seedDb.KubernetesClusters.SingleAsync(c => c.TenantId == tenantId);
        await Assert.That(cluster.KubeSystemUid).IsEqualTo(uid);
        await Assert.That(cluster.LeaseTransitions).IsEqualTo(2L);
        await Assert.That(cluster.ActiveReporterMachineId).IsEqualTo(holderB);
        List<string> nodeNames = (await seedDb.KubernetesNodes.Where(n => n.ClusterId == cluster.Id).ToListAsync()).Select(n => n.NodeName).Order().ToList();
        await Assert.That(nodeNames).IsEquivalentTo(new[] { "n1", "n2" });
        await Assert.That(await seedDb.KubernetesReporterCandidates.CountAsync(c => c.MachineId == holderB)).IsEqualTo(1);
        await Assert.That(await seedDb.KubernetesReporterCandidates.CountAsync(c => c.MachineId == holderA))
            .IsEqualTo(outcomeA == SnapshotOutcome.Accepted ? 1 : 0);

        await CleanUpKubernetesRowsAsync(seedDb, tenantId);
    }

    [Test]
    public async Task Live_SnapshotTenantLock_SerialisesTwoSnapshotsThatMergeTheSameSplitCluster()
    {
        // Intent: a cluster that local reports split in two is reported by a holder from each half, and both
        // snapshots want to merge the halves. Without the lock both would try to delete the same cluster
        // and move the same rows. Held and then released, they run one after the other: the first merges,
        // the second finds one cluster. Whichever runs first, the older cluster survives with both
        // candidates and every node, holds the UID and carries the higher transition count.
        using CancellationTokenSource failsafe = new(TimeSpan.FromMinutes(2));
        await using DatabaseContext seedDb = CreateContext();
        int tenantId = await SeedTenantAsync(seedDb);
        long holderA = await SeedMachineAsync(seedDb, tenantId);
        long holderB = await SeedMachineAsync(seedDb, tenantId);
        string uid = Guid.NewGuid().ToString();
        string fingerprint = NewFingerprint();
        DateTimeOffset at = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        long older = await SeedLiveClusterAsync(seedDb, tenantId, fingerprint, displayName: "older");
        long younger = await SeedLiveClusterAsync(seedDb, tenantId, fingerprint);
        await SeedLiveCandidateAsync(seedDb, tenantId, older, holderA, at.AddMinutes(-5));
        await SeedLiveCandidateAsync(seedDb, tenantId, younger, holderB, at.AddMinutes(-5));
        await SeedLiveNodeAsync(seedDb, tenantId, older, "a-node", holderA, at.AddMinutes(-5));
        await SeedLiveNodeAsync(seedDb, tenantId, younger, "b-node", holderB, at.AddMinutes(-5));
        ClusterSnapshotNode[] listed = [LiveNode("a-node"), LiveNode("b-node")];
        string nameA = NewApplicationName("holder-a");
        string nameB = NewApplicationName("holder-b");

        await using DatabaseContext holderDb = CreateContext();
        DatabaseRepository holder = CreateRepo(holderDb);
        Task<SnapshotOutcome> first;
        Task<SnapshotOutcome> second;
        using (IDatabaseTransaction transaction = await holder.BeginTransactionAsync(CancellationToken.None))
        {
            await holder.AcquireKubernetesTenantLockAsync(tenantId, CancellationToken.None);

            first = ApplySnapshotOnNewConnectionAsync(
                nameA, LiveSnapshot(1, tenantId, holderA, at, uid, fingerprint, transitions: 1, nodes: listed));
            bool firstBlocked = await PostgresLockProbe.WaitUntilBlockedAsync(_migratedConnectionString, nameA, first, failsafe.Token);

            second = ApplySnapshotOnNewConnectionAsync(
                nameB, LiveSnapshot(2, tenantId, holderB, at.AddSeconds(1), uid, fingerprint, transitions: 2, nodes: listed));
            bool secondBlocked = await PostgresLockProbe.WaitUntilBlockedAsync(_migratedConnectionString, nameB, second, failsafe.Token);

            await Assert.That(firstBlocked).IsTrue();
            await Assert.That(secondBlocked).IsTrue();
            await transaction.CommitAsync(CancellationToken.None);
        }

        SnapshotOutcome outcomeB = await second;
        SnapshotOutcome outcomeA = await first;

        await Assert.That(outcomeB).IsEqualTo(SnapshotOutcome.Accepted);
        await Assert.That((outcomeA == SnapshotOutcome.Accepted) || (outcomeA == SnapshotOutcome.Fenced)).IsTrue();
        KubernetesCluster cluster = await seedDb.KubernetesClusters.SingleAsync(c => c.TenantId == tenantId);
        await Assert.That(cluster.Id).IsEqualTo(older);
        await Assert.That(cluster.DisplayName).IsEqualTo("older");
        await Assert.That(cluster.KubeSystemUid).IsEqualTo(uid);
        await Assert.That(cluster.LeaseTransitions).IsEqualTo(2L);
        await Assert.That(cluster.ActiveReporterMachineId).IsEqualTo(holderB);
        List<string> nodeNames = (await seedDb.KubernetesNodes.Where(n => n.ClusterId == older).ToListAsync()).Select(n => n.NodeName).Order().ToList();
        await Assert.That(nodeNames).IsEquivalentTo(new[] { "a-node", "b-node" });
        await Assert.That(await seedDb.KubernetesReporterCandidates.CountAsync(c => (c.ClusterId == older) && (c.TenantId == tenantId))).IsEqualTo(2);

        await CleanUpKubernetesRowsAsync(seedDb, tenantId);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Live_SnapshotUidMerge_KeepsTheLowerIdAndHoldsEveryUniqueIndexOnPostgres(bool uidHolderIsOlder)
    {
        // Intent: the merge runs its moves, combines and deletes against the real indexes and foreign keys:
        // the partial unique index over (tenant, UID), the unique machine link of a node, the unique
        // (cluster, node name) and the unique machine of a candidate. Two same-named nodes linked to
        // different machines combine into one row that keeps the newer local report's machine, whichever
        // cluster the surviving row came from.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        long sender = await SeedMachineAsync(db, tenantId);
        long other = await SeedMachineAsync(db, tenantId);
        long machineOld = await SeedMachineAsync(db, tenantId);
        long machineNew = await SeedMachineAsync(db, tenantId);
        string uid = Guid.NewGuid().ToString();
        string fingerprint = NewFingerprint();
        DateTimeOffset at = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        long uidless;
        long uidHolder;
        if (uidHolderIsOlder)
        {
            uidHolder = await SeedLiveClusterAsync(db, tenantId, fingerprint, uid: uid);
            uidless = await SeedLiveClusterAsync(db, tenantId, fingerprint);
        }
        else
        {
            uidless = await SeedLiveClusterAsync(db, tenantId, fingerprint);
            uidHolder = await SeedLiveClusterAsync(db, tenantId, fingerprint, uid: uid);
        }

        await SeedLiveCandidateAsync(db, tenantId, uidless, sender, at.AddMinutes(-30));
        await SeedLiveCandidateAsync(db, tenantId, uidHolder, other, at.AddMinutes(-30));
        await SeedLiveNodeAsync(db, tenantId, uidless, "a", sender, at.AddMinutes(-3));
        await SeedLiveNodeAsync(db, tenantId, uidHolder, "b", other, at.AddMinutes(-3));
        await SeedLiveNodeAsync(db, tenantId, uidless, "dup", machineOld, at.AddHours(-3));
        await SeedLiveNodeAsync(db, tenantId, uidHolder, "dup", machineNew, at.AddMinutes(-10));

        SnapshotOutcome outcome = await repo.ApplyClusterSnapshotAsync(
            LiveSnapshot(10, tenantId, sender, at, uid, fingerprint, nodes: [LiveNode("a"), LiveNode("b"), LiveNode("dup")]), at, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        KubernetesCluster survivor = await db.KubernetesClusters.SingleAsync(c => c.TenantId == tenantId);
        await Assert.That(survivor.Id).IsEqualTo(Math.Min(uidless, uidHolder));
        await Assert.That(survivor.KubeSystemUid).IsEqualTo(uid);
        Dictionary<string, KubernetesNode> nodes = (await db.KubernetesNodes.Where(n => n.ClusterId == survivor.Id).ToListAsync())
            .ToDictionary(n => n.NodeName);
        await Assert.That(nodes.Keys.Order()).IsEquivalentTo(new[] { "a", "b", "dup" });
        await Assert.That(nodes["dup"].MachineId).IsEqualTo(machineNew);
        await Assert.That(await db.KubernetesNodes.CountAsync(n => n.MachineId == machineOld)).IsEqualTo(0);
        await Assert.That(await db.KubernetesReporterCandidates.CountAsync(c => (c.ClusterId == survivor.Id) && (c.TenantId == tenantId))).IsEqualTo(2);

        await CleanUpKubernetesRowsAsync(db, tenantId);
    }

    [Test]
    public async Task Live_SnapshotSeparationAndOverlapMerge_RunAgainstTheRealIndexesOnPostgres()
    {
        // Intent: the hardest ordering of the algorithm on the production database. A sender grouped into
        // a cluster with another UID is separated into a cluster founded for its own UID, and an older
        // UID-less cluster that overlaps it is then merged in. The younger, founded cluster is deleted
        // before the older one takes the UID, so the partial unique index never sees two holders. The same
        // snapshot links an unlinked node to its machine through the system id compared in SQL without
        // regard to case, and removes the node the snapshot no longer lists.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        long sender = await SeedMachineAsync(db, tenantId);
        string systemId = Guid.NewGuid().ToString("N");
        long linkable = await SeedMachineAsync(db, tenantId, systemId: systemId.ToUpperInvariant());
        string uidFirst = Guid.NewGuid().ToString();
        string uidSecond = Guid.NewGuid().ToString();
        string fingerprint = NewFingerprint();
        DateTimeOffset at = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        long uidless = await SeedLiveClusterAsync(db, tenantId, fingerprint, displayName: "second cluster");
        long firstUid = await SeedLiveClusterAsync(db, tenantId, fingerprint, uid: uidFirst);
        await SeedLiveCandidateAsync(db, tenantId, firstUid, sender, at.AddMinutes(-30));
        await SeedLiveNodeAsync(db, tenantId, uidless, "m-1", null, at.AddMinutes(-20));
        await SeedLiveNodeAsync(db, tenantId, firstUid, "m-2", sender, at.AddMinutes(-20));
        await SeedLiveNodeAsync(db, tenantId, uidless, "gone", null, at.AddHours(-5));

        SnapshotOutcome outcome = await repo.ApplyClusterSnapshotAsync(
            LiveSnapshot(10, tenantId, sender, at, uidSecond, fingerprint, nodes: [LiveNode("m-1"), LiveNode("m-2"), LiveNode("m-3", systemId.ToLowerInvariant())]),
            at, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(SnapshotOutcome.Accepted);
        List<KubernetesCluster> clusters = await db.KubernetesClusters.Where(c => c.TenantId == tenantId).OrderBy(c => c.Id).ToListAsync();
        await Assert.That(clusters.Select(c => c.Id)).IsEquivalentTo(new[] { uidless, firstUid });
        await Assert.That(clusters[0].KubeSystemUid).IsEqualTo(uidSecond);
        await Assert.That(clusters[0].DisplayName).IsEqualTo("second cluster");
        await Assert.That(clusters[1].KubeSystemUid).IsEqualTo(uidFirst);
        Dictionary<string, KubernetesNode> nodes = (await db.KubernetesNodes.Where(n => n.ClusterId == uidless).ToListAsync())
            .ToDictionary(n => n.NodeName);
        await Assert.That(nodes.Keys.Order()).IsEquivalentTo(new[] { "m-1", "m-2", "m-3" });
        await Assert.That(nodes["m-3"].MachineId).IsEqualTo(linkable);
        await Assert.That(nodes["m-2"].MachineId).IsEqualTo(sender);
        await Assert.That(await db.KubernetesNodes.CountAsync(n => n.ClusterId == firstUid)).IsEqualTo(0);
        await Assert.That((await db.KubernetesReporterCandidates.SingleAsync(c => c.MachineId == sender)).ClusterId).IsEqualTo(uidless);

        await CleanUpKubernetesRowsAsync(db, tenantId);
    }

    [Test]
    public async Task Live_LargeSnapshot_UpsertsEveryNodeAndALaterCompleteOneRemovesTheRestOnPostgres()
    {
        // Intent: a snapshot of 1,201 nodes on a new cluster is upserted in full, and a second complete
        // snapshot that lists 600 of them removes the other 601, on Postgres, where an IN list and a
        // delete by id are real statements with a real parameter limit. No statement may carry the whole
        // list, and the rows are removed again afterwards because the class shares one database.
        await using DatabaseContext db = CreateContext();
        StatementSizeRecorder recorder = new();
        db.AddInterceptor(recorder);
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);

        try
        {
            long sender = await SeedMachineAsync(db, tenantId);
            string uid = Guid.NewGuid().ToString();
            string fingerprint = NewFingerprint();
            DateTimeOffset at = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
            List<int> kept = Enumerable.Range(0, 300).Concat(Enumerable.Range(901, 300)).ToList();

            SnapshotOutcome first = await repo.ApplyClusterSnapshotAsync(
                LiveSnapshot(10, tenantId, sender, at, uid, fingerprint, nodes: LargeSnapshotNodes(Enumerable.Range(0, 1201))),
                at, CancellationToken.None);

            await Assert.That(first).IsEqualTo(SnapshotOutcome.Accepted);
            KubernetesCluster cluster = await db.KubernetesClusters.SingleAsync(c => c.TenantId == tenantId);
            List<string> created = await db.KubernetesNodes
                .Where(n => n.ClusterId == cluster.Id).Select(n => n.NodeName).ToListAsync();
            await Assert.That(created.Count).IsEqualTo(1201);
            await Assert.That(created).IsEquivalentTo(Enumerable.Range(0, 1201).Select(LargeSnapshotNodeName));

            SnapshotOutcome second = await repo.ApplyClusterSnapshotAsync(
                LiveSnapshot(11, tenantId, sender, at.AddMinutes(1), uid, fingerprint, nodes: LargeSnapshotNodes(kept)),
                at.AddMinutes(1), CancellationToken.None);

            await Assert.That(second).IsEqualTo(SnapshotOutcome.Accepted);
            List<string> remaining = await db.KubernetesNodes
                .Where(n => n.ClusterId == cluster.Id).Select(n => n.NodeName).ToListAsync();
            await Assert.That(remaining.Count).IsEqualTo(600);
            await Assert.That(remaining).IsEquivalentTo(kept.Select(LargeSnapshotNodeName));
            await Assert.That(recorder.MaxParameters).IsLessThanOrEqualTo(LargestStatementParameters);
            await Assert.That(recorder.MaxTextLength).IsLessThanOrEqualTo(LargestStatementLength);
            await Assert.That(recorder.MaxInListItems).IsLessThanOrEqualTo(LargestInList);
            await Assert.That(recorder.DeleteStatements).IsEqualTo(2);
        }
        finally
        {
            await CleanUpKubernetesRowsAsync(db, tenantId);
        }
    }

    [Test]
    public async Task Live_SnapshotFencing_IsPerLeaseEpochOnPostgres()
    {
        // Intent: the epoch comparison reads back what Postgres stored. A new Lease epoch restarts the
        // transition count and is accepted, a snapshot of the old epoch is then fenced, and the stored
        // timestamps compare as the instants the agent sent.
        await using DatabaseContext db = CreateContext();
        DatabaseRepository repo = CreateRepo(db);
        int tenantId = await SeedTenantAsync(db);
        long sender = await SeedMachineAsync(db, tenantId);
        string uid = Guid.NewGuid().ToString();
        string fingerprint = NewFingerprint();
        DateTimeOffset at = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset epochOne = at.AddDays(-30);
        DateTimeOffset epochTwo = at.AddDays(-1);

        SnapshotOutcome first = await repo.ApplyClusterSnapshotAsync(
            LiveSnapshot(1, tenantId, sender, at, uid, fingerprint, transitions: 7, leaseCreatedAt: epochOne), at, CancellationToken.None);
        SnapshotOutcome sameEpochLower = await repo.ApplyClusterSnapshotAsync(
            LiveSnapshot(2, tenantId, sender, at.AddMinutes(1), uid, fingerprint, transitions: 6, leaseCreatedAt: epochOne), at, CancellationToken.None);
        SnapshotOutcome newEpoch = await repo.ApplyClusterSnapshotAsync(
            LiveSnapshot(3, tenantId, sender, at.AddMinutes(2), uid, fingerprint, transitions: 1, leaseCreatedAt: epochTwo), at, CancellationToken.None);
        SnapshotOutcome oldEpoch = await repo.ApplyClusterSnapshotAsync(
            LiveSnapshot(4, tenantId, sender, at.AddMinutes(3), uid, fingerprint, transitions: 99, leaseCreatedAt: epochOne), at, CancellationToken.None);

        await Assert.That(first).IsEqualTo(SnapshotOutcome.Accepted);
        await Assert.That(sameEpochLower).IsEqualTo(SnapshotOutcome.Fenced);
        await Assert.That(newEpoch).IsEqualTo(SnapshotOutcome.Accepted);
        await Assert.That(oldEpoch).IsEqualTo(SnapshotOutcome.Fenced);
        KubernetesCluster cluster = await db.KubernetesClusters.SingleAsync(c => c.TenantId == tenantId);
        await Assert.That(cluster.LeaseCreatedAt).IsEqualTo(epochTwo);
        await Assert.That(cluster.LeaseTransitions).IsEqualTo(1L);
        await Assert.That(cluster.LastSnapshotTelemetryId).IsEqualTo(3L);

        await CleanUpKubernetesRowsAsync(db, tenantId);
    }

    /// <summary>
    /// Removes the Kubernetes rows a test seeded for its tenant. Garbage collection visits every tenant of
    /// the shared database and counts what it deletes, so a test must not leave rows behind that a later
    /// test's collection would pick up.
    /// </summary>
    private static async Task CleanUpKubernetesRowsAsync(DatabaseContext db, int tenantId)
    {
        await db.KubernetesReporterCandidates.Where(c => c.TenantId == tenantId).DeleteAsync();
        await db.KubernetesNodes.Where(n => n.TenantId == tenantId).DeleteAsync();
        await db.KubernetesClusters.Where(c => c.TenantId == tenantId).DeleteAsync();
    }

    private static string NewFingerprint()
    {
        return Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    }

    private static ClusterSnapshotNode LiveNode(string name, string? machineId = null)
    {
        return new ClusterSnapshotNode(name, machineId, ["worker"], "True", null, [], false, "v1.30.4+k3s1", "containerd://1.7.23", 3, 110);
    }

    private static string LargeSnapshotNodeName(int index)
    {
        return $"node-{index:D4}";
    }

    private static List<ClusterSnapshotNode> LargeSnapshotNodes(IEnumerable<int> indexes)
    {
        return indexes.Select(i => LiveNode(LargeSnapshotNodeName(i))).ToList();
    }

    private static ClusterSnapshot LiveSnapshot(
        long telemetryId,
        int tenantId,
        long machineId,
        DateTimeOffset at,
        string uid,
        string fingerprint,
        long transitions = 1,
        DateTimeOffset? leaseCreatedAt = null,
        IReadOnlyList<ClusterSnapshotNode>? nodes = null)
    {
        return new ClusterSnapshot(
            telemetryId, tenantId, machineId, at, uid, fingerprint, "v1.30.4+k3s1", false, transitions, "lease-uid",
            leaseCreatedAt ?? at.AddDays(-30), null, false, nodes ?? []);
    }

    private static Task<SnapshotOutcome> ApplySnapshotOnNewConnectionAsync(string applicationName, ClusterSnapshot snapshot)
    {
        return Task.Run(async () =>
        {
            await using DatabaseContext db = CreateContext(applicationName);

            return await CreateRepo(db).ApplyClusterSnapshotAsync(snapshot, snapshot.ServerReceivedAt, CancellationToken.None);
        });
    }

    private static async Task<long> SeedLiveClusterAsync(
        DatabaseContext db, int tenantId, string fingerprint, string? uid = null, string? displayName = null)
    {
        DateTimeOffset seenAt = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

        return await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = fingerprint,
            DisplayName = displayName,
            Distro = "k3s",
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
            KubeSystemUid = uid,
        });
    }

    private static async Task SeedLiveCandidateAsync(
        DatabaseContext db, int tenantId, long clusterId, long machineId, DateTimeOffset lastReportAt)
    {
        await db.InsertAsync(new KubernetesReporterCandidate
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            MachineId = machineId,
            LastReportAt = lastReportAt,
        });
    }

    private static async Task<long> SeedLiveNodeAsync(
        DatabaseContext db, int tenantId, long clusterId, string nodeName, long? machineId, DateTimeOffset lastLocalReportAt)
    {
        return await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = nodeName,
            MachineId = machineId,
            LastLocalReportAt = lastLocalReportAt,
        });
    }

    private static string NewApplicationName(string role)
    {
        return $"vord-test-{role}-{Guid.NewGuid():N}";
    }

    private static DatabaseContext CreateContext(string? applicationName = null)
    {
        string connectionString = (applicationName is null)
            ? _migratedConnectionString
            : PostgresLockProbe.WithApplicationName(_migratedConnectionString, applicationName);
        DataOptions<DatabaseContext> options = new(new DataOptions().UsePostgreSQL(connectionString));

        return new DatabaseContext(options);
    }

    private static DatabaseRepository CreateRepo(DatabaseContext db)
    {
        return new DatabaseRepository(db, NullLogger<DatabaseRepository>.Instance);
    }

    private static Task<LocalReportOutcome> ApplyOnNewConnectionAsync(string applicationName, LocalNodeReport report)
    {
        return Task.Run(async () =>
        {
            await using DatabaseContext db = CreateContext(applicationName);

            return await CreateRepo(db).ApplyLocalNodeReportAsync(report, report.ServerReceivedAt, CancellationToken.None);
        });
    }

    private static async Task<long> SeedLinkedNodeAsync(DatabaseContext db, int tenantId, long machineId)
    {
        DateTimeOffset seenAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        long clusterId = await db.InsertWithInt64IdentityAsync(new KubernetesCluster
        {
            TenantId = tenantId,
            CaFingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Distro = "k3s",
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
        });

        return await db.InsertWithInt64IdentityAsync(new KubernetesNode
        {
            TenantId = tenantId,
            ClusterId = clusterId,
            NodeName = "linked-node",
            MachineId = machineId,
            LastLocalReportAt = seenAt,
        });
    }

    private static async Task<int> SeedTenantAsync(DatabaseContext db)
    {
        return await db.InsertWithInt32IdentityAsync(new Tenant
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Kubernetes Live Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        });
    }

    private static async Task<long> SeedMachineAsync(DatabaseContext db, int tenantId, string? systemId = null)
    {
        long registrationTokenId = await db.InsertWithInt64IdentityAsync(new RegistrationToken
        {
            TenantId = tenantId,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Kubernetes Live Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            IsRevoked = false,
        });

        return await db.InsertWithInt64IdentityAsync(new Machine
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = "kubernetes-live-machine",
            SerialNumber = Guid.NewGuid().ToString("N"),
            SystemId = systemId ?? Guid.NewGuid().ToString("N"),
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = registrationTokenId,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId,
        });
    }

    /// <summary>
    /// The machine seeded by <see cref="SeedManyReportsAsync"/>, the payload of the report the server
    /// accepted last and how many reports the machine has.
    /// </summary>
    private sealed record SeededReports(long MachineId, string NewestPayload, int RowCount);

    /// <summary>
    /// Gives a new machine 200 type-14 reports accepted over the last five and a half days. The three newest
    /// were accepted at the same instant, with the agent's own clock ordering them the other way round, so
    /// only the row id can say which of them was stored last.
    /// </summary>
    private static async Task<SeededReports> SeedManyReportsAsync(DatabaseContext db, int tenantId, DateTimeOffset now)
    {
        const int reportCount = 200;
        long machineId = await SeedMachineAsync(db, tenantId);
        DateTimeOffset newest = now.AddMinutes(-1);
        TimeSpan step = TimeSpan.FromMinutes(40);

        for (int index = 0; index < reportCount; index++)
        {
            DateTimeOffset serverReceivedAt = newest - ((reportCount - 1 - index) * step);
            DateTimeOffset receivedAt = serverReceivedAt;
            if (index >= 197)
            {
                serverReceivedAt = newest;
                receivedAt = newest - TimeSpan.FromHours(index - 197);
            }

            await db.InsertWithInt64IdentityAsync(new MachineTelemetry
            {
                MachineId = machineId,
                TenantId = tenantId,
                TelemetryType = 14,
                Payload = $"report-{index}",
                ReceivedAt = receivedAt,
                ServerReceivedAt = serverReceivedAt,
                SourceEventId = Guid.NewGuid().ToString("N"),
            });
        }

        int stored = await db.MachineTelemetry.CountAsync(t => (t.MachineId == machineId) && (t.TelemetryType == 14));

        return new SeededReports(machineId, $"report-{reportCount - 1}", stored);
    }

    /// <summary>
    /// Runs the repository's latest-report read and returns the statement it sent with every parameter
    /// replaced by a literal, because a plain EXPLAIN cannot take parameters.
    /// </summary>
    private static async Task<string> CaptureLatestNodePayloadsStatementAsync(
        int tenantId, long[] machineIds, DateTimeOffset serverReceivedAfter)
    {
        string statement = string.Empty;
        DataOptions<DatabaseContext> options = new(new DataOptions()
            .UsePostgreSQL(_migratedConnectionString)
            .UseTracing(TraceLevel.Info, info =>
            {
                if ((info.TraceInfoStep == TraceInfoStep.BeforeExecute) && (info.Command != null))
                {
                    statement = InlineParameters(info.Command);
                }
            }));
        await using DatabaseContext db = new(options);

        await CreateRepo(db).GetLatestKubernetesNodePayloadsAsync(tenantId, machineIds, serverReceivedAfter, CancellationToken.None);

        return statement;
    }

    /// <summary>
    /// Adds up the rows every scan of a telemetry partition produced, counting each loop of a scan that
    /// ran more than once. Only the scans that read a table are counted: the joins and sorts above them
    /// pass on rows the scans already produced.
    /// </summary>
    private static double SumLeafScanRows(JsonElement node)
    {
        double rows = 0;
        if (node.TryGetProperty("Relation Name", out JsonElement relation)
            && relation.GetString()!.StartsWith("MachineTelemetry_", StringComparison.Ordinal))
        {
            rows += node.GetProperty("Actual Rows").GetDouble() * node.GetProperty("Actual Loops").GetDouble();
        }

        if (node.TryGetProperty("Plans", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                rows += SumLeafScanRows(child);
            }
        }

        return rows;
    }

    /// <summary>
    /// Removes the telemetry a test seeded for its tenant. The projector's batch read takes the first 500
    /// Kubernetes rows of the shared database, so a test that stores hundreds of reports must not leave them
    /// behind for that test to read past.
    /// </summary>
    private static async Task DeleteTelemetryAsync(DatabaseContext db, int tenantId)
    {
        await db.MachineTelemetry.Where(t => t.TenantId == tenantId).DeleteAsync();
    }

    /// <summary>
    /// Runs the repository's batch read and returns the statement it sent with every parameter
    /// replaced by a literal, because a plain EXPLAIN cannot take parameters, along with the rows.
    /// </summary>
    private static async Task<(string Statement, IReadOnlyList<MachineTelemetry> Rows)> CaptureBatchStatementAsync()
    {
        // The window bounds are fixed, far outside any time the seeded rows can carry, so the result
        // does not depend on the clocks of the test host and the database.
        return await CaptureStatementAsync(repo => repo.GetKubernetesTelemetryBatchAsync(
            0, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), 500, CancellationToken.None));
    }

    /// <summary>
    /// Runs one repository read and returns the statement it sent with every parameter replaced by a
    /// literal, because a plain EXPLAIN cannot take parameters, along with what the read returned.
    /// </summary>
    private static async Task<(string Statement, TResult Result)> CaptureStatementAsync<TResult>(
        Func<IKubernetesInventoryRepository, Task<TResult>> read)
    {
        string statement = string.Empty;
        DataOptions<DatabaseContext> options = new(new DataOptions()
            .UsePostgreSQL(_migratedConnectionString)
            .UseTracing(TraceLevel.Info, info =>
            {
                if ((info.TraceInfoStep == TraceInfoStep.BeforeExecute) && (info.Command != null))
                {
                    statement = InlineParameters(info.Command);
                }
            }));
        await using DatabaseContext db = new(options);

        TResult result = await read(CreateRepo(db));

        return (statement, result);
    }

    private static string InlineParameters(DbCommand command)
    {
        string text = command.CommandText;
        foreach (DbParameter parameter in command.Parameters.Cast<DbParameter>().OrderByDescending(p => p.ParameterName.Length))
        {
            string literal = parameter.Value switch
            {
                DateTimeOffset timestamp => $"'{timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}'::timestamptz",
                IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                long[] numbers => $"ARRAY[{string.Join(",", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)))}]::bigint[]",
                _ => throw new InvalidOperationException($"Unexpected parameter value for {parameter.ParameterName}"),
            };
            text = text.Replace($"@{parameter.ParameterName.TrimStart('@')}", literal, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// Walks a JSON plan collecting each index scan with the relation it reads and each sequential
    /// scan. A bitmap index scan names only the index, so it takes the relation of the bitmap heap
    /// scan above it.
    /// </summary>
    private static void CollectScans(
        JsonElement node, string? enclosingRelation, List<(string Relation, string Index)> indexScans, List<string> sequentialScans)
    {
        string? relation = node.TryGetProperty("Relation Name", out JsonElement relationName)
            ? relationName.GetString()
            : enclosingRelation;

        if (node.TryGetProperty("Index Name", out JsonElement indexName) && (relation != null))
        {
            indexScans.Add((relation, indexName.GetString()!));
        }

        if ((node.GetProperty("Node Type").GetString() == "Seq Scan") && (relation != null))
        {
            sequentialScans.Add(relation);
        }

        if (node.TryGetProperty("Plans", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                CollectScans(child, relation, indexScans, sequentialScans);
            }
        }
    }

    /// <summary>Reports whether a JSON plan contains a node of the given type anywhere.</summary>
    private static bool ContainsNodeType(JsonElement node, string nodeType)
    {
        if (node.GetProperty("Node Type").GetString() == nodeType)
        {
            return true;
        }

        if (node.TryGetProperty("Plans", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                if (ContainsNodeType(child, nodeType))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the names of every index the given index descends from. Child index names are
    /// generated and never contain the parent's name, so the ancestry has to be read from
    /// <c>pg_inherits</c> rather than guessed from the name.
    /// </summary>
    private static async Task<List<string>> IndexAncestorsAsync(NpgsqlConnection connection, string indexName)
    {
        await using NpgsqlCommand command = new(
            """
            WITH RECURSIVE ancestors(oid) AS (
                SELECT i.inhparent
                FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
                WHERE c.relname = @name
                UNION
                SELECT i.inhparent FROM pg_inherits i JOIN ancestors a ON i.inhrelid = a.oid
            )
            SELECT c.relname FROM ancestors a JOIN pg_class c ON c.oid = a.oid
            """,
            connection);
        command.Parameters.AddWithValue("name", indexName);

        List<string> names = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);

        return (string)(await command.ExecuteScalarAsync())!;
    }
}
