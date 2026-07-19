// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Machines;

namespace Framlux.FleetManagement.Test.Services.Machines;

/// <summary>Tests for the pure projection shard-assignment math.</summary>
public class StreamingShardCalculatorTests
{
    [Test]
    public async Task LockNameForShard_IsStableAndDistinct()
    {
        string a = StreamingShardCalculator.LockNameForShard(0);
        string b = StreamingShardCalculator.LockNameForShard(1);
        await Assert.That(a).IsEqualTo("state-streaming:shard:0");
        await Assert.That(b).IsEqualTo("state-streaming:shard:1");
        await Assert.That(a).IsNotEqualTo(b);
    }
}
