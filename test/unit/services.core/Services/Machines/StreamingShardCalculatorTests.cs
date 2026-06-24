// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Machines;

namespace Framlux.FleetManagement.Test.Services.Machines;

/// <summary>Tests for the pure projection shard-assignment math.</summary>
public class StreamingShardCalculatorTests
{
    [Test]
    public async Task OwnsMachine_PartitionsByModulo()
    {
        // Modulo partitioning: machineId 8 lands in shard 0 (8 % 4 == 0), and machineId 11 lands in
        // shard 3 (11 % 4 == 3) so shard 0 does not own it.
        await Assert.That(StreamingShardCalculator.OwnsMachine(machineId: 8, shardIndex: 0, shardCount: 4)).IsTrue();
        await Assert.That(StreamingShardCalculator.OwnsMachine(machineId: 11, shardIndex: 0, shardCount: 4)).IsFalse();
        await Assert.That(StreamingShardCalculator.OwnsMachine(machineId: 11, shardIndex: 3, shardCount: 4)).IsTrue();
    }

    [Test]
    public async Task OwnsMachine_NegativeMachineId_IsHandledStably()
    {
        bool any = false;
        for (int shard = 0; shard < 4; shard++)
        {
            if (StreamingShardCalculator.OwnsMachine(-7, shard, 4))
            {
                any = true;
            }
        }

        await Assert.That(any).IsTrue();
    }

    [Test]
    public async Task OwnsMachine_LongMinValue_DoesNotOverflow()
    {
        // Math.Abs(long.MinValue) would overflow; the implementation maps via the unsigned cast.
        // This pins that exact edge: no shard call throws, and exactly the bucket math holds so at
        // least one shard claims the machine.
        bool any = false;
        for (int shard = 0; shard < 4; shard++)
        {
            if (StreamingShardCalculator.OwnsMachine(long.MinValue, shard, 4))
            {
                any = true;
            }
        }

        await Assert.That(any).IsTrue();
    }

    [Test]
    public async Task LockNameForShard_IsStableAndDistinct()
    {
        string a = StreamingShardCalculator.LockNameForShard(0);
        string b = StreamingShardCalculator.LockNameForShard(1);
        await Assert.That(a).IsEqualTo("state-streaming:shard:0");
        await Assert.That(b).IsEqualTo("state-streaming:shard:1");
        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task SingleShard_OwnsEveryMachine()
    {
        await Assert.That(StreamingShardCalculator.OwnsMachine(123, 0, 1)).IsTrue();
        await Assert.That(StreamingShardCalculator.OwnsMachine(124, 0, 1)).IsTrue();
    }
}
