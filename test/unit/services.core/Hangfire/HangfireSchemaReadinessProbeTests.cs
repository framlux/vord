// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Hangfire;

namespace Framlux.FleetManagement.Test.Hangfire;

/// <summary>
/// H9 tests: <see cref="HangfireSchemaReadinessProbe"/> times out cleanly when the schema is
/// not reachable and validates its inputs. The happy-path against a real Postgres lives in
/// the integration test project, not here.
/// </summary>
public sealed class HangfireSchemaReadinessProbeTests
{
    [Test]
    public async Task WaitForHangfireSchema_NullConnectionString_ThrowsArgumentException()
    {
        ArgumentException? ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            HangfireSchemaReadinessProbe.WaitForHangfireSchemaAsync(null!, TimeSpan.FromSeconds(1)));

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task WaitForHangfireSchema_WhitespaceConnectionString_ThrowsArgumentException()
    {
        ArgumentException? ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            HangfireSchemaReadinessProbe.WaitForHangfireSchemaAsync("   ", TimeSpan.FromSeconds(1)));

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task WaitForHangfireSchema_UnreachableHost_TimesOutCleanly()
    {
        // Use a connection string pointing at a non-routable port. Connection-open will fail;
        // because the SqlState filter only swallows 42P01/3F000, anything else (including the
        // socket error) surfaces immediately — meaning the probe does NOT silently wait the
        // full timeout for unrelated failures.
        const string unreachable = "Host=127.0.0.1;Port=1;Database=postgres;Username=u;Password=p;Timeout=1";

        // Any non-PostgresException error is surfaced immediately by design.
        Exception? ex = null;
        try
        {
            await HangfireSchemaReadinessProbe.WaitForHangfireSchemaAsync(
                unreachable,
                TimeSpan.FromSeconds(2),
                pollInterval: TimeSpan.FromMilliseconds(100));
        }
        catch (Exception caught)
        {
            ex = caught;
        }

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task WaitForHangfireSchema_CancellationRequested_Throws()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        OperationCanceledException? ex = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            HangfireSchemaReadinessProbe.WaitForHangfireSchemaAsync(
                "Host=127.0.0.1;Port=5432;Database=x;Username=u;Password=p",
                TimeSpan.FromSeconds(60),
                pollInterval: TimeSpan.FromMilliseconds(100),
                ct: cts.Token));

        await Assert.That(ex).IsNotNull();
    }
}
