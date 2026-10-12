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

        await Assert.That(rows.Count).IsEqualTo(5);
        await Assert.That(rows.All(r => r.TelemetryType == 14)).IsTrue();
        await Assert.That(loadedPartition).StartsWith("MachineTelemetry_Short_");
        await Assert.That(loadedPartitionScans.Count).IsGreaterThan(0);
        await Assert.That(everyScanUsesPartialIndex).IsTrue();
        await Assert.That(sequentialScans.Contains(loadedPartition)).IsFalse();
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

        await Assert.That(deleted).IsEqualTo(2);
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

        await Assert.That(deleted).IsEqualTo(3);
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
        // state with its Lease epoch cleared, an agentless node survives a stale snapshot while a
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

        await Assert.That(deleted).IsGreaterThanOrEqualTo(3);
        await Assert.That(await db.KubernetesReporterCandidates.CountAsync(c => c.MachineId == expiredMachine)).IsEqualTo(0);
        await Assert.That(await db.KubernetesReporterCandidates.CountAsync(c => c.MachineId == liveMachine)).IsEqualTo(1);
        await Assert.That(await db.KubernetesNodes.CountAsync(n => n.Id == agentlessGone)).IsEqualTo(0);
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

    private static async Task<long> SeedMachineAsync(DatabaseContext db, int tenantId)
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
            SystemId = Guid.NewGuid().ToString("N"),
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = registrationTokenId,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId,
        });
    }

    /// <summary>
    /// Runs the repository's batch read and returns the statement it sent with every parameter
    /// replaced by a literal, because a plain EXPLAIN cannot take parameters, along with the rows.
    /// </summary>
    private static async Task<(string Statement, IReadOnlyList<MachineTelemetry> Rows)> CaptureBatchStatementAsync()
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

        // The window bounds are fixed, far outside any time the seeded rows can carry, so the result
        // does not depend on the clocks of the test host and the database.
        IReadOnlyList<MachineTelemetry> rows = await CreateRepo(db).GetKubernetesTelemetryBatchAsync(
            0, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), 500, CancellationToken.None);

        return (statement, rows);
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
