// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Test.Infrastructure;
using Hangfire;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Infrastructure;

/// <summary>
/// Tests for <see cref="RetentionReclassifyJob"/>: the day-chunk enumeration that bounds the move to
/// the tenant's new effective window, the target-class resolution that happens at execution time (so
/// a rapid double-change converges on the latest subscription state), and the per-chunk repository
/// calls the job issues.
/// </summary>
public sealed class RetentionReclassifyJobTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 22, 15, 30, 0, TimeSpan.Zero);

    private static RetentionReclassifyJob CreateJob(
        ISubscriptionRepository subscriptionRepository,
        IMachineStateRepository machineStateRepository,
        DateTimeOffset? now = null)
    {
        return new RetentionReclassifyJob(
            subscriptionRepository,
            machineStateRepository,
            new FixedTimeProvider(now ?? FixedNow),
            NullLogger<RetentionReclassifyJob>.Instance);
    }

    [Test]
    public async Task BuildDayChunks_NewestFirst_AndBoundedByTheNewWindow()
    {
        // Intent: the move is chunked one day at a time, newest day first, and the oldest chunk starts
        // exactly at the new effective window's start — never earlier.
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> chunks =
            RetentionReclassifyJob.BuildDayChunks(FixedNow, retentionDays: 3);

        await Assert.That(chunks.Count).IsGreaterThan(0);
        await Assert.That(chunks[0].End).IsEqualTo(FixedNow.AddDays(RetentionReclassifyJob.FutureWindowDays));
        await Assert.That(chunks[^1].Start).IsEqualTo(FixedNow.AddDays(-3));

        for (int index = 1; index < chunks.Count; index++)
        {
            // Newest first: each chunk starts strictly before the previous one.
            await Assert.That(chunks[index].Start < chunks[index - 1].Start).IsTrue();

            // Contiguous: no gap between adjacent chunks.
            await Assert.That(chunks[index].End).IsEqualTo(chunks[index - 1].Start);
        }
    }

    [Test]
    public async Task BuildDayChunks_SplitsOnUtcDayBoundaries()
    {
        // Intent: chunk bounds fall on UTC midnight (except the clipped window edges) so each UPDATE
        // targets exactly one daily leaf partition of the target class.
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> chunks =
            RetentionReclassifyJob.BuildDayChunks(FixedNow, retentionDays: 2);

        for (int index = 1; index < chunks.Count - 1; index++)
        {
            await Assert.That(chunks[index].Start.TimeOfDay).IsEqualTo(TimeSpan.Zero);
            await Assert.That(chunks[index].End.TimeOfDay).IsEqualTo(TimeSpan.Zero);
            await Assert.That(chunks[index].End - chunks[index].Start).IsEqualTo(TimeSpan.FromDays(1));
        }
    }

    [Test]
    public async Task BuildDayChunks_ExcludesEverythingOlderThanTheNewWindow()
    {
        // Intent: rows older than the new effective window are never in a chunk's bounds. This pins the
        // owner-approved downgrade exception — those rows stay in their old class and expire on the old
        // schedule rather than being moved into a class whose window already passed them by.
        DateTimeOffset windowStart = FixedNow.AddDays(-60);
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> chunks =
            RetentionReclassifyJob.BuildDayChunks(FixedNow, retentionDays: 60);

        foreach ((DateTimeOffset start, DateTimeOffset end) in chunks)
        {
            await Assert.That(start >= windowStart).IsTrue();
            await Assert.That(end > start).IsTrue();
        }
    }

    [Test]
    public async Task BuildDayChunks_NonPositiveRetention_ClampsToTheShortestWindow()
    {
        // Intent: a zero or negative retention (a deny-all override, or a resolution glitch) must not
        // produce an empty or inverted window; it clamps to the one-day Short window.
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> chunks =
            RetentionReclassifyJob.BuildDayChunks(FixedNow, retentionDays: 0);

        await Assert.That(chunks[^1].Start).IsEqualTo(FixedNow.AddDays(-RetentionClassPolicy.ShortWindowDays));
    }

    [Test]
    public async Task RunAsync_ResolvesTargetClassAtExecutionTime()
    {
        // Intent: the target class is read from the tenant's CURRENT effective retention when the job
        // runs, not captured at enqueue. A tenant upgraded to a 60-day plan lands in Medium.
        ISubscriptionRepository subscriptions = Substitute.For<ISubscriptionRepository>();
        subscriptions.GetEffectiveRetentionDaysAsync(7, Arg.Any<CancellationToken>()).Returns(60);
        IMachineStateRepository machineState = Substitute.For<IMachineStateRepository>();

        await CreateJob(subscriptions, machineState).RunAsync(7, CancellationToken.None);

        await machineState.Received().ReclassifyTelemetryForTenantAsync(
            7,
            RetentionClass.Medium,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await machineState.DidNotReceive().ReclassifyTelemetryForTenantAsync(
            Arg.Any<int>(),
            RetentionClass.Short,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_RapidDoubleChange_ConvergesOnTheLatestSubscription()
    {
        // Intent: two changes in quick succession enqueue two jobs; each resolves the effective
        // retention afresh, so the run that executes after the second change uses the second target.
        ISubscriptionRepository subscriptions = Substitute.For<ISubscriptionRepository>();
        subscriptions.GetEffectiveRetentionDaysAsync(7, Arg.Any<CancellationToken>()).Returns(60, 365);
        IMachineStateRepository machineState = Substitute.For<IMachineStateRepository>();
        RetentionReclassifyJob job = CreateJob(subscriptions, machineState);

        await job.RunAsync(7, CancellationToken.None);
        await job.RunAsync(7, CancellationToken.None);

        await machineState.Received().ReclassifyTelemetryForTenantAsync(
            7, RetentionClass.Medium, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await machineState.Received().ReclassifyTelemetryForTenantAsync(
            7, RetentionClass.Long, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_ReadsCommittedRetention_NotAStaleCachedEntry()
    {
        // Intent: the subscription cache is invalidated BEFORE the plan change commits, so a concurrent
        // read can re-seed it with the pre-change tier and hold that for the cache TTL. If the job read
        // through that cache it would compute the old class, move nothing, and never be re-enqueued —
        // permanent misclassification. The job therefore takes the uncached, database-backed repository.
        ISubscriptionRepository database = Substitute.For<ISubscriptionRepository>();
        database.GetEffectiveRetentionDaysAsync(7, Arg.Any<CancellationToken>()).Returns(1);
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        CachingSubscriptionRepository cached = new(
            database,
            redis,
            Options.Create(new RedisOptions { ConnectionString = "localhost", SubscriptionCacheTtlSeconds = 30 }),
            new RetentionReclassifyDispatcher(
                Substitute.For<IBackgroundJobClient>(), NullLogger<RetentionReclassifyDispatcher>.Instance));

        // A concurrent reader seeds the cache with the pre-change value…
        await cached.GetEffectiveRetentionDaysAsync(7, CancellationToken.None);

        // …and only then does the plan change become visible in the database.
        database.GetEffectiveRetentionDaysAsync(7, Arg.Any<CancellationToken>()).Returns(60);
        await Assert.That(await cached.GetEffectiveRetentionDaysAsync(7, CancellationToken.None)).IsEqualTo(1);

        IMachineStateRepository machineState = Substitute.For<IMachineStateRepository>();
        await CreateJob(database, machineState).RunAsync(7, CancellationToken.None);

        // The job used the committed 60-day retention (Medium), not the stale cached 1 day (Short).
        await machineState.Received().ReclassifyTelemetryForTenantAsync(
            7, RetentionClass.Medium, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await machineState.DidNotReceive().ReclassifyTelemetryForTenantAsync(
            Arg.Any<int>(), RetentionClass.Short, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_RetentionChangesMidRun_RepeatsAndEndsOnTheLatestValue()
    {
        // Intent: a plan change that commits while the chunk loop is running would otherwise leave the
        // tenant on the target the run started with. The post-loop convergence re-check catches it and
        // runs again on the new value.
        ISubscriptionRepository subscriptions = Substitute.For<ISubscriptionRepository>();
        subscriptions.GetEffectiveRetentionDaysAsync(7, Arg.Any<CancellationToken>()).Returns(1, 60, 60);
        IMachineStateRepository machineState = Substitute.For<IMachineStateRepository>();

        await CreateJob(subscriptions, machineState).RunAsync(7, CancellationToken.None);

        // First pass ran on the stale-at-the-time Short target; the re-check drove a second pass that
        // converged the tenant on Medium.
        await machineState.Received().ReclassifyTelemetryForTenantAsync(
            7, RetentionClass.Short, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await machineState.Received().ReclassifyTelemetryForTenantAsync(
            7, RetentionClass.Medium, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_RetentionChangesOnEveryPass_StopsAtTheConvergenceBound()
    {
        // Intent: a pathological change storm must not pin a worker. The run stops after the bound;
        // the change that outran it enqueued its own job.
        int everChanging = 100;
        ISubscriptionRepository subscriptions = Substitute.For<ISubscriptionRepository>();
        subscriptions.GetEffectiveRetentionDaysAsync(7, Arg.Any<CancellationToken>())
            .Returns(_ => everChanging++);
        IMachineStateRepository machineState = Substitute.For<IMachineStateRepository>();

        await CreateJob(subscriptions, machineState).RunAsync(7, CancellationToken.None);

        // One read to seed plus one re-check per pass, bounded by MaxConvergencePasses.
        await subscriptions.Received(RetentionReclassifyJob.MaxConvergencePasses + 1)
            .GetEffectiveRetentionDaysAsync(7, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_IssuesOneUpdatePerDayChunk()
    {
        ISubscriptionRepository subscriptions = Substitute.For<ISubscriptionRepository>();
        subscriptions.GetEffectiveRetentionDaysAsync(7, Arg.Any<CancellationToken>()).Returns(3);
        IMachineStateRepository machineState = Substitute.For<IMachineStateRepository>();

        await CreateJob(subscriptions, machineState).RunAsync(7, CancellationToken.None);

        int expected = RetentionReclassifyJob.BuildDayChunks(FixedNow, retentionDays: 3).Count;
        await machineState.Received(expected).ReclassifyTelemetryForTenantAsync(
            7, RetentionClass.Medium, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_NoRowsMoved_CompletesWithoutError()
    {
        // Intent: re-running against an already-converged tenant is a no-op — the repository's
        // "class differs from target" guard reports zero rows and the job simply finishes.
        ISubscriptionRepository subscriptions = Substitute.For<ISubscriptionRepository>();
        subscriptions.GetEffectiveRetentionDaysAsync(7, Arg.Any<CancellationToken>()).Returns(1);
        IMachineStateRepository machineState = Substitute.For<IMachineStateRepository>();
        machineState.ReclassifyTelemetryForTenantAsync(
            Arg.Any<int>(), Arg.Any<RetentionClass>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(0);

        await CreateJob(subscriptions, machineState).RunAsync(7, CancellationToken.None);

        await machineState.Received().ReclassifyTelemetryForTenantAsync(
            7, RetentionClass.Short, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_NonPositiveTenantId_Throws()
    {
        ISubscriptionRepository subscriptions = Substitute.For<ISubscriptionRepository>();
        IMachineStateRepository machineState = Substitute.For<IMachineStateRepository>();

        await Assert.That(async () => await CreateJob(subscriptions, machineState).RunAsync(0, CancellationToken.None))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Constructor_NullSubscriptionRepository_Throws()
    {
        await Assert.That(() => new RetentionReclassifyJob(
            null!,
            Substitute.For<IMachineStateRepository>(),
            new FixedTimeProvider(FixedNow),
            NullLogger<RetentionReclassifyJob>.Instance))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullMachineStateRepository_Throws()
    {
        await Assert.That(() => new RetentionReclassifyJob(
            Substitute.For<ISubscriptionRepository>(),
            null!,
            new FixedTimeProvider(FixedNow),
            NullLogger<RetentionReclassifyJob>.Instance))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullTimeProvider_Throws()
    {
        await Assert.That(() => new RetentionReclassifyJob(
            Substitute.For<ISubscriptionRepository>(),
            Substitute.For<IMachineStateRepository>(),
            null!,
            NullLogger<RetentionReclassifyJob>.Instance))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        await Assert.That(() => new RetentionReclassifyJob(
            Substitute.For<ISubscriptionRepository>(),
            Substitute.For<IMachineStateRepository>(),
            new FixedTimeProvider(FixedNow),
            null!))
            .Throws<ArgumentNullException>();
    }
}
