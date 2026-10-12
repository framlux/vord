// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using FluentMigrator;
using FluentMigrator.Runner;
using Framlux.FleetManagement.Database.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Framlux.FleetManagement.Test.Integration.Migrations;

/// <summary>
/// Verifies the Kubernetes inventory migration on a real Postgres: the indexes the projection
/// depends on exist, the partial index on the partitioned telemetry table reaches its daily
/// partitions, the foreign keys behave as designed, and the migration can be rolled back. It
/// verifies the cluster reporting migration the same way: its columns, the per-tenant cluster UID
/// index, the reporter candidates table and its constraints, and the rollback.
/// </summary>
public sealed class KubernetesInventoryMigrationTests
{
    private const string TelemetryIndexName = "IX_MachineTelemetry_Kubernetes_Id";

    private static PostgresFixture _fixture = default!;

    /// <summary>Starts the Postgres container once and runs migrations for all tests in this class.</summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        await using ServiceProvider provider = BuildMigrationServices(_fixture.ConnectionString);
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    /// <summary>Stops the Postgres container after all tests in the class.</summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task ClusterFingerprintIndex_CoversTenantAndFingerprint_AndIsNotUnique()
    {
        // Two clusters can share one CA, so this index is a lookup and must never become a constraint.
        string? definition = await GetIndexDefinitionAsync("KubernetesClusters", "IX_KubernetesClusters_TenantId_CaFingerprint");

        await Assert.That(definition).IsNotNull();
        await Assert.That(definition!).Contains("(\"TenantId\", \"CaFingerprint\")");
        await Assert.That(definition!).DoesNotContain("UNIQUE");
    }

    [Test]
    public async Task NodeNameIndex_IsUniqueOverClusterAndNodeName()
    {
        string? definition = await GetIndexDefinitionAsync("KubernetesNodes", "IX_KubernetesNodes_ClusterId_NodeName");

        await Assert.That(definition).IsNotNull();
        await Assert.That(definition!).Contains("UNIQUE");
        await Assert.That(definition!).Contains("(\"ClusterId\", \"NodeName\")");
    }

    [Test]
    public async Task NodeTenantIndex_CoversTenant()
    {
        string? definition = await GetIndexDefinitionAsync("KubernetesNodes", "IX_KubernetesNodes_TenantId");

        await Assert.That(definition).IsNotNull();
        await Assert.That(definition!).Contains("(\"TenantId\")");
        await Assert.That(definition!).DoesNotContain("UNIQUE");
    }

    [Test]
    public async Task NodeMachineIndex_IsUniqueAndPartialOverLinkedNodesOnly()
    {
        string? definition = await GetIndexDefinitionAsync("KubernetesNodes", "IX_KubernetesNodes_MachineId");

        await Assert.That(definition).IsNotNull();
        await Assert.That(definition!).Contains("UNIQUE");
        await Assert.That(definition!).Contains("(\"MachineId\")");
        await Assert.That(definition!).Contains("WHERE (\"MachineId\" IS NOT NULL)");
    }

    [Test]
    public async Task TelemetryIndex_CoversIdForKubernetesTypesOnly()
    {
        // The projection reads types 14 and 15 in Id order; the predicate keeps every other telemetry
        // row out of the index, which is what makes it small enough to be worth carrying.
        string? definition = await GetIndexDefinitionAsync("MachineTelemetry", TelemetryIndexName);

        await Assert.That(definition).IsNotNull();
        await Assert.That(definition!).Contains("(\"Id\")");
        await Assert.That(definition!).Contains("WHERE (\"TelemetryType\" = ANY (ARRAY[14, 15]))");
    }

    [Test]
    public async Task TelemetryIndex_IsBuiltOnEveryLeafPartition_IncludingDailyOnes()
    {
        // The telemetry table is partitioned by retention class and then by day. An index that exists
        // only on the parent would leave every per-partition scan sequential, so each leaf must carry
        // a child of it.
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand counts = new(
            $"""
            SELECT
                (SELECT count(*) FROM pg_partition_tree('"MachineTelemetry"'::regclass) WHERE isleaf),
                (SELECT count(*) FROM pg_partition_tree('"{TelemetryIndexName}"'::regclass) WHERE isleaf)
            """,
            connection);

        await using (NpgsqlDataReader reader = await counts.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            long leafPartitions = reader.GetInt64(0);
            long indexedLeafPartitions = reader.GetInt64(1);

            await Assert.That(leafPartitions).IsGreaterThan(0L);
            await Assert.That(indexedLeafPartitions).IsEqualTo(leafPartitions);
        }

        await using NpgsqlCommand daily = new(
            $$"""
            SELECT table_class.relname
            FROM pg_partition_tree('"{{TelemetryIndexName}}"'::regclass) AS index_tree
            JOIN pg_index ON pg_index.indexrelid = index_tree.relid
            JOIN pg_class AS table_class ON table_class.oid = pg_index.indrelid
            WHERE index_tree.isleaf AND table_class.relname ~ '_[0-9]{8}$'
            """,
            connection);

        List<string> dailyPartitionsWithIndex = [];
        await using (NpgsqlDataReader reader = await daily.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                dailyPartitionsWithIndex.Add(reader.GetString(0));
            }
        }

        await Assert.That(dailyPartitionsWithIndex).IsNotEmpty();
    }

    [Test]
    public async Task DeletingCluster_CascadesToItsNodes_AndLeavesOtherClustersNodes()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        int tenantId = await InsertTenantAsync(connection);
        long doomedClusterId = await InsertClusterAsync(connection, tenantId, "aa");
        long survivingClusterId = await InsertClusterAsync(connection, tenantId, "bb");
        await InsertNodeAsync(connection, tenantId, doomedClusterId, "node-a", machineId: null);
        await InsertNodeAsync(connection, tenantId, doomedClusterId, "node-b", machineId: null);
        await InsertNodeAsync(connection, tenantId, survivingClusterId, "node-a", machineId: null);

        await Execute(connection, """DELETE FROM "KubernetesClusters" WHERE "Id" = @id""", ("id", doomedClusterId));

        long doomedNodes = await Scalar<long>(connection, """SELECT count(*) FROM "KubernetesNodes" WHERE "ClusterId" = @id""", ("id", doomedClusterId));
        long survivingNodes = await Scalar<long>(connection, """SELECT count(*) FROM "KubernetesNodes" WHERE "ClusterId" = @id""", ("id", survivingClusterId));

        await Assert.That(doomedNodes).IsEqualTo(0L);
        await Assert.That(survivingNodes).IsEqualTo(1L);
    }

    [Test]
    public async Task Node_MachineIdMustReferenceAnExistingMachine()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        int tenantId = await InsertTenantAsync(connection);
        long clusterId = await InsertClusterAsync(connection, tenantId, "cc");
        long machineId = await InsertMachineAsync(connection, tenantId);

        long linkedNodeId = await InsertNodeAsync(connection, tenantId, clusterId, "linked", machineId);

        await Assert.That(linkedNodeId).IsGreaterThan(0L);

        PostgresException? violation = null;
        try
        {
            await InsertNodeAsync(connection, tenantId, clusterId, "dangling", machineId: machineId + 1_000_000);
        }
        catch (PostgresException ex)
        {
            violation = ex;
        }

        await Assert.That(violation).IsNotNull();
        await Assert.That(violation!.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Test]
    public async Task Node_TenantIdMustReferenceAnExistingTenant()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        int tenantId = await InsertTenantAsync(connection);
        long clusterId = await InsertClusterAsync(connection, tenantId, "dd");

        PostgresException? violation = null;
        try
        {
            await InsertNodeAsync(connection, tenantId + 1_000_000, clusterId, "orphan", machineId: null);
        }
        catch (PostgresException ex)
        {
            violation = ex;
        }

        await Assert.That(violation).IsNotNull();
        await Assert.That(violation!.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Test]
    public async Task Node_SameMachineLinkedTwice_ViolatesUniqueIndex()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        int tenantId = await InsertTenantAsync(connection);
        long clusterId = await InsertClusterAsync(connection, tenantId, "ee");
        long machineId = await InsertMachineAsync(connection, tenantId);
        await InsertNodeAsync(connection, tenantId, clusterId, "first", machineId);

        // Nodes without a machine are exempt from the uniqueness, so a second unlinked node is fine.
        await InsertNodeAsync(connection, tenantId, clusterId, "unlinked-1", machineId: null);
        await InsertNodeAsync(connection, tenantId, clusterId, "unlinked-2", machineId: null);

        PostgresException? violation = null;
        try
        {
            await InsertNodeAsync(connection, tenantId, clusterId, "second", machineId);
        }
        catch (PostgresException ex)
        {
            violation = ex;
        }

        await Assert.That(violation).IsNotNull();
        await Assert.That(violation!.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
    }

    [Test]
    public async Task MigrateDown_RemovesTheTablesAndTheTelemetryIndex_AndKeepsEarlierSchema()
    {
        string connectionString = await CreateIsolatedDatabaseAsync();
        long previousVersion = typeof(AddKubernetesInventory).GetCustomAttribute<MigrationAttribute>()!.Version - 1;

        await using ServiceProvider provider = BuildMigrationServices(connectionString);
        using IServiceScope scope = provider.CreateScope();
        IMigrationRunner runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        runner.MigrateUp();

        await Assert.That(await TableExistsAsync(connectionString, "KubernetesNodes")).IsTrue();

        runner.MigrateDown(previousVersion);

        await Assert.That(await TableExistsAsync(connectionString, "KubernetesNodes")).IsFalse();
        await Assert.That(await TableExistsAsync(connectionString, "KubernetesClusters")).IsFalse();
        await Assert.That(await TableExistsAsync(connectionString, "KubernetesInventoryProjectionCursor")).IsFalse();
        await Assert.That(await IndexExistsAsync(connectionString, TelemetryIndexName)).IsFalse();
        await Assert.That(await TableExistsAsync(connectionString, "MachineTelemetry")).IsTrue();
        await Assert.That(await TableExistsAsync(connectionString, "Machines")).IsTrue();

        runner.MigrateUp();

        await Assert.That(await TableExistsAsync(connectionString, "KubernetesNodes")).IsTrue();
        await Assert.That(await IndexExistsAsync(connectionString, TelemetryIndexName)).IsTrue();
    }

    [Test]
    public async Task ReportingMigration_ClusterColumns_HaveTheDesignedTypesAndDefaults()
    {
        Dictionary<string, string> columns = await DescribeColumnsAsync("KubernetesClusters");

        await Assert.That(columns["KubeSystemUid"]).IsEqualTo("character varying(64) NULL");
        await Assert.That(columns["ClusterVersion"]).IsEqualTo("character varying(64) NULL");
        await Assert.That(columns["ReporterError"]).IsEqualTo("character varying(32) NULL");
        await Assert.That(columns["RoleOutdated"]).IsEqualTo("boolean NOT NULL DEFAULT false");
        await Assert.That(columns["Truncated"]).IsEqualTo("boolean NOT NULL DEFAULT false");
        await Assert.That(columns["LeaseTransitions"]).IsEqualTo("bigint NOT NULL DEFAULT 0");
        await Assert.That(columns["ActiveReporterMachineId"]).IsEqualTo("bigint NULL");
        await Assert.That(columns["LastSnapshotReceivedAt"]).IsEqualTo("timestamp with time zone NULL");
        await Assert.That(columns["LastSnapshotTelemetryId"]).IsEqualTo("bigint NULL");
        await Assert.That(columns["LeaseUid"]).IsEqualTo("character varying(64) NULL");
        await Assert.That(columns["LeaseCreatedAt"]).IsEqualTo("timestamp with time zone NULL");
    }

    [Test]
    public async Task ReportingMigration_NodeColumns_HaveTheDesignedTypesAndDefaults()
    {
        Dictionary<string, string> columns = await DescribeColumnsAsync("KubernetesNodes");

        await Assert.That(columns["NodeMachineId"]).IsEqualTo("character varying(64) NULL");
        await Assert.That(columns["Roles"]).IsEqualTo("text NULL");
        await Assert.That(columns["ReadyStatus"]).IsEqualTo("character varying(16) NULL");
        await Assert.That(columns["ReadySince"]).IsEqualTo("timestamp with time zone NULL");
        await Assert.That(columns["Conditions"]).IsEqualTo("text NULL");
        await Assert.That(columns["Unschedulable"]).IsEqualTo("boolean NOT NULL DEFAULT false");
        await Assert.That(columns["UnschedulableSince"]).IsEqualTo("timestamp with time zone NULL");
        await Assert.That(columns["ContainerRuntimeVersion"]).IsEqualTo("character varying(64) NULL");
        await Assert.That(columns["PodCount"]).IsEqualTo("integer NULL");
        await Assert.That(columns["PodCapacity"]).IsEqualTo("integer NULL");
        await Assert.That(columns["LastReporterSeenAt"]).IsEqualTo("timestamp with time zone NULL");
    }

    [Test]
    public async Task ReportingMigration_CandidateColumns_HaveTheDesignedTypesAndDefaults()
    {
        Dictionary<string, string> columns = await DescribeColumnsAsync("KubernetesReporterCandidates");

        await Assert.That(columns.Count).IsEqualTo(5);
        await Assert.That(columns["TenantId"]).IsEqualTo("integer NOT NULL");
        await Assert.That(columns["ClusterId"]).IsEqualTo("bigint NOT NULL");
        await Assert.That(columns["MachineId"]).IsEqualTo("bigint NOT NULL");
        await Assert.That(columns["ReporterError"]).IsEqualTo("character varying(32) NULL");
        await Assert.That(columns["LastReportAt"]).IsEqualTo("timestamp with time zone NOT NULL");
    }

    [Test]
    public async Task ClusterKubeSystemUidIndex_IsUniqueAndPartialOverReportedUidsOnly()
    {
        string? definition = await GetIndexDefinitionAsync("KubernetesClusters", "IX_KubernetesClusters_TenantId_KubeSystemUid");

        await Assert.That(definition).IsNotNull();
        await Assert.That(definition!).Contains("UNIQUE");
        await Assert.That(definition!).Contains("(\"TenantId\", \"KubeSystemUid\")");
        await Assert.That(definition!).Contains("WHERE (\"KubeSystemUid\" IS NOT NULL)");
    }

    [Test]
    public async Task Cluster_SameKubeSystemUidInOneTenant_ViolatesUniqueIndex()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        int tenantId = await InsertTenantAsync(connection);
        await InsertClusterAsync(connection, tenantId, "f1", kubeSystemUid: "uid-one");

        PostgresException? violation = null;
        try
        {
            await InsertClusterAsync(connection, tenantId, "f2", kubeSystemUid: "uid-one");
        }
        catch (PostgresException ex)
        {
            violation = ex;
        }

        await Assert.That(violation).IsNotNull();
        await Assert.That(violation!.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
    }

    [Test]
    public async Task Cluster_SameKubeSystemUidInTwoTenants_IsAllowed()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        int firstTenantId = await InsertTenantAsync(connection);
        int secondTenantId = await InsertTenantAsync(connection);
        await InsertClusterAsync(connection, firstTenantId, "f3", kubeSystemUid: "shared-uid");

        long secondClusterId = await InsertClusterAsync(connection, secondTenantId, "f3", kubeSystemUid: "shared-uid");

        await Assert.That(secondClusterId).IsGreaterThan(0L);
    }

    [Test]
    public async Task Cluster_NullKubeSystemUids_NeverConflictInOneTenant()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        int tenantId = await InsertTenantAsync(connection);
        await InsertClusterAsync(connection, tenantId, "f4");
        long secondClusterId = await InsertClusterAsync(connection, tenantId, "f5");
        long thirdClusterId = await InsertClusterAsync(connection, tenantId, "f6");

        await Assert.That(secondClusterId).IsGreaterThan(0L);
        await Assert.That(thirdClusterId).IsGreaterThan(0L);
    }

    [Test]
    public async Task Cluster_ActiveReporterMachineIdMustReferenceAnExistingMachine()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        int tenantId = await InsertTenantAsync(connection);
        long machineId = await InsertMachineAsync(connection, tenantId);
        long clusterId = await InsertClusterAsync(connection, tenantId, "f7");

        await Execute(
            connection,
            """UPDATE "KubernetesClusters" SET "ActiveReporterMachineId" = @machineId WHERE "Id" = @clusterId""",
            ("machineId", machineId),
            ("clusterId", clusterId));

        PostgresException? violation = null;
        try
        {
            await Execute(
                connection,
                """UPDATE "KubernetesClusters" SET "ActiveReporterMachineId" = @machineId WHERE "Id" = @clusterId""",
                ("machineId", machineId + 1_000_000),
                ("clusterId", clusterId));
        }
        catch (PostgresException ex)
        {
            violation = ex;
        }

        await Assert.That(violation).IsNotNull();
        await Assert.That(violation!.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Test]
    public async Task CandidatePrimaryKey_CoversClusterAndMachine()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        string? definition = await Scalar<string?>(
            connection,
            """
            SELECT pg_get_constraintdef(oid) FROM pg_constraint
            WHERE conrelid = '"KubernetesReporterCandidates"'::regclass AND contype = 'p'
            """);

        await Assert.That(definition).IsEqualTo("PRIMARY KEY (\"ClusterId\", \"MachineId\")");
    }

    [Test]
    public async Task CandidateMachineIndex_IsUniqueOverMachine()
    {
        string? definition = await GetIndexDefinitionAsync("KubernetesReporterCandidates", "IX_KubernetesReporterCandidates_MachineId");

        await Assert.That(definition).IsNotNull();
        await Assert.That(definition!).Contains("UNIQUE");
        await Assert.That(definition!).Contains("(\"MachineId\")");
    }

    [Test]
    public async Task CandidateTenantIndex_CoversTenant()
    {
        string? definition = await GetIndexDefinitionAsync("KubernetesReporterCandidates", "IX_KubernetesReporterCandidates_TenantId");

        await Assert.That(definition).IsNotNull();
        await Assert.That(definition!).Contains("(\"TenantId\")");
        await Assert.That(definition!).DoesNotContain("UNIQUE");
    }

    [Test]
    public async Task DeletingCluster_CascadesToItsCandidates_AndLeavesOtherClustersCandidates()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        int tenantId = await InsertTenantAsync(connection);
        long doomedClusterId = await InsertClusterAsync(connection, tenantId, "g1");
        long survivingClusterId = await InsertClusterAsync(connection, tenantId, "g2");
        await InsertCandidateAsync(connection, tenantId, doomedClusterId, await InsertMachineAsync(connection, tenantId));
        await InsertCandidateAsync(connection, tenantId, doomedClusterId, await InsertMachineAsync(connection, tenantId));
        await InsertCandidateAsync(connection, tenantId, survivingClusterId, await InsertMachineAsync(connection, tenantId));

        await Execute(connection, """DELETE FROM "KubernetesClusters" WHERE "Id" = @id""", ("id", doomedClusterId));

        long doomedCandidates = await Scalar<long>(connection, """SELECT count(*) FROM "KubernetesReporterCandidates" WHERE "ClusterId" = @id""", ("id", doomedClusterId));
        long survivingCandidates = await Scalar<long>(connection, """SELECT count(*) FROM "KubernetesReporterCandidates" WHERE "ClusterId" = @id""", ("id", survivingClusterId));

        await Assert.That(doomedCandidates).IsEqualTo(0L);
        await Assert.That(survivingCandidates).IsEqualTo(1L);
    }

    [Test]
    public async Task Candidate_SameMachineInTwoClusters_ViolatesUniqueIndex()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        int tenantId = await InsertTenantAsync(connection);
        long firstClusterId = await InsertClusterAsync(connection, tenantId, "g3");
        long secondClusterId = await InsertClusterAsync(connection, tenantId, "g4");
        long machineId = await InsertMachineAsync(connection, tenantId);
        await InsertCandidateAsync(connection, tenantId, firstClusterId, machineId);

        PostgresException? violation = null;
        try
        {
            await InsertCandidateAsync(connection, tenantId, secondClusterId, machineId);
        }
        catch (PostgresException ex)
        {
            violation = ex;
        }

        await Assert.That(violation).IsNotNull();
        await Assert.That(violation!.SqlState).IsEqualTo(PostgresErrorCodes.UniqueViolation);
    }

    [Test]
    public async Task Candidate_MachineIdMustReferenceAnExistingMachine()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        int tenantId = await InsertTenantAsync(connection);
        long clusterId = await InsertClusterAsync(connection, tenantId, "g5");
        long machineId = await InsertMachineAsync(connection, tenantId);

        PostgresException? violation = null;
        try
        {
            await InsertCandidateAsync(connection, tenantId, clusterId, machineId + 1_000_000);
        }
        catch (PostgresException ex)
        {
            violation = ex;
        }

        await Assert.That(violation).IsNotNull();
        await Assert.That(violation!.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Test]
    public async Task Candidate_TenantIdMustReferenceAnExistingTenant()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        int tenantId = await InsertTenantAsync(connection);
        long clusterId = await InsertClusterAsync(connection, tenantId, "g6");
        long machineId = await InsertMachineAsync(connection, tenantId);

        PostgresException? violation = null;
        try
        {
            await InsertCandidateAsync(connection, tenantId + 1_000_000, clusterId, machineId);
        }
        catch (PostgresException ex)
        {
            violation = ex;
        }

        await Assert.That(violation).IsNotNull();
        await Assert.That(violation!.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Test]
    public async Task Candidate_ClusterIdMustReferenceAnExistingCluster()
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        int tenantId = await InsertTenantAsync(connection);
        long clusterId = await InsertClusterAsync(connection, tenantId, "g7");
        long machineId = await InsertMachineAsync(connection, tenantId);

        PostgresException? violation = null;
        try
        {
            await InsertCandidateAsync(connection, tenantId, clusterId + 1_000_000, machineId);
        }
        catch (PostgresException ex)
        {
            violation = ex;
        }

        await Assert.That(violation).IsNotNull();
        await Assert.That(violation!.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Test]
    public async Task ReportingMigrateDown_RemovesTheReportingSchema_AndKeepsTheInventoryTables()
    {
        string connectionString = await CreateIsolatedDatabaseAsync();
        long previousVersion = typeof(AddKubernetesReporting).GetCustomAttribute<MigrationAttribute>()!.Version - 1;

        await using ServiceProvider provider = BuildMigrationServices(connectionString);
        using IServiceScope scope = provider.CreateScope();
        IMigrationRunner runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        runner.MigrateUp();

        await Assert.That(await TableExistsAsync(connectionString, "KubernetesReporterCandidates")).IsTrue();
        await Assert.That(await ColumnExistsAsync(connectionString, "KubernetesClusters", "KubeSystemUid")).IsTrue();

        runner.MigrateDown(previousVersion);

        await Assert.That(await TableExistsAsync(connectionString, "KubernetesReporterCandidates")).IsFalse();
        await Assert.That(await IndexExistsAsync(connectionString, "IX_KubernetesClusters_TenantId_KubeSystemUid")).IsFalse();
        await Assert.That(await ColumnExistsAsync(connectionString, "KubernetesClusters", "KubeSystemUid")).IsFalse();
        await Assert.That(await ColumnExistsAsync(connectionString, "KubernetesClusters", "ActiveReporterMachineId")).IsFalse();
        await Assert.That(await ColumnExistsAsync(connectionString, "KubernetesClusters", "LeaseCreatedAt")).IsFalse();
        await Assert.That(await ColumnExistsAsync(connectionString, "KubernetesNodes", "Conditions")).IsFalse();
        await Assert.That(await ColumnExistsAsync(connectionString, "KubernetesNodes", "LastReporterSeenAt")).IsFalse();
        await Assert.That(await TableExistsAsync(connectionString, "KubernetesClusters")).IsTrue();
        await Assert.That(await TableExistsAsync(connectionString, "KubernetesNodes")).IsTrue();
        await Assert.That(await IndexExistsAsync(connectionString, TelemetryIndexName)).IsTrue();

        runner.MigrateUp();

        await Assert.That(await TableExistsAsync(connectionString, "KubernetesReporterCandidates")).IsTrue();
        await Assert.That(await IndexExistsAsync(connectionString, "IX_KubernetesClusters_TenantId_KubeSystemUid")).IsTrue();
    }

    private static ServiceProvider BuildMigrationServices(string connectionString)
    {
        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Warning));

        return services.BuildServiceProvider();
    }

    private static async Task<string> CreateIsolatedDatabaseAsync()
    {
        // The rollback test destroys the schema it runs against, so it gets a database of its own
        // rather than the one the other tests in this class share.
        string databaseName = $"k8sdown_{Guid.NewGuid():N}";
        NpgsqlConnectionStringBuilder builder = new(_fixture.ConnectionString);

        await using (NpgsqlConnection admin = new(builder.ConnectionString))
        {
            await admin.OpenAsync();
            await Execute(admin, $"CREATE DATABASE \"{databaseName}\"");
        }

        builder.Database = databaseName;

        return builder.ConnectionString;
    }

    private static async Task<string?> GetIndexDefinitionAsync(string tableName, string indexName)
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        return await Scalar<string?>(
            connection,
            "SELECT indexdef FROM pg_indexes WHERE tablename = @table AND indexname = @name",
            ("table", tableName),
            ("name", indexName));
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string tableName)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();

        return await Scalar<bool>(
            connection,
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @name)",
            ("name", tableName));
    }

    private static async Task<bool> ColumnExistsAsync(string connectionString, string tableName, string columnName)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();

        return await Scalar<bool>(
            connection,
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table AND column_name = @column)",
            ("table", tableName),
            ("column", columnName));
    }

    private static async Task<Dictionary<string, string>> DescribeColumnsAsync(string tableName)
    {
        await using NpgsqlConnection connection = new(_fixture.ConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = new(
            """
            SELECT column_name, data_type, character_maximum_length, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table
            """,
            connection);
        command.Parameters.AddWithValue("table", tableName);

        Dictionary<string, string> columns = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string length = reader.IsDBNull(2) ? string.Empty : $"({reader.GetInt32(2)})";
            string nullability = (reader.GetString(3) == "YES") ? "NULL" : "NOT NULL";
            string defaultValue = reader.IsDBNull(4) ? string.Empty : $" DEFAULT {reader.GetString(4)}";

            columns[reader.GetString(0)] = $"{reader.GetString(1)}{length} {nullability}{defaultValue}";
        }

        return columns;
    }

    private static async Task<bool> IndexExistsAsync(string connectionString, string indexName)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();

        return await Scalar<bool>(
            connection,
            "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = @name)",
            ("name", indexName));
    }

    private static async Task<int> InsertTenantAsync(NpgsqlConnection connection)
    {
        string unique = Guid.NewGuid().ToString("N");

        return await Scalar<int>(
            connection,
            """
            INSERT INTO "Tenants" ("ExternalId", "Name", "CreatedAt", "CreatedByUserId", "IsActive", "LogoUrl")
            VALUES (@externalId, @name, now(), 1, true, '')
            RETURNING "Id"
            """,
            ("externalId", $"ext-{unique}"),
            ("name", $"Tenant {unique}"));
    }

    private static async Task<long> InsertMachineAsync(NpgsqlConnection connection, int tenantId)
    {
        string unique = Guid.NewGuid().ToString("N");

        return await Scalar<long>(
            connection,
            """
            INSERT INTO "Machines" ("TenantId", "ApiKeyHash", "Name", "SerialNumber", "SystemId", "MachineType", "OperatingSystem", "RegistrationTokenId", "RegisteredOn", "IsDeleted")
            VALUES (@tenantId, @apiKeyHash, @name, @serial, @systemId, 0, 0, 1, now(), false)
            RETURNING "Id"
            """,
            ("tenantId", tenantId),
            ("apiKeyHash", unique + unique),
            ("name", $"machine-{unique}"),
            ("serial", unique[..16]),
            ("systemId", unique[16..]));
    }

    private static async Task<long> InsertClusterAsync(NpgsqlConnection connection, int tenantId, string fingerprintSeed, string? kubeSystemUid = null)
    {
        return await Scalar<long>(
            connection,
            """
            INSERT INTO "KubernetesClusters" ("TenantId", "CaFingerprint", "Distro", "FirstSeenAt", "LastSeenAt", "KubeSystemUid")
            VALUES (@tenantId, @fingerprint, 'k3s', now(), now(), @kubeSystemUid)
            RETURNING "Id"
            """,
            ("tenantId", tenantId),
            ("fingerprint", fingerprintSeed.PadRight(64, '0')),
            ("kubeSystemUid", (object?)kubeSystemUid ?? DBNull.Value));
    }

    private static async Task InsertCandidateAsync(NpgsqlConnection connection, int tenantId, long clusterId, long machineId)
    {
        await Execute(
            connection,
            """
            INSERT INTO "KubernetesReporterCandidates" ("TenantId", "ClusterId", "MachineId", "LastReportAt")
            VALUES (@tenantId, @clusterId, @machineId, now())
            """,
            ("tenantId", tenantId),
            ("clusterId", clusterId),
            ("machineId", machineId));
    }

    private static async Task<long> InsertNodeAsync(NpgsqlConnection connection, int tenantId, long clusterId, string nodeName, long? machineId)
    {
        return await Scalar<long>(
            connection,
            """
            INSERT INTO "KubernetesNodes" ("TenantId", "ClusterId", "NodeName", "MachineId")
            VALUES (@tenantId, @clusterId, @nodeName, @machineId)
            RETURNING "Id"
            """,
            ("tenantId", tenantId),
            ("clusterId", clusterId),
            ("nodeName", nodeName),
            ("machineId", (object?)machineId ?? DBNull.Value));
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        object? result = await command.ExecuteScalarAsync();

        return (result is null or DBNull) ? default! : (T)result;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
