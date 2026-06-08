// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Test.Infrastructure;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.FunctionalTest.Hangfire;

/// <summary>
/// End-to-end smoke test for the Hangfire wiring. Enqueues a trivial job through the
/// real <see cref="IBackgroundJobClient"/> and polls a shared sink to confirm the job
/// was actually executed by an in-process Hangfire processing server. This single test
/// catches whole categories of wiring regressions (broken storage, missing activator,
/// DI scope misconfiguration, serializer breakage, server not started).
/// </summary>
public sealed class HangfireSmokeTest
{
    [Test]
    public async Task EnqueueTrivialJob_ItRunsAndUpdatesSharedSink()
    {
        // Intent: prove that the Hangfire client -> storage -> server -> activator -> DI scope
        // path is wired end-to-end. A failing wiring (broken JobActivator, missing scope,
        // wrong storage backend) shows up here, not at first production deploy.
        using FunctionalTestFactory factory = new();
        SmokeSink sink = new();

        factory.EnableHangfireProcessingServer = true;
        factory.AdditionalTestServices = services =>
        {
            services.AddSingleton(sink);
            services.AddScoped<SmokeJob>();
        };

        // Touching Services forces the host to build with the overrides above applied.
        IBackgroundJobClient client = factory.Services.GetRequiredService<IBackgroundJobClient>();
        client.Enqueue<SmokeJob>(j => j.RunAsync(CancellationToken.None));

        // Poll for up to 10 seconds. The in-memory storage drains the enqueued state
        // almost immediately, but CI machines can be slow so we give plenty of headroom.
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while ((sink.RanAt is null) && (DateTimeOffset.UtcNow < deadline))
        {
            await Task.Delay(50);
        }

        await Assert.That(sink.RanAt).IsNotNull();
    }
}
