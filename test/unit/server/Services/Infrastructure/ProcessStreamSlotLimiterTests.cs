// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Test.Services.Infrastructure;

/// <summary>Tests the per-process stream-slot fallback used during a Redis outage.</summary>
public class ProcessStreamSlotLimiterTests
{
    [Test]
    public async Task TryAcquire_UpToCap_Succeeds_ThenRejects()
    {
        ProcessStreamSlotLimiter limiter = new(maxPerProcess: 2);

        await Assert.That(limiter.TryAcquire()).IsTrue();
        await Assert.That(limiter.TryAcquire()).IsTrue();
        await Assert.That(limiter.TryAcquire()).IsFalse();
    }

    [Test]
    public async Task Release_FreesASlot()
    {
        ProcessStreamSlotLimiter limiter = new(maxPerProcess: 1);
        await Assert.That(limiter.TryAcquire()).IsTrue();
        await Assert.That(limiter.TryAcquire()).IsFalse();

        limiter.Release();

        await Assert.That(limiter.TryAcquire()).IsTrue();
    }

    [Test]
    public async Task Release_BelowZero_DoesNotThrowOrExceedCap()
    {
        ProcessStreamSlotLimiter limiter = new(maxPerProcess: 1);

        limiter.Release(); // spurious release must not raise the ceiling

        await Assert.That(limiter.TryAcquire()).IsTrue();
        await Assert.That(limiter.TryAcquire()).IsFalse();
    }
}
