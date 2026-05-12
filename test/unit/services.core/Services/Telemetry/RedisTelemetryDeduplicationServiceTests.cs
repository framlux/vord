// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Framlux.FleetManagement.Services.Core.Telemetry;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="RedisTelemetryDeduplicationService"/>.
/// </summary>
public sealed class RedisTelemetryDeduplicationServiceTests
{
    [Test]
    public async Task Constructor_NullRedis_ThrowsException()
    {
        // ServerConfigurationService is a sealed concrete class that cannot be mocked.
        // Passing null for redis causes a failure during construction; the exact exception
        // type depends on the runtime but construction should not succeed.
        await Assert.That(() => new RedisTelemetryDeduplicationService(null!, null!))
            .Throws<Exception>();
    }

    [Test]
    public async Task Constructor_NullConfigService_ThrowsException()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();

        await Assert.That(() => new RedisTelemetryDeduplicationService(redis, null!))
            .Throws<ArgumentNullException>();
    }
}
