// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Repositories;

/// <summary>
/// Tests the payload-marker telemetry queries that back windowed failed-SSH-login alerting: a
/// server-side count over the window, and the bounded payload read used only to describe an
/// incident that has already been decided.
/// </summary>
public sealed class TelemetryPayloadMarkerQueryTests
{
    private const short SshSessions = 9;
    private const string FailedMarker = "\"action\":\"failed\"";

    private static readonly DateTimeOffset WindowEnd = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowStart = WindowEnd.AddMinutes(-5);

    private static IMachineStateRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }

    private static async Task InsertAsync(
        TestDatabaseFactory dbFactory,
        DateTimeOffset serverReceivedAt,
        string action = "failed",
        string sourceIp = "203.0.113.7",
        long machineId = 1,
        short telemetryType = SshSessions,
        DateTimeOffset? receivedAt = null)
    {
        await dbFactory.Context.InsertAsync(new MachineTelemetry
        {
            MachineId = machineId,
            TenantId = 1,
            TelemetryType = telemetryType,
            Payload = $"{{\"user\":\"root\",\"source_ip\":\"{sourceIp}\",\"source_port\":55000,\"action\":\"{action}\",\"auth_method\":\"password\"}}",
            ReceivedAt = receivedAt ?? serverReceivedAt,
            ServerReceivedAt = serverReceivedAt,
        });
    }

    [Test]
    public async Task CountTelemetryWithPayloadMarker_CountsOnlyMatchingPayloads()
    {
        using TestDatabaseFactory dbFactory = new();
        await InsertAsync(dbFactory, WindowEnd.AddMinutes(-1));
        await InsertAsync(dbFactory, WindowEnd.AddMinutes(-2));
        await InsertAsync(dbFactory, WindowEnd.AddMinutes(-3), action: "connect");
        await InsertAsync(dbFactory, WindowEnd.AddMinutes(-4), action: "disconnect");

        IMachineStateRepository repo = CreateRepo(dbFactory);

        int count = await repo.CountTelemetryWithPayloadMarkerAsync(1, SshSessions, FailedMarker, WindowStart, WindowEnd, CancellationToken.None);

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task CountTelemetryWithPayloadMarker_IgnoresOtherMachinesAndTelemetryTypes()
    {
        using TestDatabaseFactory dbFactory = new();
        await InsertAsync(dbFactory, WindowEnd.AddMinutes(-1));
        await InsertAsync(dbFactory, WindowEnd.AddMinutes(-1), machineId: 2);
        await InsertAsync(dbFactory, WindowEnd.AddMinutes(-1), telemetryType: 6);

        IMachineStateRepository repo = CreateRepo(dbFactory);

        int count = await repo.CountTelemetryWithPayloadMarkerAsync(1, SshSessions, FailedMarker, WindowStart, WindowEnd, CancellationToken.None);

        await Assert.That(count).IsEqualTo(1);
    }

    /// <summary>
    /// The window is bounded by server receipt time, inclusive at both ends, so a run that lands
    /// exactly on a boundary neither double-counts nor drops the attempt sitting on it.
    /// </summary>
    [Test]
    public async Task CountTelemetryWithPayloadMarker_IncludesBoundariesAndExcludesOutsideTheWindow()
    {
        using TestDatabaseFactory dbFactory = new();
        await InsertAsync(dbFactory, WindowStart);
        await InsertAsync(dbFactory, WindowEnd);
        await InsertAsync(dbFactory, WindowStart.AddSeconds(-1));
        await InsertAsync(dbFactory, WindowEnd.AddSeconds(1));

        IMachineStateRepository repo = CreateRepo(dbFactory);

        int count = await repo.CountTelemetryWithPayloadMarkerAsync(1, SshSessions, FailedMarker, WindowStart, WindowEnd, CancellationToken.None);

        await Assert.That(count).IsEqualTo(2);
    }

    /// <summary>
    /// The window is measured on server receipt time, never on the agent-clock-derived ReceivedAt,
    /// so a machine whose clock is hours out still has its attempts counted in the right window.
    /// </summary>
    [Test]
    public async Task CountTelemetryWithPayloadMarker_UsesServerReceiptTimeNotTheAgentClock()
    {
        using TestDatabaseFactory dbFactory = new();

        // Two rows the server received inside the window, from an agent whose clock is hours slow.
        await InsertAsync(dbFactory, WindowEnd.AddMinutes(-1), receivedAt: WindowEnd.AddHours(-6), sourceIp: "203.0.113.1");
        await InsertAsync(dbFactory, WindowEnd.AddMinutes(-2), receivedAt: WindowEnd.AddHours(-6), sourceIp: "203.0.113.2");

        // One row the server received long ago, from an agent whose clock is hours fast. Counting on
        // the agent clock would swap which rows match, so the counts must differ for the assertion to
        // distinguish the two columns at all.
        await InsertAsync(dbFactory, WindowEnd.AddHours(-6), receivedAt: WindowEnd.AddMinutes(-1), sourceIp: "198.51.100.9");

        IMachineStateRepository repo = CreateRepo(dbFactory);

        int count = await repo.CountTelemetryWithPayloadMarkerAsync(1, SshSessions, FailedMarker, WindowStart, WindowEnd, CancellationToken.None);

        List<string> payloads = await repo.GetTelemetryPayloadsWithMarkerAsync(1, SshSessions, FailedMarker, WindowStart, WindowEnd, 10, CancellationToken.None);

        await Assert.That(count).IsEqualTo(2);
        await Assert.That(payloads.Any(payload => payload.Contains("203.0.113.1", StringComparison.Ordinal))).IsTrue();
        await Assert.That(payloads.Any(payload => payload.Contains("198.51.100.9", StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>
    /// MachineTelemetry is range-partitioned on ReceivedAt, so the query carries a generous
    /// ReceivedAt lower bound purely to let PostgreSQL prune partitions. Ingestion clamps ReceivedAt
    /// to the same maintained window, so a row this bound excludes cannot be produced in
    /// production — the assertion exists to fail if the bound is ever dropped.
    /// </summary>
    [Test]
    public async Task CountTelemetryWithPayloadMarker_KeepsThePartitionPruningLowerBound()
    {
        using TestDatabaseFactory dbFactory = new();
        await InsertAsync(dbFactory, WindowEnd.AddMinutes(-1), receivedAt: WindowStart.AddDays(-30));

        IMachineStateRepository repo = CreateRepo(dbFactory);

        int count = await repo.CountTelemetryWithPayloadMarkerAsync(1, SshSessions, FailedMarker, WindowStart, WindowEnd, CancellationToken.None);

        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task GetTelemetryPayloadsWithMarker_ReturnsNewestFirstAndRespectsTheLimit()
    {
        using TestDatabaseFactory dbFactory = new();
        await InsertAsync(dbFactory, WindowEnd.AddMinutes(-4), sourceIp: "198.51.100.1");
        await InsertAsync(dbFactory, WindowEnd.AddMinutes(-3), sourceIp: "198.51.100.2");
        await InsertAsync(dbFactory, WindowEnd.AddMinutes(-2), sourceIp: "198.51.100.3");
        await InsertAsync(dbFactory, WindowEnd.AddMinutes(-1), action: "connect", sourceIp: "198.51.100.9");

        IMachineStateRepository repo = CreateRepo(dbFactory);

        List<string> payloads = await repo.GetTelemetryPayloadsWithMarkerAsync(1, SshSessions, FailedMarker, WindowStart, WindowEnd, limit: 2, CancellationToken.None);

        await Assert.That(payloads.Count).IsEqualTo(2);
        await Assert.That(payloads[0]).Contains("198.51.100.3");
        await Assert.That(payloads[1]).Contains("198.51.100.2");
    }

    [Test]
    public async Task CountTelemetryWithPayloadMarker_EmptyMarker_Throws()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = CreateRepo(dbFactory);

        await Assert.That(async () => await repo.CountTelemetryWithPayloadMarkerAsync(1, SshSessions, "", WindowStart, WindowEnd, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetTelemetryPayloadsWithMarker_NonPositiveLimit_Throws()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineStateRepository repo = CreateRepo(dbFactory);

        await Assert.That(async () => await repo.GetTelemetryPayloadsWithMarkerAsync(1, SshSessions, FailedMarker, WindowStart, WindowEnd, limit: 0, CancellationToken.None))
            .Throws<ArgumentOutOfRangeException>();
    }
}
