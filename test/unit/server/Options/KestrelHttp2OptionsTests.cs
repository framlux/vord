// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Options;

namespace Framlux.FleetManagement.Test.Server.Options;

/// <summary>Tests the Kestrel HTTP/2 limit option defaults.</summary>
public class KestrelHttp2OptionsTests
{
    [Test]
    public async Task Defaults_AreConservativeAndNonZero()
    {
        KestrelHttp2Options opts = new();

        await Assert.That(opts.MaxStreamsPerConnection).IsEqualTo(100);
        await Assert.That(opts.MaxConcurrentConnections).IsEqualTo(20000L);
    }
}
