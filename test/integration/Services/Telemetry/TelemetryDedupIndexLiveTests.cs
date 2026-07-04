// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Grpc.AgentTelemetry;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Framlux.FleetManagement.Test.Integration.Services.Telemetry;

/// <summary>
/// Live integration tests that exercise the MachineTelemetry dedup unique index against a real
/// Postgres backend (Testcontainers). The dedup index is <c>(SourceEventId, ReceivedAt)</c> because
/// Postgres requires the partition key in every unique index on a partitioned table. These tests
/// prove that a re-delivery of the same source event collides on that index only when the
/// partition-key timestamp is derived deterministically from the item's immutable collected_at —
/// which is exactly what <see cref="TelemetryService.ResolveDedupTimestamp"/> guarantees.
/// </summary>
public sealed class TelemetryDedupIndexLiveTests
{
    private const long TestMachineId = 1;
    private const int TestTenantId = 1;

    private static PostgresFixture _fixture = default!;

    /// <summary>
    /// Starts the Postgres container once per test class.
    /// </summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();
    }

    /// <summary>
    /// Tears down the container after all tests in the class run.
    /// </summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task DedupIndex_SameSourceEventId_SameReceivedAt_SecondInsertViolatesUniqueConstraint()
    {
        // Intent: two deliveries of the same source event that resolve to the same partition-key
        // timestamp must collide on IX_MachineTelemetry_SourceEventId. This is the property the
        // server-side dedup fix relies on — if the index ever stopped enforcing uniqueness on the
        // (SourceEventId, ReceivedAt) tuple, re-deliveries would silently double-write.
        await using DedupTestSchema schema = await DedupTestSchema.CreateAsync();

        DateTimeOffset receivedAt = new(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        const string sourceEventId = "dedup-collide-same-time";

        await schema.InsertTelemetryAsync(sourceEventId, receivedAt);

        PostgresException? ex = await Assert.ThrowsAsync<PostgresException>(
            async () => await schema.InsertTelemetryAsync(sourceEventId, receivedAt));

        await Assert.That(ex?.SqlState).IsEqualTo("23505");
    }

    [Test]
    public async Task DedupIndex_SameSourceEventId_ResolvedTimestampsCollide_OnReDelivery()
    {
        // Intent: two deliveries of the same event id carrying the same immutable collected_at
        // resolve (via ResolveDedupTimestamp) to the SAME partition-key timestamp, so the second
        // physical insert violates the unique index. This is the behavior the dedup fix introduces:
        // a re-delivery after the Redis dedup TTL expires can no longer slip past the index by
        // landing on a fresh ReceivedAt.
        await using DedupTestSchema schema = await DedupTestSchema.CreateAsync();

        DateTimeOffset serverReceivedAt = new(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset collectedAt = new(2026, 6, 24, 11, 59, 30, TimeSpan.Zero);
        const string sourceEventId = "dedup-redelivery";

        TelemetryItem firstDelivery = new()
        {
            EventId = sourceEventId,
            CollectedAt = Timestamp.FromDateTimeOffset(collectedAt),
        };
        // The re-delivery arrives later (different server receipt time) but carries the same
        // immutable collected_at — it must still resolve to the same partition-key timestamp.
        TelemetryItem reDelivery = new()
        {
            EventId = sourceEventId,
            CollectedAt = Timestamp.FromDateTimeOffset(collectedAt),
        };

        DateTimeOffset firstResolved = TelemetryService.ResolveDedupTimestamp(firstDelivery, serverReceivedAt);
        DateTimeOffset reDeliveryResolved = TelemetryService.ResolveDedupTimestamp(
            reDelivery, serverReceivedAt.AddSeconds(1));

        await Assert.That(reDeliveryResolved).IsEqualTo(firstResolved);

        await schema.InsertTelemetryAsync(sourceEventId, firstResolved);

        PostgresException? ex = await Assert.ThrowsAsync<PostgresException>(
            async () => await schema.InsertTelemetryAsync(sourceEventId, reDeliveryResolved));

        await Assert.That(ex?.SqlState).IsEqualTo("23505");
    }

    /// <summary>
    /// Owns a freshly migrated, isolated Postgres database with the daily partitions needed for the
    /// dedup test pre-created, plus a seeded tenant and machine to satisfy the MachineTelemetry
    /// foreign keys. Disposes the per-test data source on teardown.
    /// </summary>
    private sealed class DedupTestSchema : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _dataSource;

        private DedupTestSchema(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        public static async Task<DedupTestSchema> CreateAsync()
        {
            string connectionString = BuildIsolatedDatabaseConnectionString();
            RunMigrations(connectionString);

            NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);

            // Pre-create the daily partitions the inserts will land in. The dedup test uses fixed
            // June 2026 timestamps; create that day and a couple either side so any clamp lands in
            // an existing partition.
            DateOnly[] partitionDays =
            [
                new(2026, 6, 23),
                new(2026, 6, 24),
                new(2026, 6, 25),
            ];
            foreach (DateOnly day in partitionDays)
            {
                string sql = PartitionManagementJob.BuildCreatePartitionSql("MachineTelemetry", day);
                await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
                await cmd.ExecuteNonQueryAsync();
            }

            await SeedTenantAndMachineAsync(dataSource);

            return new DedupTestSchema(dataSource);
        }

        /// <summary>
        /// Inserts a single MachineTelemetry row with the given source event id and partition-key
        /// timestamp. Throws <see cref="PostgresException"/> with SqlState 23505 if the insert
        /// violates the dedup unique index.
        /// </summary>
        public async Task InsertTelemetryAsync(string sourceEventId, DateTimeOffset receivedAt)
        {
            await using NpgsqlCommand cmd = _dataSource.CreateCommand(
                @"INSERT INTO ""MachineTelemetry""
                    (""MachineId"", ""TenantId"", ""TelemetryType"", ""Payload"", ""ReceivedAt"", ""ServerReceivedAt"", ""SourceEventId"")
                  VALUES (@machineId, @tenantId, @type, @payload, @receivedAt, @serverReceivedAt, @sourceEventId)");
            cmd.Parameters.AddWithValue("machineId", TestMachineId);
            cmd.Parameters.AddWithValue("tenantId", TestTenantId);
            cmd.Parameters.AddWithValue("type", (short)1);
            cmd.Parameters.AddWithValue("payload", "{}");
            cmd.Parameters.AddWithValue("receivedAt", receivedAt);
            cmd.Parameters.AddWithValue("serverReceivedAt", receivedAt);
            cmd.Parameters.AddWithValue("sourceEventId", sourceEventId);

            await cmd.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _dataSource.DisposeAsync();
        }

        private static void RunMigrations(string connectionString)
        {
            ServiceCollection services = new();
            services
                .AddFluentMigratorCore()
                .ConfigureRunner(rb => rb
                    .AddPostgres()
                    .WithGlobalConnectionString(connectionString)
                    .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
                .AddLogging(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Warning));

            using ServiceProvider provider = services.BuildServiceProvider();
            using IServiceScope scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
        }

        private static async Task SeedTenantAndMachineAsync(NpgsqlDataSource dataSource)
        {
            // MachineTelemetry has foreign keys to Tenants and Machines; seed the minimum rows the
            // inserts reference. The system user (Id 1) is already seeded by InitialMigration and
            // satisfies the Tenants.CreatedByUserId foreign key. Column sets mirror the consolidated
            // InitialMigration schema.
            DateTimeOffset seedTime = DateTimeOffset.UnixEpoch;

            await using (NpgsqlCommand tenant = dataSource.CreateCommand(
                @"INSERT INTO ""Tenants""
                    (""Id"", ""ExternalId"", ""Name"", ""CreatedAt"", ""CreatedByUserId"", ""IsActive"", ""LogoUrl"")
                  OVERRIDING SYSTEM VALUE
                  VALUES (@id, @externalId, @name, @createdAt, @createdBy, @isActive, @logoUrl)"))
            {
                tenant.Parameters.AddWithValue("id", TestTenantId);
                tenant.Parameters.AddWithValue("externalId", "dedup-test-tenant-ext");
                tenant.Parameters.AddWithValue("name", "dedup-test-tenant");
                tenant.Parameters.AddWithValue("createdAt", seedTime);
                tenant.Parameters.AddWithValue("createdBy", 1);
                tenant.Parameters.AddWithValue("isActive", true);
                tenant.Parameters.AddWithValue("logoUrl", string.Empty);
                await tenant.ExecuteNonQueryAsync();
            }

            await using (NpgsqlCommand machine = dataSource.CreateCommand(
                @"INSERT INTO ""Machines""
                    (""Id"", ""TenantId"", ""ApiKeyHash"", ""Name"", ""SerialNumber"", ""SystemId"",
                     ""MachineType"", ""OperatingSystem"", ""RegistrationTokenId"", ""RegisteredOn"", ""IsDeleted"")
                  OVERRIDING SYSTEM VALUE
                  VALUES (@id, @tenantId, @apiKeyHash, @name, @serial, @systemId,
                          @machineType, @os, @regTokenId, @registeredOn, @isDeleted)"))
            {
                machine.Parameters.AddWithValue("id", TestMachineId);
                machine.Parameters.AddWithValue("tenantId", TestTenantId);
                machine.Parameters.AddWithValue("apiKeyHash", "dedup-test-api-key-hash");
                machine.Parameters.AddWithValue("name", "dedup-test-machine");
                machine.Parameters.AddWithValue("serial", "dedup-serial");
                machine.Parameters.AddWithValue("systemId", "dedup-system-id");
                machine.Parameters.AddWithValue("machineType", (short)0);
                machine.Parameters.AddWithValue("os", (short)0);
                machine.Parameters.AddWithValue("regTokenId", 0L);
                machine.Parameters.AddWithValue("registeredOn", seedTime);
                machine.Parameters.AddWithValue("isDeleted", false);
                await machine.ExecuteNonQueryAsync();
            }
        }

        private static string BuildIsolatedDatabaseConnectionString()
        {
            string baseConn = _fixture.ConnectionString;
            string dbName = $"deduptest_{Guid.NewGuid():N}".Substring(0, 18).ToLowerInvariant();
            NpgsqlConnectionStringBuilder template = new(baseConn);

            using NpgsqlConnection admin = new(baseConn);
            admin.Open();
            using (NpgsqlCommand cmd = admin.CreateCommand())
            {
                cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
                cmd.ExecuteNonQuery();
            }
            admin.Close();

            template.Database = dbName;

            return template.ConnectionString;
        }
    }
}
