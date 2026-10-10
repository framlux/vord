// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="HostsPausedAuditWriter"/>: a limit change that leaves hosts paused is
/// recorded with the count computed from the limit the caller just wrote.
/// </summary>
public sealed class HostsPausedAuditWriterTests
{
    private static async Task SeedMachinesAsync(TestDatabaseFactory db, int count, int tenantId = 1, bool isDeleted = false)
    {
        for (int i = 0; i < count; i++)
        {
            Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
            machine.IsDeleted = isDeleted;
            await db.Context.InsertWithInt64IdentityAsync(machine);
        }
    }

    private static (HostsPausedAuditWriter Writer, ISubscriptionService Subscriptions) Create(TestDatabaseFactory db, SubscriptionTier tier, int limit)
    {
        DatabaseRepository repo = new(db.Context, new NullLogger<DatabaseRepository>());
        ISubscriptionService subscriptions = Substitute.For<ISubscriptionService>();
        subscriptions.GetMachineLimitForTierAsync(Arg.Any<int>(), tier, Arg.Any<CancellationToken>()).Returns(limit);

        return (new HostsPausedAuditWriter(subscriptions, repo, repo), subscriptions);
    }

    private static async Task<List<AuditLogEntry>> HostsPausedRowsAsync(TestDatabaseFactory db, int tenantId = 1)
    {
        return await db.Context.AuditLog
            .Where(a => (a.TenantId == tenantId) && (a.Action == AuditAction.HostsPaused))
            .ToListAsync();
    }

    [Test]
    public async Task TenMachinesUnderALimitOfThree_WritesOneRowCountingSevenPaused()
    {
        using TestDatabaseFactory db = new();
        await SeedMachinesAsync(db, 10);
        (HostsPausedAuditWriter writer, _) = Create(db, SubscriptionTier.Free, limit: 3);

        await writer.WriteIfHostsPausedAsync(1, SubscriptionTier.Free, userId: null, CancellationToken.None);

        List<AuditLogEntry> rows = await HostsPausedRowsAsync(db);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Details).Contains("\"paused\":7");
        await Assert.That(rows[0].Details).Contains("\"limit\":3");
        await Assert.That(rows[0].Details).Contains("\"registered\":10");
        await Assert.That(rows[0].ResourceType).IsEqualTo(AuditResourceType.Subscription);
        await Assert.That(rows[0].ResourceId).IsEqualTo("1");
    }

    [Test]
    public async Task ThreeMachinesUnderALimitOfThree_WritesNothing()
    {
        using TestDatabaseFactory db = new();
        await SeedMachinesAsync(db, 3);
        (HostsPausedAuditWriter writer, _) = Create(db, SubscriptionTier.Free, limit: 3);

        await writer.WriteIfHostsPausedAsync(1, SubscriptionTier.Free, userId: null, CancellationToken.None);

        await Assert.That((await HostsPausedRowsAsync(db)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task ALimitOfZeroWithTwoMachines_PausesBoth()
    {
        using TestDatabaseFactory db = new();
        await SeedMachinesAsync(db, 2);
        (HostsPausedAuditWriter writer, _) = Create(db, SubscriptionTier.Free, limit: 0);

        await writer.WriteIfHostsPausedAsync(1, SubscriptionTier.Free, userId: null, CancellationToken.None);

        List<AuditLogEntry> rows = await HostsPausedRowsAsync(db);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Details).Contains("\"paused\":2");
    }

    [Test]
    public async Task AnUnlimitedTier_WritesNothing()
    {
        using TestDatabaseFactory db = new();
        await SeedMachinesAsync(db, 5);
        (HostsPausedAuditWriter writer, _) = Create(db, SubscriptionTier.Enterprise, limit: int.MaxValue);

        await writer.WriteIfHostsPausedAsync(1, SubscriptionTier.Enterprise, userId: null, CancellationToken.None);

        await Assert.That((await HostsPausedRowsAsync(db)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task DeletedMachinesAndOtherTenantsMachines_AreNotCounted()
    {
        // Only the tenant's own registered machines can be paused, so the count must ignore both.
        using TestDatabaseFactory db = new();
        await SeedMachinesAsync(db, 4);
        await SeedMachinesAsync(db, 6, isDeleted: true);
        await SeedMachinesAsync(db, 6, tenantId: 2);
        (HostsPausedAuditWriter writer, _) = Create(db, SubscriptionTier.Free, limit: 3);

        await writer.WriteIfHostsPausedAsync(1, SubscriptionTier.Free, userId: null, CancellationToken.None);

        List<AuditLogEntry> rows = await HostsPausedRowsAsync(db);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Details).Contains("\"paused\":1");
        await Assert.That(rows[0].Details).Contains("\"registered\":4");
    }

    [Test]
    public async Task TheLimitIsReadForTheTierTheCallerWrote_AndAttributedToTheCallingUser()
    {
        // The cached subscription may still describe the previous tier, so the limit must come from the
        // tier handed in rather than from the tenant's current effective limits.
        using TestDatabaseFactory db = new();
        await SeedMachinesAsync(db, 5);
        (HostsPausedAuditWriter writer, ISubscriptionService subscriptions) = Create(db, SubscriptionTier.Pro, limit: 2);

        await writer.WriteIfHostsPausedAsync(1, SubscriptionTier.Pro, userId: 42, CancellationToken.None);

        await subscriptions.Received(1).GetMachineLimitForTierAsync(1, SubscriptionTier.Pro, Arg.Any<CancellationToken>());
        await subscriptions.DidNotReceive().GetEffectiveLimitsForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        List<AuditLogEntry> rows = await HostsPausedRowsAsync(db);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].UserId).IsEqualTo(42);
    }

    [Test]
    public async Task Constructor_NullArguments_Throw()
    {
        using TestDatabaseFactory db = new();
        DatabaseRepository repo = new(db.Context, new NullLogger<DatabaseRepository>());
        ISubscriptionService subscriptions = Substitute.For<ISubscriptionService>();

        await Assert.That(() => new HostsPausedAuditWriter(null!, repo, repo)).Throws<ArgumentNullException>();
        await Assert.That(() => new HostsPausedAuditWriter(subscriptions, null!, repo)).Throws<ArgumentNullException>();
        await Assert.That(() => new HostsPausedAuditWriter(subscriptions, repo, null!)).Throws<ArgumentNullException>();
    }
}
