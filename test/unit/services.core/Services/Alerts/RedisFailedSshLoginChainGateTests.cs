// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Alerts;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Alerts;

/// <summary>
/// Tests the Redis lease that keeps one failed-SSH-login evaluation chain per machine, including the
/// contract that matters most to its callers: connectivity faults are propagated, never swallowed.
/// </summary>
public sealed class RedisFailedSshLoginChainGateTests
{
    private const long MachineId = 4242;

    private static (RedisFailedSshLoginChainGate Gate, IDatabase Db) Create()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        return (new RedisFailedSshLoginChainGate(redis), db);
    }

    [Test]
    public async Task TryOpenChainAsync_WhenTheLeaseIsFree_ReturnsAFencingToken()
    {
        (RedisFailedSshLoginChainGate gate, IDatabase db) = Create();
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>()).Returns(true);

        string? token = await gate.TryOpenChainAsync(MachineId);

        await Assert.That(token).IsNotNull();
        await Assert.That(token!.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// The lease is taken with a set-if-absent, which is what makes two replicas racing the same
    /// machine produce one chain rather than two.
    /// </summary>
    [Test]
    public async Task TryOpenChainAsync_TakesTheLeaseOnlyWhenAbsentAndKeysItPerMachine()
    {
        (RedisFailedSshLoginChainGate gate, IDatabase db) = Create();
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>()).Returns(true);

        await gate.TryOpenChainAsync(MachineId);

        await db.Received(1).StringSetAsync(
            Arg.Is<RedisKey>(key => key.ToString() == $"{AlertConstants.FailedSshLoginChainKeyPrefix}:{MachineId}"),
            Arg.Any<RedisValue>(),
            AlertConstants.FailedSshLoginChainLease,
            When.NotExists);
    }

    [Test]
    public async Task TryOpenChainAsync_WhenAChainIsAlreadyLive_ReturnsNull()
    {
        (RedisFailedSshLoginChainGate gate, IDatabase db) = Create();
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>()).Returns(false);

        string? token = await gate.TryOpenChainAsync(MachineId);

        await Assert.That(token).IsNull();
    }

    [Test]
    [Arguments(1L, true)]
    [Arguments(0L, false)]
    public async Task RenewChainAsync_ReportsWhetherThisChainStillHoldsTheLease(long scriptResult, bool expected)
    {
        (RedisFailedSshLoginChainGate gate, IDatabase db) = Create();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)scriptResult));

        bool renewed = await gate.RenewChainAsync(MachineId, "token-a");

        await Assert.That(renewed).IsEqualTo(expected);
    }

    [Test]
    public async Task RenewChainAsync_PassesTheFencingTokenAndLeaseLength()
    {
        (RedisFailedSshLoginChainGate gate, IDatabase db) = Create();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        await gate.RenewChainAsync(MachineId, "token-a");

        await db.Received(1).ScriptEvaluateAsync(
            RedisFailedSshLoginChainGate.RenewIfOwnedScript,
            Arg.Is<RedisKey[]>(keys => keys[0].ToString() == $"{AlertConstants.FailedSshLoginChainKeyPrefix}:{MachineId}"),
            Arg.Is<RedisValue[]>(values => (values[0] == "token-a")
                && (values[1] == (int)AlertConstants.FailedSshLoginChainLease.TotalSeconds)),
            Arg.Any<CommandFlags>());
    }

    /// <summary>
    /// A superseded chain must not clear the lease of the chain that replaced it, so the delete is
    /// conditional on ownership inside the script rather than in the caller.
    /// </summary>
    [Test]
    public async Task ReleaseChainAsync_DeletesOnlyWhenTheTokenStillOwnsTheLease()
    {
        (RedisFailedSshLoginChainGate gate, IDatabase db) = Create();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        await gate.ReleaseChainAsync(MachineId, "token-a");

        await db.Received(1).ScriptEvaluateAsync(
            RedisFailedSshLoginChainGate.ReleaseIfOwnedScript,
            Arg.Is<RedisKey[]>(keys => keys[0].ToString() == $"{AlertConstants.FailedSshLoginChainKeyPrefix}:{MachineId}"),
            Arg.Is<RedisValue[]>(values => values[0] == "token-a"),
            Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    /// <summary>
    /// The contract the callers depend on: an outage must be distinguishable from "a chain is already
    /// running". Swallowing it here would make every caller's fail-open branch unreachable and stop
    /// anything being scheduled at all while Redis was down.
    /// </summary>
    [Test]
    public async Task TryOpenChainAsync_WhenRedisIsUnreachable_PropagatesRatherThanReportingAChain()
    {
        (RedisFailedSshLoginChainGate gate, IDatabase db) = Create();
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns<bool>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Connection refused"));

        await Assert.That(async () => await gate.TryOpenChainAsync(MachineId)).Throws<RedisConnectionException>();
    }

    [Test]
    public async Task RenewChainAsync_WhenRedisTimesOut_Propagates()
    {
        (RedisFailedSshLoginChainGate gate, IDatabase db) = Create();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns<RedisResult>(_ => throw new RedisTimeoutException("timeout", CommandStatus.WaitingInBacklog));

        await Assert.That(async () => await gate.RenewChainAsync(MachineId, "token-a")).Throws<RedisTimeoutException>();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task RenewChainAsync_WithNoToken_Throws(string? token)
    {
        (RedisFailedSshLoginChainGate gate, IDatabase _) = Create();

        await Assert.That(async () => await gate.RenewChainAsync(MachineId, token!)).ThrowsException();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task ReleaseChainAsync_WithNoToken_Throws(string? token)
    {
        (RedisFailedSshLoginChainGate gate, IDatabase _) = Create();

        await Assert.That(async () => await gate.ReleaseChainAsync(MachineId, token!)).ThrowsException();
    }
}
