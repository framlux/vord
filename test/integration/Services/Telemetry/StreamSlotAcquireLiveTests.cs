// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Telemetry;
using Framlux.FleetManagement.Test.Integration;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Polly;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Integration.Services.Telemetry;

/// <summary>
/// Live tests for the atomic stream-slot acquire against a real Redis (Testcontainers), which — unlike a
/// substitute — actually runs the acquire Lua script. Proves the cap is enforced server-side, a denied
/// acquire never strands the count above the cap, a release re-opens a slot, and the TTL is (re)applied
/// on every successful acquire.
/// </summary>
public sealed class StreamSlotAcquireLiveTests
{
    private static RedisFixture _fixture = default!;

    /// <summary>Starts the Redis container once for the class.</summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new RedisFixture();
        await _fixture.InitializeAsync();
    }

    /// <summary>Stops the Redis container after the class.</summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    private static TelemetryService BuildService(int maxPerMachine)
    {
        return new TelemetryService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ITelemetryDeduplicationService>(),
            Substitute.For<ISubscriptionService>(),
            Substitute.For<IBackgroundJobClient>(),
            ResiliencePipeline.Empty,
            _fixture.Connection,
            Options.Create(new TelemetryOptions { MaxConcurrentStreamsPerMachine = maxPerMachine }),
            new ProcessStreamSlotLimiter(5000),
            TimeProvider.System,
            NullLogger<TelemetryService>.Instance);
    }

    private static string SlotKey(long machineId) => $"telemetry:stream:{machineId}";

    [Test]
    public async Task AcquireBeyondCap_IsDenied_AndCountNeverExceedsCap()
    {
        TelemetryService svc = BuildService(maxPerMachine: 1);
        const long machineId = 91001;
        IDatabase db = _fixture.Connection.GetDatabase();

        StreamSlotSource? first = await svc.TryAcquireStreamSlotAsync(machineId, TimeSpan.FromMinutes(6));
        StreamSlotSource? second = await svc.TryAcquireStreamSlotAsync(machineId, TimeSpan.FromMinutes(6));

        await Assert.That(first).IsEqualTo(StreamSlotSource.Redis);
        await Assert.That(second).IsNull();

        // The over-cap acquire self-decremented, so the stored count stays at the cap — it never leaks up.
        RedisValue count = await db.StringGetAsync(SlotKey(machineId));
        await Assert.That((long)count).IsEqualTo(1L);
    }

    [Test]
    public async Task ReleaseAfterDenied_ReopensASlot()
    {
        TelemetryService svc = BuildService(maxPerMachine: 1);
        const long machineId = 91002;
        IDatabase db = _fixture.Connection.GetDatabase();

        StreamSlotSource? first = await svc.TryAcquireStreamSlotAsync(machineId, TimeSpan.FromMinutes(6));
        StreamSlotSource? denied = await svc.TryAcquireStreamSlotAsync(machineId, TimeSpan.FromMinutes(6));
        await Assert.That(first).IsEqualTo(StreamSlotSource.Redis);
        await Assert.That(denied).IsNull();

        await svc.ReleaseStreamSlotAsync(machineId, StreamSlotSource.Redis);

        StreamSlotSource? afterRelease = await svc.TryAcquireStreamSlotAsync(machineId, TimeSpan.FromMinutes(6));
        await Assert.That(afterRelease).IsEqualTo(StreamSlotSource.Redis);

        RedisValue count = await db.StringGetAsync(SlotKey(machineId));
        await Assert.That((long)count).IsEqualTo(1L);
    }

    [Test]
    public async Task Ttl_IsReappliedOnEverySuccessfulAcquire()
    {
        TelemetryService svc = BuildService(maxPerMachine: 2);
        const long machineId = 91003;
        IDatabase db = _fixture.Connection.GetDatabase();

        StreamSlotSource? first = await svc.TryAcquireStreamSlotAsync(machineId, TimeSpan.FromSeconds(120));
        await Assert.That(first).IsEqualTo(StreamSlotSource.Redis);

        // Strip the TTL to simulate a key that would otherwise expire mid-stream. The old code only set
        // the TTL on the 0->1 transition, so the second (overlapping) acquire would leave the key
        // persisted. The atomic script re-applies EXPIRE on every acquire, so the TTL comes back.
        await db.KeyPersistAsync(SlotKey(machineId));
        await Assert.That(await db.KeyTimeToLiveAsync(SlotKey(machineId))).IsNull();

        StreamSlotSource? second = await svc.TryAcquireStreamSlotAsync(machineId, TimeSpan.FromSeconds(120));
        await Assert.That(second).IsEqualTo(StreamSlotSource.Redis);

        TimeSpan? ttl = await db.KeyTimeToLiveAsync(SlotKey(machineId));
        await Assert.That(ttl.HasValue).IsTrue();
        await Assert.That(ttl!.Value > TimeSpan.Zero).IsTrue();
    }
}
