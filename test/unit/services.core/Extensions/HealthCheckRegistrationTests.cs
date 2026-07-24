// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Extensions;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Extensions;

/// <summary>
/// Guards the readiness-probe failure semantics registered by
/// <see cref="ServiceCollectionExtensions.AddCoreInfrastructure"/>: Postgres is a hard dependency,
/// while Redis is fail-open so a Redis blip degrades the fleet rather than evicting every pod.
/// </summary>
public class HealthCheckRegistrationTests
{
    private static HealthCheckServiceOptions BuildHealthCheckOptions()
    {
        ServiceCollection services = new();
        services.AddLogging();
        RedisOptions redisOpts = new() { ConnectionString = "localhost:6379" };
        services.AddCoreInfrastructure(redisOpts, "Host=localhost;Database=vord_test;Username=u;Password=p");

        using ServiceProvider provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
    }

    [Test]
    public async Task AddCoreInfrastructure_RegistersRedisHealthCheck_AsDegraded()
    {
        HealthCheckServiceOptions options = BuildHealthCheckOptions();

        HealthCheckRegistration redis = options.Registrations.Single(r => string.Equals(r.Name, "redis", StringComparison.Ordinal));

        await Assert.That(redis.FailureStatus).IsEqualTo(HealthStatus.Degraded);
    }

    [Test]
    public async Task AddCoreInfrastructure_RegistersPostgresHealthCheck_AsUnhealthy()
    {
        HealthCheckServiceOptions options = BuildHealthCheckOptions();

        HealthCheckRegistration postgres = options.Registrations.Single(r => string.Equals(r.Name, "postgresql", StringComparison.Ordinal));

        await Assert.That(postgres.FailureStatus).IsEqualTo(HealthStatus.Unhealthy);
    }
}
