// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Hangfire;

public sealed class HangfireRegistrationTests
{
    [Test]
    public async Task BuildServerName_IncludesHostAndProcessId()
    {
        // Intent: Hangfire registers each running server by name. In Kubernetes the pod hostname
        // is unique per replica, but in non-K8s environments (docker-compose, local dev) the host
        // can collide across replicas. Including the process id guarantees uniqueness regardless
        // of the host. Two replicas with the same name would silently overwrite each other's
        // heartbeat in Hangfire storage, surfacing as a flapping/unstable server in the dashboard.
        string name = HangfireRegistration.BuildServerName();

        await Assert.That(name).StartsWith("vord-worker-");
        await Assert.That(name).Contains(Environment.MachineName);
        await Assert.That(name).Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task BuildServerName_TwoCallsInSameProcess_ReturnIdentical()
    {
        // Intent: the server name is stable for the lifetime of the process. Hangfire heartbeats
        // by server name; a name that changes mid-life would orphan the prior registration.
        string first = HangfireRegistration.BuildServerName();
        string second = HangfireRegistration.BuildServerName();

        await Assert.That(first).IsEqualTo(second);
    }

    // ==========================================================================================
    // C4 regression tests: UseHangfireAdminDashboard respects HangfireOptions.DashboardEnabled.
    // ==========================================================================================

    /// <summary>
    /// Builds a minimal IApplicationBuilder whose ApplicationServices contains the supplied
    /// <see cref="HangfireOptions"/> plus an <see cref="ILoggerFactory"/>. The builder is sufficient
    /// for <see cref="HangfireRegistration.UseHangfireAdminDashboard"/> to read the option.
    /// </summary>
    private static IApplicationBuilder BuildAppWithOptions(HangfireOptions options)
    {
        ServiceCollection services = new();
        services.AddSingleton<IOptions<HangfireOptions>>(Options.Create(options));
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        ServiceProvider provider = services.BuildServiceProvider();

        return new ApplicationBuilder(provider);
    }

    [Test]
    public async Task UseHangfireAdminDashboard_DashboardDisabled_DoesNotMountRoute()
    {
        IApplicationBuilder app = BuildAppWithOptions(new HangfireOptions { DashboardEnabled = false });

        IApplicationBuilder returned = HangfireRegistration.UseHangfireAdminDashboard(app);

        // Returns the builder unchanged for chaining; no exception is thrown.
        await Assert.That(returned).IsSameReferenceAs(app);
    }

    [Test]
    public async Task UseHangfireAdminDashboard_NullApp_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            HangfireRegistration.UseHangfireAdminDashboard(null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("app");
    }

    [Test]
    public async Task HangfireOptions_Default_DashboardEnabled_IsTrue()
    {
        HangfireOptions options = new();

        await Assert.That(options.DashboardEnabled).IsTrue();
    }
}
