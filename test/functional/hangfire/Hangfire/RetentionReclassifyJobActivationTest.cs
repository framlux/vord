// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.FunctionalTest.Hangfire;

/// <summary>
/// Proves the reclassification job can actually be activated out of the production DI graph, and that
/// the repository it receives is the uncached, database-backed one rather than the Redis caching
/// decorator. The job resolves its subscription repository through a keyed registration, which is only
/// honoured if the job type itself is registered for constructor injection — a wiring detail that no
/// unit test can catch because unit tests construct the job by hand.
/// </summary>
public sealed class RetentionReclassifyJobActivationTest
{
    [Test]
    public async Task RetentionReclassifyJob_ResolvesFromDi_WithTheUncachedSubscriptionRepository()
    {
        using FunctionalTestFactory factory = new();

        using IServiceScope scope = factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();

        RetentionReclassifyJob job = scope.ServiceProvider.GetRequiredService<RetentionReclassifyJob>();
        await Assert.That(job).IsNotNull();

        // The keyed registration the job's constructor asks for must resolve, and it must NOT be the
        // caching decorator: the cache can hold a pre-change tier past the commit, which would make the
        // job compute the old retention class and silently move nothing.
        ISubscriptionRepository uncached = scope.ServiceProvider.GetRequiredKeyedService<ISubscriptionRepository>(
            RetentionReclassifyJob.UncachedRepositoryKey);
        ISubscriptionRepository cached = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();

        await Assert.That(uncached).IsNotNull();
        await Assert.That(uncached.GetType()).IsNotEqualTo(cached.GetType());
    }
}
